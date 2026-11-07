using OpenCvSharp;
using OpenCvSharp.Dnn;
using ReelsMagician.Core.Diagnostics;

namespace ReelsMagician.Core.Framing;

public enum SubjectKind { None, Face, Person, Salient }

/// <summary>
/// Where the interesting thing is in one sampled frame. Coordinates are normalised to 0..1 of
/// the frame, so they survive the downscale used for analysis.
/// </summary>
public sealed record SubjectHit(
    double Time,
    double CenterX,
    double CenterY,
    double Width,
    double Height,
    double Confidence,
    SubjectKind Kind)
{
    public double Area => Width * Height;
    public static SubjectHit Unknown(double time) => new(time, 0.5, 0.5, 0, 0, 0, SubjectKind.None);
}

/// <summary>
/// Finds the frame's main subject in three tiers, in the order a human editor would frame the shot:
///
///   1. a face — the foreground speaker, chosen by apparent size with hysteresis so the crop
///      does not flip between two people who are both on screen;
///   2. a full body, when the face is turned away or too small to detect;
///   3. otherwise the visually dominant region — edge detail plus motion — which is what keeps
///      a product, a screen share or a landscape centred when no human is present.
/// </summary>
public sealed class SubjectDetector : IDisposable
{
    // Input geometry and channel means baked into the ResNet-10 SSD face detector.
    private static readonly Size FaceInput = new(300, 300);
    private static readonly Scalar FaceMean = new(104, 177, 123);
    private const float FaceConfidenceThreshold = 0.55f;

    private readonly Net? _faceNet;
    private readonly HOGDescriptor? _personDetector;
    private readonly IProgressSink _sink;

    private Mat? _previousGray;
    private SubjectHit? _previous;

    public SubjectDetector(string? faceProtoPath, string? faceWeightsPath, IProgressSink sink, bool detectPeople = true)
    {
        _sink = sink;

        if (File.Exists(faceProtoPath ?? "") && File.Exists(faceWeightsPath ?? ""))
        {
            try
            {
                _faceNet = CvDnn.ReadNetFromCaffe(faceProtoPath!, faceWeightsPath!);
            }
            catch (Exception ex)
            {
                sink.Warn("framing", $"Face detector unavailable ({ex.Message}); falling back to body and saliency.");
            }
        }
        else
        {
            sink.Warn("framing", "No face model installed — framing will use body detection and saliency only.");
        }

        if (detectPeople)
        {
            try
            {
                _personDetector = new HOGDescriptor();
                _personDetector.SetSVMDetector(HOGDescriptor.GetDefaultPeopleDetector());
            }
            catch (Exception ex)
            {
                sink.Debug("framing", $"Person detector unavailable: {ex.Message}");
                _personDetector = null;
            }
        }
    }

    /// <summary>Resets temporal state between clips so one clip cannot bias the next.</summary>
    public void BeginClip()
    {
        _previousGray?.Dispose();
        _previousGray = null;
        _previous = null;
    }

    public SubjectHit Analyze(string framePath, double time)
    {
        using var frame = Cv2.ImRead(framePath, ImreadModes.Color);
        return frame.Empty() ? SubjectHit.Unknown(time) : Analyze(frame, time);
    }

    public SubjectHit Analyze(Mat frame, double time)
    {
        var hit = DetectFace(frame, time)
               ?? DetectPerson(frame, time)
               ?? DetectSalient(frame, time);
        _previous = hit;
        return hit;
    }

    private SubjectHit? DetectFace(Mat frame, double time)
    {
        if (_faceNet is null) return null;
        try
        {
            using var blob = CvDnn.BlobFromImage(frame, 1.0, FaceInput, FaceMean, swapRB: false, crop: false);
            _faceNet.SetInput(blob);
            using var output = _faceNet.Forward();

            // The SSD head emits 1×1×N×7 rows of [batch, class, confidence, x1, y1, x2, y2],
            // with the box corners already normalised to the input image.
            using var detections = output.Reshape(1, output.Size(2));

            var best = (Score: double.MinValue, Row: -1);
            var rows = detections.Rows;
            for (var i = 0; i < rows; i++)
            {
                var confidence = detections.At<float>(i, 2);
                if (confidence < FaceConfidenceThreshold) continue;

                var x1 = detections.At<float>(i, 3);
                var y1 = detections.At<float>(i, 4);
                var x2 = detections.At<float>(i, 5);
                var y2 = detections.At<float>(i, 6);
                var w = x2 - x1;
                var h = y2 - y1;
                if (w <= 0.005f || h <= 0.005f) continue;

                var cx = x1 + w / 2;

                // A nearer face is a bigger face, and that is the foreground subject; the
                // continuity bonus stops the crop ping-ponging between two similar faces.
                var score = Math.Sqrt(w * h) * 2 + confidence;
                if (_previous is { Kind: SubjectKind.Face } prev)
                    score += 0.6 * (1 - Math.Min(1, Math.Abs(cx - prev.CenterX) * 3));

                if (score > best.Score) best = (score, i);
            }
            if (best.Row < 0) return null;

            var bx1 = detections.At<float>(best.Row, 3);
            var by1 = detections.At<float>(best.Row, 4);
            var bx2 = detections.At<float>(best.Row, 5);
            var by2 = detections.At<float>(best.Row, 6);
            var bw = bx2 - bx1;
            var bh = by2 - by1;

            // Aim below the face so the crop holds head and shoulders, not a floating head.
            var centerY = by1 + bh * 1.35;

            return new SubjectHit(
                time,
                Math.Clamp(bx1 + bw / 2, 0, 1),
                Math.Clamp(centerY, 0, 1),
                Math.Clamp(bw, 0, 1),
                Math.Clamp(bh, 0, 1),
                Math.Clamp(detections.At<float>(best.Row, 2), 0, 1),
                SubjectKind.Face);
        }
        catch (Exception ex)
        {
            _sink.Debug("framing", $"Face detection failed at {time:0.0}s: {ex.Message}");
            return null;
        }
    }

