using System.Text.Json;
using System.Buffers.Binary;
using System.Globalization;

namespace Brs.Local.Management;

public sealed class ManagementAssets
{
    private readonly IReadOnlyDictionary<string, (byte[] Bytes, string ContentType)> assets;
    public ManagementAssets(IReadOnlyDictionary<string, (byte[] Bytes, string ContentType)> assets) => this.assets = assets;
    public bool TryGet(string path, out (byte[] Bytes, string ContentType) asset) => assets.TryGetValue(path, out asset);
    public bool IsThumbnail(string path) => path.StartsWith("/management/images/", StringComparison.Ordinal) &&
        assets.TryGetValue(path, out var asset) && asset.ContentType == "image/png";

    public static ManagementAssets LoadBundled()
    {
        var assets = new Dictionary<string, (byte[], string)>(StringComparer.Ordinal);
        foreach (var (name, contentType) in new[]
        {
            ("index.html", "text/html; charset=utf-8"), ("app.js", "text/javascript; charset=utf-8"),
            ("i18n.js", "text/javascript; charset=utf-8"), ("catalog-locales.js", "text/javascript; charset=utf-8"),
            ("api.js", "text/javascript; charset=utf-8"), ("styles.css", "text/css; charset=utf-8")
        }) assets.Add("/" + (name == "index.html" ? "" : name), (ReadResource(name), contentType));
        AddThumbnails(assets);
        AddItemThumbnails(assets);
        return new ManagementAssets(assets);
    }

