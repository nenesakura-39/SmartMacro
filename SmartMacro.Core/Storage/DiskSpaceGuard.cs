namespace SmartMacro.Core.Storage;

/// <summary>
/// Protects low-free-space SSDs from running out of storage during automated screenshot loops,
/// failure snapshots, and backup archive creation.
/// </summary>
public static class DiskSpaceGuard
{
    /// <summary>Minimum free disk space required before writing large artifacts (250 MB).</summary>
    public const long MinFreeBytes = 250L * 1024 * 1024;

    /// <summary>
    /// Returns true when the drive backing <paramref name="path"/> has at least <paramref name="minFreeBytes"/>
    /// of available free space, or when the drive's free space cannot be determined (e.g. UNC / virtual path).
    /// </summary>
    public static bool HasEnoughFreeSpace(string? path, out long availableBytes, long minFreeBytes = MinFreeBytes)
    {
        availableBytes = long.MaxValue;
        if (string.IsNullOrWhiteSpace(path)) return true;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root)) return true;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return true;

            availableBytes = drive.AvailableFreeSpace;
            return availableBytes >= minFreeBytes;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Throws <see cref="IOException"/> if the drive backing <paramref name="path"/> has less than
    /// <paramref name="minFreeBytes"/> of free space.
    /// </summary>
    public static void EnsureEnoughFreeSpace(string? path, string operationName, long minFreeBytes = MinFreeBytes)
    {
        if (!HasEnoughFreeSpace(path, out var availableBytes, minFreeBytes))
        {
            var freeMb = Math.Max(0, availableBytes / (1024 * 1024));
            var requiredMb = Math.Max(1, minFreeBytes / (1024 * 1024));
            throw new IOException(
                $"Low disk space ({freeMb:N0} MB free; {requiredMb:N0} MB required). Skipped {operationName} to protect your SSD.");
        }
    }
}
