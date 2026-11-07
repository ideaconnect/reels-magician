using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.App.ViewModels;

/// <summary>
/// Asks before the link tools are downloaded. yt-dlp is a third-party tool that does not come
/// with the app, so nothing downloads it until the user has said yes here. No is the default,
/// so a stray Enter does not count as agreeing.
/// </summary>
public sealed class LinkToolsConsent(IAppShell shell, ToolchainManager toolchain)
{
    /// <summary>
    /// Whether loading this source means downloading the link tools first, which happens only
    /// after the user agrees. Decided in one place, so the question and the download can never
    /// disagree about whether a download is coming.
    /// </summary>
    public static bool NeedsDownload(bool isLink, Toolset tools) => isLink && !tools.HasYtDlp;

    /// <summary>Asks the user, listing what would be downloaded. True only on an explicit yes.</summary>
    public Task<bool> AskAsync(Toolset tools)
    {
        var downloads = string.Join(Environment.NewLine,
            toolchain.DescribeLinkTools(tools)
                .Where(c => c.NeedsDownload)
                .Select(c => $"  • {c.Name}, about {AssetDownloader.Humanize(c.ApproxBytes)}"));

        var question =
            "Loading a video from a link needs yt-dlp, a separate third-party tool. " +
            "It is not part of ReelsMagician and does not come with it.\n\n" +
            $"If you continue, ReelsMagician downloads from the publishers' own GitHub releases:\n\n{downloads}\n\n" +
            "You are responsible for having the right to download the videos you use it on, " +
            "and for following the terms of the site they come from.\n\n" +
            "Local video files work without it. Download now?";

        return shell.AskAsync("Video links need yt-dlp", question, "Download", "No");
    }
}
