using ReelsMagician.Core.Localization;
using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.Core.About;

/// <summary>How a component reaches the user, in the order THIRD-PARTY-NOTICES.md lists them.</summary>
public enum LicenceGroup
{
    /// <summary>Copied into the source or restored by NuGet: in every build of the app.</summary>
    EveryBuild,

    /// <summary>Added by a release package: the .NET runtime, and in the AppImage its own runtime.</summary>
    ReleasePackage,

    /// <summary>Downloaded by the app into its data folder.</summary>
    DownloadedByApp,

    /// <summary>Downloaded only once the user agrees.</summary>
    DownloadedIfAgreed,
}

/// <summary>Where a licence text in a build comes from, and so which builds carry it.</summary>
public enum LicenceOrigin
{
    /// <summary>Kept in this repository (LICENSE, THIRD-PARTY-NOTICES.md, licenses/): every publish carries it.</summary>
    Repository,

    /// <summary>Shipped by a NuGet package, which the publish copies to licenses/&lt;package&gt;/.</summary>
    Package,

    /// <summary>Kept in packaging/linux/licenses, which only the AppImage adds.</summary>
    AppImage,
}

/// <summary>A downloaded component whose install state the About tab shows.</summary>
public enum DownloadedTool { Ffmpeg, Whisper, Llama, WhisperModel, LlmModel, FaceModel, YtDlp, JsRuntime }

/// <param name="Path">Relative to the app's folder, with forward slashes: <c>licenses/SkiaSharp/LICENSE.txt</c>.</param>
/// <param name="Package">For a text a package ships, that package.</param>
/// <param name="OnlyOn">For a text only one system's build carries, that system.</param>
public sealed record LicenceText(string Path, LicenceOrigin Origin, string? Package = null, TargetOs? OnlyOn = null)
{
    public static LicenceText Kept(string path) => new(path, LicenceOrigin.Repository);

    public static LicenceText Shipped(string package, string file, TargetOs? onlyOn = null) =>
        new($"licenses/{package}/{file}", LicenceOrigin.Package, package, onlyOn);

    public static LicenceText InAppImage(string path) => new(path, LicenceOrigin.AppImage, OnlyOn: TargetOs.Linux);
}

/// <summary>One row of the About tab's licences, and of THIRD-PARTY-NOTICES.md.</summary>
/// <param name="Source">Where it comes from; THIRD-PARTY-NOTICES.md lists the same address in the same section.</param>
/// <param name="Texts">The licence texts a build carries for it, which View licence shows.</param>
/// <param name="Packages">The NuGet packages and runtime packs it stands for. Every package in a publish is one row's.</param>
/// <param name="Tool">For a downloaded component, which one, so the About tab can say whether it is installed.</param>
/// <param name="OnlyOn">A component only one system's build has; the other system's About tab leaves it out.</param>
public sealed record LicensedComponent(
    string Name,
    string Purpose,
    string Licence,
    string Source,
    LicenceGroup Group,
    IReadOnlyList<LicenceText> Texts,
    IReadOnlyList<string> Packages,
    DownloadedTool? Tool = null,
    TargetOs? OnlyOn = null)
{
    public bool AppliesTo(TargetOs os) => OnlyOn is null || OnlyOn == os;
}

/// <summary>
/// Every licence involved in ReelsMagician, held once here rather than copied into the window. The
/// logic checks (LicenceChecks) hold it to THIRD-PARTY-NOTICES.md, to the project files and the
/// repository's licence texts, and, given a publish, to every package and licence file in it, so a
/// new dependency can't ship without a row.
/// </summary>
public static class Licences
{
    /// <summary>ReelsMagician itself.</summary>
    public static LicensedComponent App { get; } = new(
        "ReelsMagician", "This app", "BSD-3-Clause", AppInfo.RepositoryLink, LicenceGroup.EveryBuild,
        [LicenceText.Kept("LICENSE")], []);

    /// <summary>The whole notices file, which every build carries next to the app.</summary>
    public static LicenceText Notices { get; } = LicenceText.Kept("THIRD-PARTY-NOTICES.md");

    public const string FontAwesomeAttribution =
        "Font Awesome Free 7.3.1 by @fontawesome - https://fontawesome.com, used under CC BY 4.0. " +
        "Icons by Fonticons, Inc., converted from SVG to XAML path data with their shapes unchanged.";

