using System.Globalization;
using System.Text;
using ReelsMagician.Core.Media;

namespace ReelsMagician.Core.Framing;

public sealed record CropKeyframe(double Time, double X, double Y);

/// <summary>The crop window and how it travels across one clip, in source pixels.</summary>
public sealed class CropPath
{
    public int CropWidth { get; init; }
    public int CropHeight { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public List<CropKeyframe> Keyframes { get; init; } = [];

    public SubjectKind DominantSubject { get; set; } = SubjectKind.None;
    public double FaceCoverage { get; set; }
    public bool IsStatic => Keyframes.Count <= 1;

    public double MotionRange => Keyframes.Count == 0
        ? 0
        : Keyframes.Max(k => k.X) - Keyframes.Min(k => k.X);

    public string Describe()
    {
        var subject = DominantSubject switch
        {
            SubjectKind.Face => $"speaker ({FaceCoverage:P0} of frames)",
            SubjectKind.Person => "person",
            SubjectKind.Salient => "main visual subject",
            _ => "centre",
        };
        var motion = IsStatic ? "locked" : $"tracking across {MotionRange:0} px";
        return $"{subject}, {motion}";
    }

    /// <summary>
    /// Builds the ffmpeg crop expression for one axis: a piecewise-linear walk through the
    /// keyframes, evaluated against the clip-relative timestamp.
    /// </summary>
    public string BuildExpression(bool horizontal)
    {
        double Value(CropKeyframe k) => horizontal ? k.X : k.Y;

        if (Keyframes.Count == 0) return "0";
        if (Keyframes.Count == 1) return Num(Value(Keyframes[0]));

        // Walk backwards so each segment wraps the ones after it: if before this keyframe's
        // time, interpolate within this segment, otherwise defer to the rest.
        var expression = new StringBuilder(Num(Value(Keyframes[^1])));

        for (var i = Keyframes.Count - 2; i >= 0; i--)
        {
            var a = Keyframes[i];
            var b = Keyframes[i + 1];
            var span = b.Time - a.Time;

            string segment;
            if (span <= 0.001)
            {
                segment = Num(Value(a));
            }
            else
            {
                var from = Value(a);
                var delta = Value(b) - from;
                segment = Math.Abs(delta) < 0.01
                    ? Num(from)
                    : $"({Num(from)}+{Num(delta)}*(t-{Num(a.Time)})/{Num(span)})";
            }

            expression.Insert(0, $"if(lt(t,{Num(b.Time)}),{segment},")
                      .Append(')');
        }
        return expression.ToString();
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed record FramingOptions(
    double SampleFps = 3,
    int AnalysisWidth = 480,
    /// <summary>Seconds for the crop to catch up with the subject. Higher is calmer.</summary>
    double SmoothingSeconds = 0.85,
    /// <summary>Fraction of the source width the subject may drift before the crop moves at all.</summary>
    double DeadzoneFraction = 0.035,
    /// <summary>Maximum pan speed, as a fraction of source width per second.</summary>
    double MaxPanPerSecond = 0.22,
    int MaxKeyframes = 48,
    double SimplifyPixels = 2.5,
    bool AllowVerticalPan = true);

/// <summary>
/// Turns per-frame detections into a camera path that a person would accept: outliers are
/// filtered out, a deadzone stops the crop twitching around a stationary speaker, and the
/// remaining motion is eased and speed-limited so pans never snap.
/// </summary>
public static class CropPlanner
{
    /// <summary>
    /// The largest window with <em>exactly</em> the target aspect that fits inside the source.
    /// The target is reduced to its smallest integer ratio (1080×1920 → 9×16) and the window is a
    /// whole multiple of it, so the scale that follows is uniform by construction. Any other
    /// rounding — even snapping each side to an even pixel — leaves a window the scaler has to
    /// stretch to fill the reel. Odd sizes are fine: the crop is always followed by a scale.
    /// </summary>
    public static (int Width, int Height) ComputeCropSize(int sourceWidth, int sourceHeight, int targetW, int targetH)
    {
        if (targetW <= 0 || targetH <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetW), "The target size must be positive.");

        var divisor = Gcd(targetW, targetH);
        var unitW = targetW / divisor;
        var unitH = targetH / divisor;

        var steps = Math.Min(sourceWidth / unitW, sourceHeight / unitH);
        if (steps < 1)
        {
            // Smaller than a single aspect unit: nothing exact fits. The renderer's
            // aspect-preserving scale still keeps such a frame undistorted.
            return (Math.Max(1, Math.Min(sourceWidth, unitW)), Math.Max(1, Math.Min(sourceHeight, unitH)));
        }
        return (unitW * steps, unitH * steps);
    }

    public static CropPath Plan(
        IReadOnlyList<SubjectHit> hits,
        MediaInfo source,
        double clipDuration,
        int targetWidth,
        int targetHeight,
        FramingOptions options)
    {
        var (sw, sh) = source.DisplaySize;
        var (cropW, cropH) = ComputeCropSize(sw, sh, targetWidth, targetHeight);

        var path = new CropPath
        {
            CropWidth = cropW,
            CropHeight = cropH,
            SourceWidth = sw,
            SourceHeight = sh,
        };

        var maxX = Math.Max(0, sw - cropW);
        var maxY = Math.Max(0, sh - cropH);

        if (hits.Count == 0 || (maxX == 0 && maxY == 0))
        {
            path.Keyframes.Add(new CropKeyframe(0, maxX / 2.0, maxY / 2.0));
            path.DominantSubject = SubjectKind.None;
            return path;
        }

        path.FaceCoverage = hits.Count(h => h.Kind == SubjectKind.Face) / (double)hits.Count;
        path.DominantSubject = DominantKind(hits);

        // 1. Reject outliers — a single bad frame should not yank the camera.
        var centersX = MedianFilter([.. hits.Select(h => h.CenterX)], 5);
        var centersY = MedianFilter([.. hits.Select(h => h.CenterY)], 5);

        // 2. Ease toward the subject, with a deadzone and a speed limit.
        var deadzone = options.DeadzoneFraction;
        var maxStep = options.MaxPanPerSecond;

        double currentX = centersX[0], currentY = centersY[0];
        var raw = new List<CropKeyframe>(hits.Count);
        var previousTime = hits[0].Time;

        for (var i = 0; i < hits.Count; i++)
        {
            var dt = Math.Max(1e-3, hits[i].Time - previousTime);
            previousTime = hits[i].Time;
            var alpha = 1 - Math.Exp(-dt / Math.Max(0.05, options.SmoothingSeconds));

            currentX = Advance(currentX, centersX[i], alpha, deadzone, maxStep * dt);
            currentY = options.AllowVerticalPan
                ? Advance(currentY, centersY[i], alpha, deadzone * 1.6, maxStep * dt)
                : 0.5;

            // Convert the subject centre into a crop origin, clamped inside the frame.
            var x = Math.Clamp(currentX * sw - cropW / 2.0, 0, maxX);
            var y = Math.Clamp(currentY * sh - cropH / 2.0, 0, maxY);

            raw.Add(new CropKeyframe(Math.Max(0, hits[i].Time - hits[0].Time), x, y));
        }

        // 3. Hold the very first frame steady so the clip does not open mid-pan.
        if (raw.Count > 0) raw[0] = raw[0] with { Time = 0 };
        raw.Add(new CropKeyframe(Math.Max(clipDuration, raw[^1].Time), raw[^1].X, raw[^1].Y));

        // 4. Reduce to the fewest keyframes that still describe the motion.
        var simplified = Simplify(raw, options.SimplifyPixels);
        if (simplified.Count > options.MaxKeyframes)
            simplified = Decimate(simplified, options.MaxKeyframes);

        // A path that never really moves is emitted as a fixed crop: cheaper and rock steady.
        var travelX = simplified.Max(k => k.X) - simplified.Min(k => k.X);
        var travelY = simplified.Max(k => k.Y) - simplified.Min(k => k.Y);
        if (travelX < 2 && travelY < 2)
        {
            var avgX = simplified.Average(k => k.X);
            var avgY = simplified.Average(k => k.Y);
            simplified = [new CropKeyframe(0, Math.Round(avgX), Math.Round(avgY))];
        }

        path.Keyframes.AddRange(simplified);
        return path;
    }

    /// <summary>One easing step with a deadzone and a per-frame speed cap.</summary>
    private static double Advance(double current, double target, double alpha, double deadzone, double maxStep)
    {
        var error = target - current;
        if (Math.Abs(error) <= deadzone) return current;

        // Only chase the part of the error outside the deadzone, so the crop settles
        // instead of oscillating around the edge of it.
        var corrected = error - Math.Sign(error) * deadzone;
        var step = corrected * alpha;
        if (Math.Abs(step) > maxStep) step = Math.Sign(step) * maxStep;
        return Math.Clamp(current + step, 0, 1);
    }

    private static SubjectKind DominantKind(IReadOnlyList<SubjectHit> hits)
    {
        var faces = hits.Count(h => h.Kind == SubjectKind.Face);
        var people = hits.Count(h => h.Kind == SubjectKind.Person);

        // A speaker who is detected in a decent share of frames is the subject even when
        // detection drops out on turns and blinks.
        if (faces >= hits.Count * 0.25) return SubjectKind.Face;
        if (faces + people >= hits.Count * 0.35) return SubjectKind.Person;
        return hits.Any(h => h.Kind == SubjectKind.Salient) ? SubjectKind.Salient : SubjectKind.None;
    }

    private static double[] MedianFilter(double[] values, int window)
    {
        if (values.Length <= 2 || window < 3) return values;
        var half = window / 2;
        var result = new double[values.Length];
        var scratch = new List<double>(window);

        for (var i = 0; i < values.Length; i++)
        {
            scratch.Clear();
            for (var j = Math.Max(0, i - half); j <= Math.Min(values.Length - 1, i + half); j++)
                scratch.Add(values[j]);
            scratch.Sort();
            result[i] = scratch[scratch.Count / 2];
        }
        return result;
    }

    /// <summary>Ramer–Douglas–Peucker over the 2D path, with time as the parameter.</summary>
    private static List<CropKeyframe> Simplify(List<CropKeyframe> points, double epsilon)
    {
        if (points.Count <= 2) return points;

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        SimplifySegment(points, 0, points.Count - 1, epsilon, keep);

        var result = new List<CropKeyframe>();
        for (var i = 0; i < points.Count; i++)
            if (keep[i]) result.Add(points[i]);
        return result;
    }

    private static void SimplifySegment(List<CropKeyframe> points, int first, int last, double epsilon, bool[] keep)
    {
        if (last <= first + 1) return;

        var a = points[first];
        var b = points[last];
        var span = b.Time - a.Time;

        var worst = (Distance: 0.0, Index: -1);
        for (var i = first + 1; i < last; i++)
        {
            var t = span <= 0 ? 0 : (points[i].Time - a.Time) / span;
            var predictedX = a.X + (b.X - a.X) * t;
            var predictedY = a.Y + (b.Y - a.Y) * t;
            var dx = points[i].X - predictedX;
            var dy = points[i].Y - predictedY;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance > worst.Distance) worst = (distance, i);
        }

        if (worst.Index < 0 || worst.Distance <= epsilon) return;
        keep[worst.Index] = true;
        SimplifySegment(points, first, worst.Index, epsilon, keep);
        SimplifySegment(points, worst.Index, last, epsilon, keep);
    }

    /// <summary>Evenly thins a path that is still too dense after simplification.</summary>
    private static List<CropKeyframe> Decimate(List<CropKeyframe> points, int maxCount)
    {
        var result = new List<CropKeyframe>(maxCount);
        var stride = (points.Count - 1) / (double)(maxCount - 1);
        for (var i = 0; i < maxCount; i++)
            result.Add(points[Math.Min(points.Count - 1, (int)Math.Round(i * stride))]);
        return result;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
