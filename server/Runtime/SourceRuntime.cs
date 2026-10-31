using System.Security.Cryptography;
using System.Text.Json;

namespace Brs.Portable.Runtime;

public static class SourceRuntime
{
    public const string DirectoryName = "runtime-source-v1";
    // Fixed source configurations plus the imported original catalog/overlay form the
    // exact current runtime. Stage elsewhere so failed preparation preserves old inputs.
    public static void Prepare(string destinationRoot, string materialsRoot)
    {
        var destination = Path.GetFullPath(destinationRoot);
        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidDataException("Invalid runtime directory.");
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            using var manifestStream = typeof(SourceRuntime).Assembly.GetManifestResourceStream("Brs.Portable.Runtime.InputManifest.json")!;
            using var buffer = new MemoryStream(); manifestStream.CopyTo(buffer); var manifest = buffer.ToArray();
            using var json = JsonDocument.Parse(manifest);
            foreach (var entry in json.RootElement.GetProperty("entries").EnumerateArray())
            {
                var relative = entry.GetProperty("relativePath").GetString()!;
                var target = Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = relative.StartsWith("rules/", StringComparison.Ordinal) || relative.StartsWith("bootstrap/", StringComparison.Ordinal)
                    ? typeof(SourceRuntime).Assembly.GetManifestResourceStream("Brs.Portable.Runtime.Defaults." + relative.Replace('/', '.'))
                    : new FileStream(Path.Combine(materialsRoot, "runtime", relative), FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input is null) throw new InvalidDataException("Source runtime default unavailable.");
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write); input.CopyTo(output);
            }
            File.WriteAllBytes(Path.Combine(stage, "runtime-input-manifest.json"), manifest);
            _ = RuntimeInputs.LoadAndVerify(stage);
            // Existing verified input trees can be reused. Never overwrite a differing tree.
            if (Directory.Exists(destination))
            {
                try { _ = RuntimeInputs.LoadAndVerify(destination); return; }
                catch (Exception error) when (error is IOException or InvalidDataException) { }
                _ = OwnedFiles(destination);
                var previous = destination + ".previous-" + Guid.NewGuid().ToString("N");
                Directory.Move(destination, previous);
                try { Directory.Move(stage, destination); }
                catch { Directory.Move(previous, destination); throw; }
                DeleteOwnedTree(previous);
            }
            else Directory.Move(stage, destination);
        }
        finally { if (Directory.Exists(stage)) DeleteOwnedTree(stage); }
    }

    private static void DeleteOwnedTree(string root)
    {
        foreach (var file in OwnedFiles(root)) File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length)) Directory.Delete(directory);
        Directory.Delete(root);
    }

    private static string[] OwnedFiles(string root)
    {
        using var manifestStream = typeof(SourceRuntime).Assembly.GetManifestResourceStream("Brs.Portable.Runtime.InputManifest.json")!;
        using var manifest = JsonDocument.Parse(manifestStream);
        var permitted = manifest.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("relativePath").GetString()!).ToHashSet(StringComparer.Ordinal);
        permitted.Add("runtime-input-manifest.json");
        var result = new List<string>();
        void Walk(string directory)
        {
            var info = new DirectoryInfo(directory);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked runtime directory.");
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked runtime input.");
                if (Directory.Exists(child)) { Walk(child); continue; }
                if (!permitted.Contains(Path.GetRelativePath(root, child).Replace('\\', '/'))) throw new InvalidDataException("Unexpected runtime input; existing data preserved.");
                result.Add(child);
            }
        }
        Walk(root); return result.ToArray();
    }

}
