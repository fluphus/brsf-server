namespace Brs.Portable.Content;

internal static class StoreSafety
{
    public static string DirectoryPath(string path, bool create = false)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(full) || string.Equals(full, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Content root cannot be a filesystem root.");
        CheckParents(full);
        if (File.Exists(full)) throw new InvalidDataException("Content root must be a directory.");
        if (create) { Directory.CreateDirectory(full); CheckParents(full); }
        else if (!System.IO.Directory.Exists(full)) throw new DirectoryNotFoundException("Content store missing.");
        return full;
    }

    public static void CheckParents(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            // LinkTarget also detects a dangling link for which Exists is false.
            var file = new FileInfo(current);
            var directory = new DirectoryInfo(current);
            if (file.LinkTarget is not null || directory.LinkTarget is not null ||
                ((file.Exists || directory.Exists) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException("Content path cannot traverse a reparse point.");
        }
    }

    public static byte[] ReadSmall(string path, int maximum)
    {
        CheckParents(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 || input.Length > maximum) throw new InvalidDataException("Invalid bounded content input.");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }
}
