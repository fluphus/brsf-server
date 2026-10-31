using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace Brs.Portable.Runtime;

public sealed record RuntimeInput(string Role, string Category, string RelativePath, long Length, string Sha256);

// Trusted manifest is embedded in the runtime assembly. The packaged copy cannot redefine the pins.
public sealed class RuntimeInputs
{
    public string Root { get; }
    public string ManifestSha256 { get; }
    public IReadOnlyList<RuntimeInput> Entries { get; }
    public int RuleCount { get; }
    private readonly IReadOnlyDictionary<string, RuntimeInput> byRole;

    private RuntimeInputs(string root, byte[] manifest)
    {
        Root = Path.GetFullPath(root);
        ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifest));
        using var json = JsonDocument.Parse(manifest);
        var source = json.RootElement;
        if (source.GetProperty("formatVersion").GetInt32() != 1 || source.GetProperty("inputCount").GetInt32() != 30 ||
            source.GetProperty("ruleCount").GetInt32() != 22) throw new InvalidDataException("Unsupported runtime input manifest.");
        var entries = source.GetProperty("entries").EnumerateArray().Select(e => new RuntimeInput(
            e.GetProperty("role").GetString()!, e.GetProperty("category").GetString()!, e.GetProperty("relativePath").GetString()!,
            e.GetProperty("length").GetInt64(), e.GetProperty("sha256").GetString()!)).ToArray();
        RuleCount = entries.Count(e => e.Category == "immutable-rule-config");
        if (entries.Length != 30 || RuleCount != 22 || entries.Select(e => e.RelativePath).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new InvalidDataException("Invalid runtime input manifest.");
        Entries = Array.AsReadOnly(entries);
        byRole = new ReadOnlyDictionary<string, RuntimeInput>(entries.ToDictionary(e => e.Role, StringComparer.Ordinal));
    }

    public string PathFor(string role) => Resolve(byRole.TryGetValue(role, out var input) ? input.RelativePath : throw new InvalidDataException("Unknown runtime role."));

    public static RuntimeInputs LoadAndVerify(string runtimeRoot)
    {
        using var resource = typeof(RuntimeInputs).Assembly.GetManifestResourceStream("Brs.Portable.Runtime.InputManifest.json")
            ?? throw new InvalidDataException("Runtime input manifest unavailable.");
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        var manifest = bytes.ToArray();
        var inputs = new RuntimeInputs(runtimeRoot, manifest);
        inputs.RequireFile("runtime-input-manifest.json", manifest.LongLength, inputs.ManifestSha256);
        foreach (var entry in inputs.Entries) inputs.RequireFile(entry.RelativePath, entry.Length, entry.Sha256);
        return inputs;
    }

    private string Resolve(string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(s => s is "" or "." or ".."))
            throw new InvalidDataException("Invalid runtime relative path.");
        var path = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Runtime input escapes root.");
        return path;
    }

    private void RequireFile(string relative, long expectedLength, string expectedSha)
    {
        var file = new FileInfo(Resolve(relative));
        if (!file.Exists) throw new FileNotFoundException("Required runtime input is missing.", relative);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Runtime input cannot be a reparse point.");
        for (var parent = file.Directory; parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Runtime input directory cannot be a reparse point.");
        using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (expectedLength < 1 || expectedLength > 16 * 1024 * 1024 || input.Length != expectedLength ||
            Convert.ToHexStringLower(SHA256.HashData(input)) != expectedSha)
            throw new InvalidDataException("Runtime input identity mismatch.");
    }
}
