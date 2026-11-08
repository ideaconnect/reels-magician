// Copies the Font Awesome Free icons the app draws into src/ReelsMagician.App/Assets/Icons.axaml,
// and Font Awesome's licence into licenses/FontAwesome/LICENSE.txt. Run it from anywhere:
//
//   dotnet run tools/update-icons.cs                        the version below, from the npm registry
//   dotnet run tools/update-icons.cs -- 7.4.0               another version
//   dotnet run tools/update-icons.cs -- path/to/package     an unpacked @fortawesome/fontawesome-free
//
// To use another icon, add its name to Icons below and run the script again. After a version
// change, update the version in THIRD-PARTY-NOTICES.md too; the logic checks compare the two.
//
// Each icon comes from the package's svgs-full folder, which draws every icon on the same
// 640×640 viewBox, so FaIcon can scale them all alike. Free icons are a single path each.

using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const string DefaultVersion = "7.3.1";
const string Package = "@fortawesome/fontawesome-free";

// "name" is a solid icon; "regular/name" or "brands/name" picks another style.
string[] Icons =
[
    // Component status on the Setup tab.
    "circle-check", "circle-xmark", "circle-minus", "triangle-exclamation",
    // A done step in the step strip.
    "check",
    // The appearance switch in the header: follow the system, light, dark.
    "desktop", "sun", "moon",
    // Buttons.
    "folder-open", "rotate", "download", "copy", "scissors", "code-merge", "trash-can", "backward-step",
    "forward-step", "file-import", "file-export", "film", "broom", "xmark",
    // The support bar.
    "mug-hot", "heart",
];

var root = Path.GetFullPath(Path.Combine(
    Path.GetDirectoryName((string)AppContext.GetData("EntryPointFilePath")!)!, ".."));
if (!File.Exists(Path.Combine(root, "ReelsMagician.slnx")))
    return Fail($"{root} is not the repository root.");

var source = args.Length > 0 ? args[0] : DefaultVersion;
Dictionary<string, byte[]> files;
try
{
    files = Directory.Exists(source) ? ReadFolder(source) : await DownloadAsync(source);
}
catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException)
{
    return Fail($"Could not read {Package} {source}: {ex.Message}");
}

var version = JsonDocument.Parse(Need("package.json")).RootElement.GetProperty("version").GetString();
string? attribution = null;
var geometries = new StringBuilder();
foreach (var icon in Icons)
{
    var (style, name) = icon.Contains('/') ? (icon.Split('/')[0], icon.Split('/')[1]) : ("solid", icon);
    if (!files.TryGetValue($"svgs-full/{style}/{name}.svg", out var data))
        return Fail($"{icon}: Font Awesome Free {version} has no such icon.");
    var svg = Encoding.UTF8.GetString(data);
    if (!svg.Contains("viewBox=\"0 0 640 640\""))
        return Fail($"{icon}: not on the 640×640 viewBox FaIcon draws in.");
    var paths = Regex.Matches(svg, "<path[^>]* d=\"([^\"]+)\"");
    if (paths.Count != 1)
        return Fail($"{icon}: {paths.Count} paths; FaIcon draws one (duotone icons are Pro only).");

    // The credit Font Awesome asks for is the comment in each SVG; the header keeps it.
    var comment = Regex.Match(svg, "<!--!(.*?)-->").Groups[1].Value.Trim();
    if (attribution is null) attribution = comment;
    else if (comment != attribution) return Fail($"{icon}: its credit differs from the other icons'.");

    // F1 selects the nonzero fill rule the paths are drawn for; Avalonia's default is even-odd.
    geometries.Append($"  <StreamGeometry x:Key=\"fa-{name}\">F1 {paths[0].Groups[1].Value}</StreamGeometry>\n");
}

var axaml = $"""
    <!--
      Font Awesome Free {version} icons, copied by tools/update-icons.cs. Do not edit by hand: change
      the list in that script and run it again.

      {attribution}

      Each geometry is the path of the icon's SVG in the package's svgs-full folder, unchanged apart
      from the F1 prefix. Every icon there shares one 640×640 viewBox, which FaIcon draws in. The
      licence is in licenses/FontAwesome/LICENSE.txt; THIRD-PARTY-NOTICES.md credits it.
    -->
    <ResourceDictionary xmlns="https://github.com/avaloniaui"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    {geometries}</ResourceDictionary>

    """;
var axamlPath = Path.Combine(root, "src", "ReelsMagician.App", "Assets", "Icons.axaml");
File.WriteAllText(axamlPath, axaml.ReplaceLineEndings("\n"));
var licencePath = Path.Combine(root, "licenses", "FontAwesome", "LICENSE.txt");
Directory.CreateDirectory(Path.GetDirectoryName(licencePath)!);
File.WriteAllBytes(licencePath, Need("LICENSE.txt"));

Console.WriteLine($"Font Awesome Free {version}: {Icons.Length} icons in {Path.GetRelativePath(root, axamlPath)}, " +
                  $"licence in {Path.GetRelativePath(root, licencePath)}.");
if (version != DefaultVersion)
    Console.WriteLine($"Set DefaultVersion in this script and the version in THIRD-PARTY-NOTICES.md to {version}.");
return 0;

byte[] Need(string path) =>
    files.TryGetValue(path, out var data) ? data : throw new InvalidDataException($"The package has no {path}.");

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

// An unpacked package, such as node_modules/@fortawesome/fontawesome-free.
static Dictionary<string, byte[]> ReadFolder(string folder) =>
    Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(folder, f).Replace('\\', '/'), File.ReadAllBytes);

// The package's tarball from the npm registry, checked against the integrity hash the registry
// publishes for that version.
static async Task<Dictionary<string, byte[]>> DownloadAsync(string version)
{
    using var http = new HttpClient();
    using var meta = JsonDocument.Parse(await http.GetStringAsync($"https://registry.npmjs.org/{Package}/{version}"));
    var dist = meta.RootElement.GetProperty("dist");
    var tarball = await http.GetByteArrayAsync(dist.GetProperty("tarball").GetString());
    var integrity = dist.GetProperty("integrity").GetString() ?? "";
    if (integrity != "sha512-" + Convert.ToBase64String(SHA512.HashData(tarball)))
        throw new InvalidDataException($"The {Package} {version} tarball does not match the registry's integrity hash.");

    var files = new Dictionary<string, byte[]>();
    using var reader = new TarReader(new GZipStream(new MemoryStream(tarball), CompressionMode.Decompress));
    while (reader.GetNextEntry() is { } entry)
    {
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
            continue;
        using var data = new MemoryStream();
        entry.DataStream.CopyTo(data);
        // npm packs everything under a package/ folder.
        files[entry.Name.StartsWith("package/", StringComparison.Ordinal) ? entry.Name["package/".Length..] : entry.Name] =
            data.ToArray();
    }
    return files;
}