    public static IReadOnlyList<LicensedComponent> Components { get; } =
    [
        // ── In every build ──
        new("Font Awesome Free 7.3.1", "The icons the app draws", "Icons: CC BY 4.0",
            "https://fontawesome.com", LicenceGroup.EveryBuild,
            [LicenceText.Kept("licenses/FontAwesome/LICENSE.txt")], []),
        new("Avalonia", "The user interface", "MIT", "https://github.com/AvaloniaUI/Avalonia", LicenceGroup.EveryBuild,
            [LicenceText.Kept("licenses/Avalonia/licence.md"), LicenceText.Kept("licenses/Avalonia/NOTICE.md")],
            ["Avalonia", "Avalonia.Skia", "Avalonia.HarfBuzz", "Avalonia.Win32", "Avalonia.Wayland",
             "Avalonia.Themes.Fluent", "Avalonia.FreeDesktop", "Avalonia.Remote.Protocol"]),
        new("NWayland", "Avalonia's Wayland bindings", "MIT", "https://github.com/kekekeks/NWayland",
            LicenceGroup.EveryBuild, [LicenceText.Kept("licenses/NWayland/licence.md")], ["NWayland"]),
        new("MicroCom.Runtime", "Used by Avalonia", "MIT", "https://github.com/kekekeks/MicroCom",
            LicenceGroup.EveryBuild, [LicenceText.Kept("licenses/MicroCom/LICENSE")], ["MicroCom.Runtime"]),
        new("Tmds.DBus.Protocol", "Used by Avalonia on Linux", "MIT", "https://github.com/tmds/Tmds.DBus",
            LicenceGroup.EveryBuild, [LicenceText.Kept("licenses/Tmds.DBus/COPYING")], ["Tmds.DBus.Protocol"]),
        new("SkiaSharp, with its native Skia library", "Draws the interface",
            "MIT; Skia itself is BSD-3-Clause, and the native library carries notices of its own",
            "https://github.com/mono/SkiaSharp", LicenceGroup.EveryBuild,
            [
                LicenceText.Shipped("SkiaSharp", "LICENSE.txt"),
                LicenceText.Shipped("SkiaSharp.NativeAssets.Win32", "LICENSE.txt", TargetOs.Windows),
                LicenceText.Shipped("SkiaSharp.NativeAssets.Win32", "THIRD-PARTY-NOTICES.txt", TargetOs.Windows),
                LicenceText.Shipped("SkiaSharp.NativeAssets.Linux", "LICENSE.txt", TargetOs.Linux),
                LicenceText.Shipped("SkiaSharp.NativeAssets.Linux", "THIRD-PARTY-NOTICES.txt", TargetOs.Linux),
            ],
            // A build for any system also holds the macOS library, which neither release package carries.
            ["SkiaSharp", "SkiaSharp.NativeAssets.Win32", "SkiaSharp.NativeAssets.Linux", "SkiaSharp.NativeAssets.macOS"]),
        new("HarfBuzzSharp, with its native HarfBuzz library", "Shapes text",
            "MIT; HarfBuzz itself is under the \"Old MIT\" licence", "https://github.com/mono/SkiaSharp",
            LicenceGroup.EveryBuild,
            [
                LicenceText.Shipped("HarfBuzzSharp", "LICENSE.txt"),
                LicenceText.Shipped("HarfBuzzSharp.NativeAssets.Win32", "LICENSE.txt", TargetOs.Windows),
                LicenceText.Shipped("HarfBuzzSharp.NativeAssets.Win32", "THIRD-PARTY-NOTICES.txt", TargetOs.Windows),
                LicenceText.Shipped("HarfBuzzSharp.NativeAssets.Linux", "LICENSE.txt", TargetOs.Linux),
                LicenceText.Shipped("HarfBuzzSharp.NativeAssets.Linux", "THIRD-PARTY-NOTICES.txt", TargetOs.Linux),
            ],
            ["HarfBuzzSharp", "HarfBuzzSharp.NativeAssets.Win32", "HarfBuzzSharp.NativeAssets.Linux",
             "HarfBuzzSharp.NativeAssets.macOS"]),
        new("ANGLE (Avalonia.Angle.Windows.Natives)", "Draws the interface through Direct3D on Windows",
            "BSD-3-Clause", "https://github.com/AvaloniaUI/angle", LicenceGroup.EveryBuild,
            [LicenceText.Shipped("Avalonia.Angle.Windows.Natives", "LICENSE", TargetOs.Windows)],
            ["Avalonia.Angle.Windows.Natives"], OnlyOn: TargetOs.Windows),
        new("OpenCvSharp5", "Face and people detection: OpenCV 5.0 for .NET", "Apache-2.0",
            "https://github.com/shimat/opencvsharp", LicenceGroup.EveryBuild,
            [LicenceText.Kept("licenses/OpenCvSharp/LICENSE")], ["OpenCvSharp5"]),
        new("OpenCV for Windows (OpenCvSharp5.runtime.win)", "OpenCV's native library",
            "Apache-2.0; its opencv_videoio_ffmpeg DLL is OpenCV's LGPL-2.1 build of FFmpeg",
            "https://github.com/shimat/opencvsharp", LicenceGroup.EveryBuild,
            [
                LicenceText.Kept("licenses/OpenCV/LICENSE"), LicenceText.Kept("licenses/OpenCV/COPYRIGHT"),
                LicenceText.Kept("licenses/FFmpeg/COPYING.LGPLv2.1"),
            ],
            ["OpenCvSharp5.runtime.win"], OnlyOn: TargetOs.Windows),
        new("OpenCV for Linux, headless (OpenCvSharp5.official.runtime.linux-x64.headless)",
            "OpenCV's native library, without OpenCV's own windows",
            "Apache-2.0; it statically links FFmpeg (LGPL-2.1) and Tesseract (Apache-2.0)",
            "https://github.com/shimat/opencvsharp", LicenceGroup.EveryBuild,
            [
                LicenceText.Kept("licenses/OpenCV/LICENSE"), LicenceText.Kept("licenses/OpenCV/COPYRIGHT"),
                LicenceText.Kept("licenses/FFmpeg/COPYING.LGPLv2.1"), LicenceText.Kept("licenses/Tesseract/LICENSE"),
            ],
            ["OpenCvSharp5.official.runtime.linux-x64.headless"], OnlyOn: TargetOs.Linux),
        new("SharpCompress", "Unpacks the .tar.xz archives Linux tools come in", "MIT",
            "https://github.com/adamhathcock/sharpcompress", LicenceGroup.EveryBuild,
            [LicenceText.Kept("licenses/SharpCompress/LICENSE.txt")], ["SharpCompress"]),
        new("Velopack", "Connects the app to its Windows installer",
            "MIT", "https://github.com/velopack/velopack", LicenceGroup.EveryBuild,
            [LicenceText.Kept("licenses/Velopack/LICENSE")], ["Velopack"]),

        // ── Added by the release package ──
        new(".NET runtime", "Runs the app; the Windows installer and zip and the AppImage include it", "MIT, with notices of its own",
            "https://github.com/dotnet/runtime", LicenceGroup.ReleasePackage,
            [
                LicenceText.Shipped("Microsoft.NETCore.App.Runtime.win-x64", "LICENSE.TXT", TargetOs.Windows),
                LicenceText.Shipped("Microsoft.NETCore.App.Runtime.win-x64", "THIRD-PARTY-NOTICES.TXT", TargetOs.Windows),
                LicenceText.Shipped("Microsoft.NETCore.App.Runtime.linux-x64", "LICENSE.TXT", TargetOs.Linux),
                LicenceText.Shipped("Microsoft.NETCore.App.Runtime.linux-x64", "THIRD-PARTY-NOTICES.TXT", TargetOs.Linux),
            ],
            ["Microsoft.NETCore.App.Runtime.win-x64", "Microsoft.NETCore.App.Runtime.linux-x64"]),
        new("AppImage type2 runtime", "Mounts the AppImage and starts the app",
            "MIT; it statically links musl (MIT), libfuse (LGPL-2.1), squashfuse (BSD-2-Clause), " +
            "zstd (BSD-3-Clause or GPL-2.0) and zlib (zlib licence)",
            "https://github.com/AppImage/type2-runtime", LicenceGroup.ReleasePackage,
            [
                LicenceText.InAppImage("licenses/AppImage-type2-runtime/LICENSE"),
                LicenceText.InAppImage("licenses/musl/COPYRIGHT"),
                LicenceText.InAppImage("licenses/libfuse/LGPL2.txt"),
                LicenceText.InAppImage("licenses/squashfuse/LICENSE"),
                LicenceText.InAppImage("licenses/zstd/LICENSE"),
                LicenceText.InAppImage("licenses/zlib/LICENSE"),
            ],
            [], OnlyOn: TargetOs.Linux),
        new("Velopack's Setup.exe and Update.exe", "Install the app for your account, and remove it again",
            "MIT", "https://github.com/velopack/velopack", LicenceGroup.ReleasePackage,
            [LicenceText.Kept("licenses/Velopack/LICENSE")], [], OnlyOn: TargetOs.Windows),

        // ── Downloaded by the app ──
        new("ffmpeg + ffprobe", "Reads, cuts and encodes the video",
            "GPL-3.0: BtbN's GPL build includes GPL-only libraries such as libx264",
            "https://github.com/BtbN/FFmpeg-Builds", LicenceGroup.DownloadedByApp, [], [], DownloadedTool.Ffmpeg),
        new("whisper.cpp", "Speech recognition",
            "MIT; a CUDA build includes NVIDIA's CUDA runtime libraries, under NVIDIA's CUDA EULA",
            "https://github.com/ggml-org/whisper.cpp", LicenceGroup.DownloadedByApp, [], [], DownloadedTool.Whisper),
        new("llama.cpp", "Runs Qwen, which picks the reel",
            "MIT; with a CUDA build the app also downloads NVIDIA's CUDA runtime libraries, under NVIDIA's CUDA EULA",
            "https://github.com/ggml-org/llama.cpp", LicenceGroup.DownloadedByApp, [], [], DownloadedTool.Llama),
        new("Whisper model", "The speech recognition model, a ggml conversion", "MIT",
            "https://huggingface.co/ggerganov/whisper.cpp", LicenceGroup.DownloadedByApp, [], [], DownloadedTool.WhisperModel),
        new("Qwen3 model", "The language model, 4B or 8B, as GGUF", "Apache-2.0",
            "https://huggingface.co/Qwen", LicenceGroup.DownloadedByApp, [], [], DownloadedTool.LlmModel),
        new("YuNet face detector", "Finds faces to keep in the frame", "MIT",
            "https://github.com/opencv/opencv_zoo", LicenceGroup.DownloadedByApp, [], [], DownloadedTool.FaceModel),

        // ── Downloaded only if you agree ──
        new("yt-dlp", "Downloads a video from a link",
            "GPLv3+: the standalone builds include GPLv3+ code; yt-dlp's own code is Unlicense",
            "https://github.com/yt-dlp/yt-dlp", LicenceGroup.DownloadedIfAgreed, [], [], DownloadedTool.YtDlp),
        new("Deno", "The JavaScript runtime yt-dlp uses to read a video's formats", "MIT",
            "https://github.com/denoland/deno", LicenceGroup.DownloadedIfAgreed, [], [], DownloadedTool.JsRuntime),
    ];

