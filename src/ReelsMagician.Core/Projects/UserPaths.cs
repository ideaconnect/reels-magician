using System.Globalization;
using System.Text;

namespace ReelsMagician.Core.Projects;

/// <summary>
/// Paths the user types: a local source and the folder reels go to. ffmpeg runs in a project's
/// own work folder, so a relative path would mean one place when the app checks it and another
/// when ffmpeg opens it. Every typed path is made full before anything uses it. Also the names the
/// app gives the files it writes there.
/// </summary>
public static class UserPaths
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// A leading <c>~</c> read as the home folder, as a shell reads it: <c>~</c> and <c>~/Videos</c>,
    /// and on Windows <c>~\Videos</c> too. Anything else comes back unchanged, <c>~user</c> included.
    /// </summary>
    public static string ExpandHome(string path, string? home = null)
    {
        if (path.Length == 0 || path[0] != '~') return path;
        if (path.Length == 1) return home ?? Home;
        if (path[1] != '/' && path[1] != Path.DirectorySeparatorChar) return path;
        return Path.Join(home ?? Home, path[2..]);
    }

    /// <summary>
    /// The full path of the folder reels are written to. A relative folder is taken from the home
    /// folder rather than from wherever the app happened to start, and an empty box means
    /// <paramref name="fallback"/>.
    /// </summary>
    public static string ResolveOutputFolder(string? typed, string fallback, string? home = null)
    {
        home ??= Home;
        var path = (typed ?? "").Trim().Trim('"', '\'').Trim();
        if (path.Length == 0) path = fallback;
        path = ExpandHome(path, home);
        return Path.IsPathFullyQualified(home) ? Path.GetFullPath(path, home) : Path.GetFullPath(path);
    }

    /// <summary>
    /// A file name made from a title, using only ASCII letters, digits and underscores, so the same
    /// title gives the same name on every system, and the file copies anywhere: an exFAT or FAT
    /// drive, a Windows share, a phone. Accented letters lose the accent (ą is a), the few letters
    /// that are not an accented ASCII letter are spelt out (ł is l, ß is ss), and every run of
    /// anything else becomes one underscore. A title with nothing usable gives <paramref name="fallback"/>.
    /// </summary>
    public static string FileNameFromTitle(string? title, string fallback = "reel", int maxLength = 60)
    {
        var name = new StringBuilder();
        var gap = false;
        // Compatibility decomposition splits an accented letter into the letter and its accent,
        // and turns forms such as fullwidth letters or ligatures into plain ones.
        foreach (var c in (title ?? "").Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var ascii = char.IsAsciiLetterOrDigit(c) ? c.ToString() : Spelt(c);
            if (ascii is null)
            {
                gap = true;
                continue;
            }
            if (gap && name.Length > 0) name.Append('_');
            gap = false;
            name.Append(ascii);
        }

        var result = name.Length <= maxLength ? name.ToString() : name.ToString(0, maxLength).TrimEnd('_');
        return result.Length > 0 ? result : fallback;
    }

    // Letters that decomposition leaves alone, as they are usually written in ASCII.
    private static string? Spelt(char c) => c switch
    {
        'ł' => "l", 'Ł' => "L",
        'đ' => "d", 'Đ' => "D",
        'ø' => "o", 'Ø' => "O",
        'æ' => "ae", 'Æ' => "AE",
        'œ' => "oe", 'Œ' => "OE",
        'ß' => "ss",
        'þ' => "th", 'Þ' => "Th",
        'ð' => "d", 'Ð' => "D",
        'ı' => "i",
        _ => null,
    };
}
