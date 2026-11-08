namespace ReelsMagician.Core.Projects;

/// <summary>
/// Paths the user types: a local source and the folder reels go to. ffmpeg runs in a project's
/// own work folder, so a relative path would mean one place when the app checks it and another
/// when ffmpeg opens it. Every typed path is made full before anything uses it.
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
}