    /// <summary>The heading and the line under it the About tab gives each group, in the interface's language.</summary>
    public static (string Title, string Description) Describe(LicenceGroup group) =>
        (Lang.Ui[$"LicenceGroup_{group}_Title"], Lang.Ui[$"LicenceGroup_{group}_Description"]);

    /// <summary>
    /// What a component is for, as the About tab shows it in the interface's language. The English
    /// resource holds the row's own <see cref="LicensedComponent.Purpose"/>, which the logic checks
    /// keep equal; THIRD-PARTY-NOTICES.md and the checks go by the row.
    /// </summary>
    public static string UiPurpose(LicensedComponent component) => Lang.Ui[PurposeKey(component)];

    /// <summary>
    /// A component's licence as the About tab shows it. Only a licence that says more than an
    /// identifier, such as "MIT; Skia itself is BSD-3-Clause…", has a translation; "MIT" stays itself.
    /// </summary>
    public static string UiLicence(LicensedComponent component) =>
        Strings.ResourceManager.GetString(LicenceKey(component), Lang.Ui.Texts) ?? component.Licence;

    /// <summary>
    /// A component's name as the About tab shows it. A name that describes, such as "Whisper model",
    /// has a translation; a product's own name, such as "SharpCompress", stays itself.
    /// </summary>
    public static string UiName(LicensedComponent component) =>
        Strings.ResourceManager.GetString(NameKey(component), Lang.Ui.Texts) ?? component.Name;

