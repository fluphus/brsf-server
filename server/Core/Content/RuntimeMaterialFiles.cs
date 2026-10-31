namespace Brs.Local.Content;

// The host selects one verified resource package before composing its runtime.
// The loader exposes only fixed names requested by the existing resource consumers.
public static class RuntimeMaterialFiles
{
    private static string? root;
    public static void Configure(string directory)
    {
        var selected = Path.GetFullPath(directory);
        var current = Interlocked.CompareExchange(ref root, selected, null);
        if (current is not null && !string.Equals(current, selected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("A different resource package is already configured in this process.");
    }
    public static Stream? Open(string relative)
    {
        if (root is null) return null;
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(s => s is "" or "." or ".."))
            throw new InvalidDataException("Invalid material name.");
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        for (var parent = new FileInfo(path).Directory; parent is not null; parent = parent.Parent)
            if (parent.LinkTarget is not null || (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException("Material path cannot traverse links.");
        if (new FileInfo(path).LinkTarget is not null) throw new InvalidDataException("Material cannot be a link.");
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }
}