    private SubjectHit? DetectPerson(Mat frame, double time)
    {
        if (_personDetector is null) return null;
        try
        {
            // HOG is happiest on a small image; 320 px wide is plenty at this scale.
            using var small = new Mat();
            var scale = 320.0 / Math.Max(1, frame.Width);
            if (scale < 1) Cv2.Resize(frame, small, new Size(320, (int)(frame.Height * scale)));
            else frame.CopyTo(small);
            if (small.Height < 128) return null;

            var found = _personDetector.DetectMultiScale(
                small, out var weights, hitThreshold: 0, winStride: new Size(8, 8),
                padding: new Size(16, 16), scale: 1.06, groupThreshold: 2);
            if (found.Length == 0) return null;

            var bestIndex = 0;
            for (var i = 1; i < found.Length; i++)
                if (found[i].Width * found[i].Height > found[bestIndex].Width * found[bestIndex].Height)
                    bestIndex = i;

            var r = found[bestIndex];
            var confidence = weights.Length > bestIndex ? Math.Clamp(weights[bestIndex] / 2.0, 0, 1) : 0.5;
            if (confidence < 0.15) return null;

            return new SubjectHit(
                time,
                Math.Clamp((r.X + r.Width / 2.0) / small.Width, 0, 1),
                // Frame the upper body rather than the legs.
                Math.Clamp((r.Y + r.Height * 0.35) / small.Height, 0, 1),
                r.Width / (double)small.Width,
                r.Height / (double)small.Height,
                confidence,
                SubjectKind.Person);
        }
        catch (Exception ex)
        {
            _sink.Debug("framing", $"Person detection failed at {time:0.0}s: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// No human in the frame: fall back to what the eye actually lands on — a blend of edge
    /// detail and inter-frame motion, reduced to a centre of mass.
    /// </summary>
    private SubjectHit DetectSalient(Mat frame, double time)
    {
        try
        {
            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.GaussianBlur(gray, gray, new Size(5, 5), 0);

            using var gx = new Mat();
            using var gy = new Mat();
            Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0, 3);
            Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1, 3);

            using var energy = new Mat();
            Cv2.Magnitude(gx, gy, energy);

            if (_previousGray is not null &&
                _previousGray.Width == gray.Width && _previousGray.Height == gray.Height)
            {
                using var diff = new Mat();
                Cv2.Absdiff(gray, _previousGray, diff);
                using var diff32 = new Mat();
                diff.ConvertTo(diff32, MatType.CV_32F);
                // Motion is the stronger cue for "what matters", so it outweighs static detail.
                Cv2.AddWeighted(energy, 1.0, diff32, 3.0, 0, energy);
            }

            _previousGray?.Dispose();
            _previousGray = gray.Clone();

            using var blurred = new Mat();
            Cv2.GaussianBlur(energy, blurred, new Size(31, 31), 0);

            var (cx, cy, concentration) = CenterOfMass(blurred);
            return new SubjectHit(time, cx, cy, 0.4, 0.4, Math.Clamp(concentration, 0, 1), SubjectKind.Salient);
        }
        catch (Exception ex)
        {
            _sink.Debug("framing", $"Saliency failed at {time:0.0}s: {ex.Message}");
            return SubjectHit.Unknown(time);
        }
    }

    /// <summary>Energy-weighted centroid, plus how concentrated that energy is (0 = flat, 1 = a point).</summary>
    private static (double X, double Y, double Concentration) CenterOfMass(Mat energy)
    {
        // Column and row profiles are enough here and far cheaper than a full 2D pass.
        using var colSum = new Mat();
        using var rowSum = new Mat();
        Cv2.Reduce(energy, colSum, ReduceDimension.Row, ReduceTypes.Sum, MatType.CV_32F);
        Cv2.Reduce(energy, rowSum, ReduceDimension.Column, ReduceTypes.Sum, MatType.CV_32F);

        var (x, spreadX) = Centroid(colSum.Cols, i => colSum.At<float>(0, i));
        var (y, _) = Centroid(rowSum.Rows, i => rowSum.At<float>(i, 0));

        return (x, y, 1 - Math.Clamp(spreadX * 3.2, 0, 1));
    }

    private static (double Center, double Spread) Centroid(int count, Func<int, float> at)
    {
        if (count <= 1) return (0.5, 1);

        double total = 0, weighted = 0;
        for (var i = 0; i < count; i++)
        {
            var v = Math.Max(0, at(i));
            total += v;
            weighted += v * i;
        }
        if (total <= 0.0001) return (0.5, 1);

        var center = weighted / total;
        double variance = 0;
        for (var i = 0; i < count; i++)
        {
            var v = Math.Max(0, at(i));
            var d = i - center;
            variance += v * d * d;
        }
        var spread = Math.Sqrt(variance / total) / count;
        return (Math.Clamp(center / (count - 1), 0, 1), spread);
    }

    public void Dispose()
    {
        _faceNet?.Dispose();
        _personDetector?.Dispose();
        _previousGray?.Dispose();
    }
}