    private static void AddThumbnails(Dictionary<string, (byte[], string)> assets)
    {
        if (!HasResource("Brs.Local.Management.ThumbnailRoutes.json")) return;
        var manifest = ReadBoundedResource("Brs.Local.Management.ThumbnailRoutes.json", 65536);
        using var json = JsonDocument.Parse(manifest);
        var root = json.RootElement;
        var fields = root.EnumerateObject().Select(p => p.Name).ToArray();
        if (fields.Length != 3 || !fields.ToHashSet(StringComparer.Ordinal).SetEquals(["formatVersion", "cards", "equipment"]) ||
            root.GetProperty("formatVersion").GetInt32() != 1) throw new InvalidDataException("Invalid thumbnail manifest.");
        var loaded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        Add("cards", 76); Add("equipment", 306);
        if (loaded.Count != 281) throw new InvalidDataException("Incomplete thumbnail resource set.");
        void Add(string category, int count)
        {
            var entries = root.GetProperty(category).EnumerateObject().ToArray();
            if (entries.Length != count) throw new InvalidDataException("Incomplete thumbnail ID map.");
            foreach (var entry in entries)
            {
                if (!CanonicalId(entry.Name)) throw new InvalidDataException("Invalid thumbnail ID.");
                var file = entry.Value.GetString() ?? throw new InvalidDataException("Missing thumbnail resource.");
                var parts = file.Split('/');
                if (parts.Length != 2 || parts[0] != category || !parts[1].EndsWith(".png", StringComparison.Ordinal) ||
                    !CanonicalId(parts[1][..^4]) || category == "cards" && parts[1] != entry.Name + ".png")
                    throw new InvalidDataException("Invalid fixed thumbnail resource name.");
                if (!loaded.TryGetValue(file, out var bytes))
                {
                    bytes = ReadBoundedResource("Brs.Local.Management.Thumbs." + category + "." + parts[1], 131072);
                    if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                        !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
                        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)) is < 1 or > 128 ||
                        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) is < 1 or > 128 || bytes[25] != 6)
                        throw new InvalidDataException("Invalid bounded RGBA thumbnail.");
                    loaded.Add(file, bytes);
                }
                assets.Add("/management/images/" + category + "/" + entry.Name + ".png", (bytes, "image/png"));
            }
        }
    }

    private static void AddItemThumbnails(Dictionary<string, (byte[], string)> assets)
    {
        if (!HasResource("Brs.Local.Management.ItemThumbnailRoutes.json")) return;
        using var json = JsonDocument.Parse(ReadBoundedResource("Brs.Local.Management.ItemThumbnailRoutes.json", 65536));
        var root = json.RootElement;
        var fields = root.EnumerateObject().Select(p => p.Name).ToArray();
        if (fields.Length != 2 || !fields.ToHashSet(StringComparer.Ordinal).SetEquals(["formatVersion", "items"]) ||
            root.GetProperty("formatVersion").GetInt32() != 1) throw new InvalidDataException("Invalid item thumbnail manifest.");
        var entries = root.GetProperty("items").EnumerateArray().ToArray();
        if (entries.Length != 230) throw new InvalidDataException("Incomplete item thumbnail set.");
        var expectedCounts = new Dictionary<int, int> { [6] = 67, [7] = 8, [11] = 76, [12] = 51, [14] = 28 };
        var actualCounts = expectedCounts.Keys.ToDictionary(type => type, _ => 0);
        var keys = new HashSet<(int Type, int Id, int EventId)>();
        foreach (var entry in entries)
        {
            var names = entry.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != 4 || !names.ToHashSet(StringComparer.Ordinal).SetEquals(["type", "id", "eventId", "file"]))
                throw new InvalidDataException("Invalid item thumbnail key.");
            var type = entry.GetProperty("type").GetInt32(); var id = entry.GetProperty("id").GetInt32();
            var eventId = entry.GetProperty("eventId").GetInt32();
            if (!expectedCounts.ContainsKey(type) || id <= 0 || (type == 14 ? eventId <= 0 : eventId != 0) || !keys.Add((type, id, eventId)))
                throw new InvalidDataException("Invalid or repeated item thumbnail identity.");
            actualCounts[type]++;
            var typeText = type.ToString(CultureInfo.InvariantCulture); var idText = id.ToString(CultureInfo.InvariantCulture);
            var eventText = eventId.ToString(CultureInfo.InvariantCulture);
            var name = idText + (type == 14 ? "_" + eventText : "") + ".png";
            if (entry.GetProperty("file").GetString() != typeText + "/" + name)
                throw new InvalidDataException("Invalid fixed item thumbnail resource name.");
            var bytes = ReadBoundedResource("Brs.Local.Management.ItemThumbs." + typeText + "." + name, 131072);
            if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)) is < 1 or > 128 ||
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) is < 1 or > 128 || bytes[25] != 6)
                throw new InvalidDataException("Invalid bounded RGBA item thumbnail.");
            var path = "/management/images/items/" + typeText + "/" + idText + (type == 14 ? "/" + eventText : "") + ".png";
            assets.Add(path, (bytes, "image/png"));
        }
        if (expectedCounts.Any(pair => actualCounts[pair.Key] != pair.Value))
            throw new InvalidDataException("Incomplete item thumbnail categories.");
    }

    private static bool CanonicalId(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
        value > 0 && value.ToString(CultureInfo.InvariantCulture) == text;

    private static string MaterialName(string name) => name switch
    {
        "Brs.Local.Management.ThumbnailRoutes.json" => "icons/thumbnail-routes.json",
        "Brs.Local.Management.ItemThumbnailRoutes.json" => "icons/item-thumbnail-routes.json",
        _ when name.StartsWith("Brs.Local.Management.Thumbs.", StringComparison.Ordinal) =>
            "icons/" + name["Brs.Local.Management.Thumbs.".Length..].Replace("cards.", "cards/").Replace("equipment.", "equipment/"),
        _ when name.StartsWith("Brs.Local.Management.ItemThumbs.", StringComparison.Ordinal) =>
            "icons/items/" + name["Brs.Local.Management.ItemThumbs.".Length..].Replace(".png", "~png").Replace('.', '/').Replace("~png", ".png"),
        _ => throw new InvalidDataException("Unknown management material.")
    };
    private static bool HasResource(string name)
    {
        using var stream = Brs.Local.Content.RuntimeMaterialFiles.Open(MaterialName(name));
        return stream is not null;
    }

    private static byte[] ReadBoundedResource(string name, int maximum)
    {
        using var source = Brs.Local.Content.RuntimeMaterialFiles.Open(MaterialName(name)) ?? throw new InvalidDataException("Management thumbnail resource unavailable.");
        using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        { if (output.Length + read > maximum) throw new InvalidDataException("Oversized management thumbnail resource."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }

    public static IReadOnlyDictionary<string, string> LoadLabels()
    {
        using var stream = typeof(ManagementAssets).Assembly.GetManifestResourceStream("Brs.Local.Management.Labels.json")
            ?? throw new InvalidDataException("setup_required_private_assets");
        var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException("Management labels unavailable.");
        if (labels.Count == 0 || labels.Any(pair => pair.Key.Length > 100 || pair.Value.Length > 500))
            throw new InvalidDataException("Management labels invalid.");
        return labels;
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(ManagementAssets).Assembly.GetManifestResourceStream("Brs.Local.ManagementWeb." + name)
            ?? throw new InvalidDataException("Management asset unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
