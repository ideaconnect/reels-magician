using ReelsMagician.Core.Toolchain;

namespace ReelsMagician.Core.Updates;

/// <summary>
/// Updates the AppImage the app runs from, in place: the new file is downloaded into the same
/// folder under a temporary name, held to the size and digest GitHub gives for it, made
/// executable (0755) and renamed over the old one. The rename is atomic, so the path holds either
/// the old image or the whole new one, never part of one, and the running copy keeps the old
/// file open until it exits. Velopack can't update an AppImage built with appimagetool, so this
/// part is the app's own (issue #57).
/// </summary>
public static class AppImageSwap
{
    /// <summary>
    /// The temporary name beside the image: hidden, and without the .AppImage ending, so nothing
    /// lists or starts it while it is incomplete.
    /// </summary>
    public static string TemporaryPath(string appImagePath, SemVersion version) =>
        Path.Combine(Path.GetDirectoryName(appImagePath)!, $".{Path.GetFileName(appImagePath)}.{version}.download");

    /// <summary>
    /// Whether a new file can be put beside the image and renamed over it: that needs the folder
    /// to be writable, which /opt, say, isn't for a user. Tried for real with an empty file, as
    /// permissions alone don't tell (a read-only mount, an ACL). No admin rights are ever asked for.
    /// </summary>
    public static bool CanReplace(string appImagePath)
    {
        var folder = Path.GetDirectoryName(appImagePath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder) || !File.Exists(appImagePath)) return false;
        var probe = Path.Combine(folder, $".{Path.GetFileName(appImagePath)}.{Environment.ProcessId}.probe");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception) { return false; }
        finally { UpdateClient.TryDelete(probe); }
    }

    /// <summary>
    /// Downloads <paramref name="asset"/> and puts it in place of <paramref name="appImagePath"/>.
    /// On any failure the old image is left as it was and the temporary file is removed.
    /// </summary>
    public static async Task ReplaceAsync(UpdateClient client, GhAsset asset, SemVersion version, string appImagePath,
        Action<long, long>? progress, CancellationToken ct)
    {
        var temporary = TemporaryPath(appImagePath, version);
        try
        {
            await client.DownloadAsync(asset, temporary, progress, ct).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            // rename(2): replaces the old name in one step, on the same file system by construction.
            File.Move(temporary, appImagePath, overwrite: true);
        }
        finally
        {
            UpdateClient.TryDelete(temporary);
        }
    }
}
