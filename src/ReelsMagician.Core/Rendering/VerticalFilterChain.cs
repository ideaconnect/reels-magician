using ReelsMagician.Core.Framing;
using ReelsMagician.Core.Media;

namespace ReelsMagician.Core.Rendering;

/// <summary>
/// The filter chain that turns a source frame into a reel frame. Two operations are allowed on
/// the picture, cropping and <em>uniform</em> scaling, and nothing in the chain can change the
/// proportions of what the viewer sees. The reel and the caption preview share it, so the still
/// shown in the app is cut exactly the way the final clip is.
/// </summary>
public static class VerticalFilterChain
{
    /// <summary>
    /// Filters, in order, that take the decoded frame of <paramref name="source"/> to a
    /// <paramref name="width"/>×<paramref name="height"/> square-pixel frame, following
    /// <paramref name="crop"/> when there is one and taking a centre cut when there is not.
    /// </summary>
    public static List<string> Build(MediaInfo? source, CropPath? crop, int width, int height)
    {
        var filters = new List<string>();

        // 1. Anamorphic sources are resampled to square pixels first. The crop planner works in
        //    display geometry, and a window it sized there would be the wrong shape if it were
        //    cut from the squeezed picture.
        if (source is not null && !source.HasSquarePixels)
        {
            var (displayW, displayH) = source.DisplaySize;
            filters.Add($"scale=w={displayW}:h={displayH}:flags=lanczos");
            filters.Add("setsar=1");
        }

        // 2. The tracked window. CropPlanner sizes it to exactly the target aspect, so the scale
        //    after it is uniform. Single-quoted so the commas inside the if() expressions are not
        //    read as filter separators.
        if (crop is not null)
        {
            filters.Add($"crop=w={crop.CropWidth}:h={crop.CropHeight}:" +
                        $"x='{crop.BuildExpression(horizontal: true)}':" +
                        $"y='{crop.BuildExpression(horizontal: false)}'");
        }

        // 3. Scale to cover the target while keeping the incoming proportions, then centre-crop
        //    to the exact size. With an exact window the crop trims nothing; with no window at
        //    all (the preview before framing has run) this is a centre cut of the frame rather
        //    than the whole picture squeezed into 9:16. A plain scale=W:H here would stretch.
        filters.Add($"scale=w={width}:h={height}:force_original_aspect_ratio=increase:flags=lanczos");
        filters.Add($"crop=w={width}:h={height}");
        filters.Add("setsar=1");
        return filters;
    }
}