    public static string NameKey(LicensedComponent component) => $"Licence_{Lang.Slug(component.Name)}_Name";

    public static string PurposeKey(LicensedComponent component) => $"Licence_{Lang.Slug(component.Name)}_Purpose";

    public static string LicenceKey(LicensedComponent component) => $"Licence_{Lang.Slug(component.Name)}_Licence";

    /// <summary>The rows this system's About tab shows, in their groups' order.</summary>
    public static IEnumerable<IGrouping<LicenceGroup, LicensedComponent>> For(TargetOs os) =>
        Components.Where(c => c.AppliesTo(os)).GroupBy(c => c.Group).OrderBy(g => g.Key);

    /// <summary>The texts View licence shows for a row on this system.</summary>
    public static IReadOnlyList<LicenceText> TextsFor(LicensedComponent component, TargetOs os) =>
        [.. component.Texts.Where(t => t.OnlyOn is null || t.OnlyOn == os)];

    /// <summary>
    /// Reads texts from the app's folder (<see cref="AppContext.BaseDirectory"/>). A text this build
    /// lacks, such as the AppImage's own when running from source, comes back with no text rather
    /// than failing.
    /// </summary>
    public static IReadOnlyList<LicenceFile> Read(string appFolder, IEnumerable<LicenceText> texts) =>
    [
        .. texts.Select(t =>
        {
            var path = Path.Combine(appFolder, t.Path.Replace('/', Path.DirectorySeparatorChar));
            try { return new LicenceFile(t.Path, File.Exists(path) ? File.ReadAllText(path) : null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new LicenceFile(t.Path, null); }
        }),
    ];
}

/// <summary>A licence text as read from a build; <see cref="Text"/> is null when the build lacks it.</summary>
public sealed record LicenceFile(string Path, string? Text)
{
    public bool Found => Text is not null;
}
