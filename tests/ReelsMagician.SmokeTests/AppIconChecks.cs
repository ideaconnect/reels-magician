using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ReelsMagician.SmokeTests;

/// <summary>
/// The app's icon: every file tools/make-icons.cs writes still comes from assets/AppIconBase.png
/// as it is now, the .ico holds the frames Windows asks for, and the names that tie a Wayland
/// window to its desktop entry and icon agree. None of this fails a build when it goes wrong:
/// Windows just scales the wrong frame, and GNOME shows a generic icon.
/// </summary>
public static class AppIconChecks
{
    public static void Run()
    {
        Check.Section("App icon: made from assets/AppIconBase.png, with the same names everywhere");

        var root = FindRepositoryRoot();
        Check.True(root is not null, "the checks run inside the repository");
        if (root is null) return;
        // A Windows checkout may turn the line endings of the text files into CRLF.
        string Read(string path) => File.ReadAllText(Path.Combine(root, path)).ReplaceLineEndings("\n");

        var script = Read("tools/make-icons.cs");
        var windowsSizes = Sizes(script, @"int\[\] WindowsSizes = \[([0-9, ]+)\]");
        var linuxSizes = Sizes(script, @"int\[\] LinuxSizes = \[([0-9, ]+)\]");
        Check.True(windowsSizes.Count > 0 && linuxSizes.Count > 0, "tools/make-icons.cs lists the Windows and Linux sizes");

        // Generated files: the checksums the script wrote must still match the drawing and its output.
        var sums = Read("assets/AppIcon.sha256").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split("  ", 2)).ToDictionary(p => p[1], p => p[0]);
        Check.Equal("assets/AppIconBase.png", sums.Keys.FirstOrDefault(), "the checksums start with the drawing");
        var stale = sums.Where(s => !File.Exists(Path.Combine(root, s.Key))
                || Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, s.Key)))) != s.Value)
            .Select(s => s.Key).ToList();
        Check.True(stale.Count == 0,
            "every icon is what tools/make-icons.cs makes from the drawing as it is now" + Listing("run the script again; changed", stale));

        var expected = new[] { "assets/AppIconBase.png", "src/ReelsMagician.App/Assets/reelsmagician.ico",
                "src/ReelsMagician.App/Assets/reelsmagician-256.png", "src/ReelsMagician.App/Assets/reelsmagician-128.png" }
            .Concat(linuxSizes.Select(s => $"packaging/linux/icons/reelsmagician-{s}.png")).ToHashSet();
        var onDisk = Directory.GetFiles(Path.Combine(root, "packaging", "linux", "icons"))
            .Select(f => "packaging/linux/icons/" + Path.GetFileName(f));
        var unlisted = onDisk.Where(f => !sums.ContainsKey(f)).Order().ToList();
        Check.True(sums.Keys.ToHashSet().SetEquals(expected) && unlisted.Count == 0,
            "the checksums cover exactly the drawing, the app's three icon files and one Linux icon per size, and no other icon is left" +
            Listing("not made by the script", unlisted));

        // Windows: the .ico Avalonia and the .exe take their frames from.
        var ico = File.ReadAllBytes(Path.Combine(root, "src", "ReelsMagician.App", "Assets", "reelsmagician.ico"));
        var frames = IcoFrames(ico);
        Check.True(frames is not null, "reelsmagician.ico is an icon file whose frames lie inside it");
        if (frames is not null)
        {
            Check.Equal(string.Join(",", windowsSizes), string.Join(",", frames.Select(f => f.Size)),
                "reelsmagician.ico has a frame for each size Windows picks by DPI");
            var big = frames.FirstOrDefault(f => f.Size == 256);
            Check.True(big.Data is not null && PngSize(big.Data) == (256, 256), "its 256 px frame is a 256×256 PNG");
            var bitmaps = frames.Where(f => f.Size < 256).ToList();
            Check.True(bitmaps.All(f => IsDib(f.Data, f.Size)), "its smaller frames are 32-bit bitmaps with an alpha channel");
            Check.True(bitmaps.All(f => DibAlpha(f.Data, f.Size, 0, 0) == 0 && DibAlpha(f.Data, f.Size, f.Size / 2, f.Size / 2) == 255),
                "in every bitmap frame the corner is transparent and the middle fully opaque");
            Check.True(bitmaps.All(f => !DibAlphas(f.Data, f.Size).Any(a => a is >= 250 and < 255)),
                "no pixel is left almost opaque, so a light taskbar never shows through");
        }

        // Linux and the app's own images.
        var sized = linuxSizes.Select(s => ($"packaging/linux/icons/reelsmagician-{s}.png", s))
            .Append(("src/ReelsMagician.App/Assets/reelsmagician-256.png", 256))
            .Append(("src/ReelsMagician.App/Assets/reelsmagician-128.png", 128));
        var wrong = sized.Where(f => !File.Exists(Path.Combine(root, f.Item1))
                || PngSize(File.ReadAllBytes(Path.Combine(root, f.Item1))) != (f.Item2, f.Item2))
            .Select(f => f.Item1).ToList();
        Check.True(wrong.Count == 0, "every PNG icon is an RGBA PNG of the size its name says" + Listing("wrong", wrong));

        var build = Read("packaging/linux/build-appimage.sh");
        var checkScript = Read("packaging/linux/check-appimage.sh");
        Check.Equal(string.Join(" ", linuxSizes), Regex.Match(build, @"for size in ([0-9 ]+); do").Groups[1].Value,
            "build-appimage.sh installs the sizes the script makes");
        Check.Equal(string.Join(" ", linuxSizes), Regex.Match(checkScript, @"for size in ([0-9 ]+); do").Groups[1].Value,
            "check-appimage.sh looks for the same sizes");

        // A Wayland compositor finds the icon only through the app_id, which Avalonia takes from
        // the assembly name, the desktop entry of that name, and its Icon= in the hicolor theme.
        var project = Read("src/ReelsMagician.App/ReelsMagician.App.csproj");
        var assembly = Regex.Match(project, "<AssemblyName>([^<]+)</AssemblyName>").Groups[1].Value;
        Check.Equal("ReelsMagician", assembly, "the app's assembly name, and so its Wayland app_id");
        var desktop = Path.Combine(root, "packaging", "linux", $"{assembly}.desktop");
        Check.True(File.Exists(desktop), $"the desktop entry is named after the app_id: packaging/linux/{assembly}.desktop");
        var entry = File.Exists(desktop) ? Read($"packaging/linux/{assembly}.desktop") : "";
        Check.Contains(entry, $"\nStartupWMClass={assembly}\n", "its StartupWMClass is the app_id too");
        Check.Contains(entry, "\nIcon=reelsmagician\n", "its Icon= is reelsmagician");
        Check.Contains(build, "\"$icons/reelsmagician.png\"", "build-appimage.sh installs the icons under that name");
        Check.Contains(build, $"\"$here/{assembly}.desktop\" \"$appdir/usr/share/applications/{assembly}.desktop\"",
            "and installs the desktop entry under the app_id");

        // The app loads its icons by path at run time, so a file left out of the resources only
        // fails when the window opens.
        Check.Contains(project, @"<ApplicationIcon>Assets\reelsmagician.ico</ApplicationIcon>", "the .exe gets reelsmagician.ico");
        var resources = Regex.Matches(project, "<AvaloniaResource Include=\"([^\"]+)\"").SelectMany(m => m.Groups[1].Value.Split(';'))
            .Select(p => Path.GetFileName(p.Replace('\\', '/'))).ToHashSet();
        var loaded = Regex.Matches(Read("src/ReelsMagician.App/App.axaml.cs") + Read("src/ReelsMagician.App/MainWindow.axaml"),
                @"reelsmagician[a-z0-9-]*\.(?:ico|png)")
            .Select(m => m.Value).ToHashSet();
        var notBundled = loaded.Except(resources).Order().ToList();
        Check.True(loaded.Count >= 3 && notBundled.Count == 0,
            "the icons the app loads (window icons and the header image) are in its resources" + Listing("missing", notBundled));
    }

    private static List<int> Sizes(string text, string pattern) =>
        [.. Regex.Match(text, pattern).Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)];

    private static List<(int Size, byte[] Data)>? IcoFrames(byte[] ico)
    {
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(0)) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)) != 1) return null;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        if (ico.Length < 6 + 16 * count) return null;
        var frames = new List<(int, byte[])>();
        for (var i = 0; i < count; i++)
        {
            var entry = ico.AsSpan(6 + 16 * i, 16);
            var size = entry[0] == 0 ? 256 : entry[0];
            var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (entry[0] != entry[1] || BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]) != 32
                || offset + (long)length > ico.Length) return null;
            frames.Add((size, ico.AsSpan((int)offset, (int)length).ToArray()));
        }
        return frames;
    }

    private static (int, int)? PngSize(byte[] png)
    {
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 26 || !png.AsSpan(0, 8).SequenceEqual(signature) || png[25] != 6) return null;   // 6: RGBA
        return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
    }

    private static bool IsDib(byte[] dib, int size) =>
        dib.Length >= 40 + size * size * 4
        && BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(0)) == 40
        && BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4)) == size
        && BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)) == size * 2
        && BinaryPrimitives.ReadInt16LittleEndian(dib.AsSpan(14)) == 32;

    // Rows are stored bottom up, as BGRA.
    private static int DibAlpha(byte[] dib, int size, int x, int y) =>
        IsDib(dib, size) ? dib[40 + ((size - 1 - y) * size + x) * 4 + 3] : -1;

    private static IEnumerable<int> DibAlphas(byte[] dib, int size) =>
        IsDib(dib, size) ? Enumerable.Range(0, size * size).Select(i => (int)dib[40 + i * 4 + 3]) : [];

    private static string Listing(string what, List<string> items) =>
        items.Count == 0 ? "" : $" ({what}: {string.Join(", ", items)})";

    private static string? FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ReelsMagician.slnx"))) return dir.FullName;
        return null;
    }
}
