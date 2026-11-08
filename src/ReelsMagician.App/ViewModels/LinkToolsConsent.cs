using ReelsMagician.App.Localization;
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
                .Select(c => Localizer.Instance.Format(nameof(Strings.LinkTools_Item), c.Name, AssetDownloader.Humanize(c.ApproxBytes))));

        // Its responsibility sentence is word for word the one in the README's "About video links"
        // section, in English, and the Polish says the same.
        var question = Localizer.Instance.Format(nameof(Strings.LinkTools_Ask), downloads);

        return shell.AskAsync(Strings.LinkTools_Title, question, Strings.Common_Download, Strings.Common_No);
    }
}
