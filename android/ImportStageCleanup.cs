using Brs.Portable.Content;

namespace Brs.AndroidLocalHost;

internal sealed record ImportStage(string Path, long Bytes);

internal static class ImportStageCleanup
{
    private static void Regular(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("unsafe_import_stage");
        for (var parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("unsafe_import_stage_parent");
    }
    private static ImportStage Validate(string root, string stage)
    {
        root = System.IO.Path.GetFullPath(root); stage = System.IO.Path.GetFullPath(stage);
        if (!string.Equals(System.IO.Path.GetDirectoryName(stage), root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("import_stage_outside_root");
        var name = System.IO.Path.GetFileName(stage);
        if (name.Length != 40 || !name.StartsWith(".import-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[8..], "N", out var id) || id.ToString("N") != name[8..] || !Directory.Exists(stage))
            throw new InvalidDataException("unrecognized_import_stage");
        Regular(stage); long bytes = 0;
        foreach (var child in Directory.EnumerateFileSystemEntries(stage))
        {
            if (System.IO.Path.GetFileName(child) is not ("content.zip" or "receipt.json") || !File.Exists(child) || Directory.Exists(child))
                throw new InvalidDataException("unexpected_import_stage_child");
            Regular(child); bytes = checked(bytes + new FileInfo(child).Length);
        }
        return new(stage, bytes);
    }
    public static IReadOnlyList<ImportStage> Scan(string root)
    {
        root = System.IO.Path.GetFullPath(root);
        if (!Directory.Exists(root)) { if (File.Exists(root)) throw new InvalidDataException("invalid_content_root"); return []; }
        Regular(root);
        return Directory.EnumerateFileSystemEntries(root, ".import-*", SearchOption.TopDirectoryOnly).Select(stage => Validate(root, stage))
            .Concat(ResourcePackage.ScanIncompleteImports(root).Select(stage => new ImportStage(stage.Path, stage.Bytes))).ToArray();
    }
    // Explicit user confirmation only; caller holds the process-wide cleanup gate.
    public static int DeleteConfirmed(string root, IReadOnlyList<ImportStage> confirmed, CancellationToken token)
    {
        var scanned = Scan(root).ToDictionary(stage => stage.Path, StringComparer.Ordinal);
        foreach (var stage in confirmed)
        {
            token.ThrowIfCancellationRequested();
            if (!scanned.TryGetValue(stage.Path, out var current) || current.Bytes != stage.Bytes)
                throw new InvalidDataException("import_stage_changed");
        }
        var count = 0;
        foreach (var stage in confirmed)
        {
            token.ThrowIfCancellationRequested();
            if (System.IO.Path.GetFileName(stage.Path).StartsWith(".package-", StringComparison.Ordinal))
            {
                ResourcePackage.DeleteIncompleteImports(root, [new(stage.Path, stage.Bytes)], token);
                count++; continue;
            }
            _ = Validate(root, stage.Path);
            foreach (var name in new[] { "receipt.json", "content.zip" })
            {
                var file = System.IO.Path.Combine(stage.Path, name);
                if (!File.Exists(file)) continue;
                Regular(file); File.SetAttributes(file, FileAttributes.Normal); File.Delete(file);
            }
            Directory.Delete(stage.Path, recursive: false); count++;
        }
        return count;
    }
}
