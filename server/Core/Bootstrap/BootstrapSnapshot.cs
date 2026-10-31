using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Brs.Local.Content;
using Brs.Local.Transport;

namespace Brs.Local.Bootstrap;

// Explicit immutable local content, unrelated to player/account state.
public sealed class BootstrapSnapshot
{
    public const string NoticeName = "b49abd1a2809b4ea813889fe93080913";
    // NoticeVersionUpdater ctor appends /Android to the CDN root (0x19ec2b4..0x19ec2d8).
    public const string NoticePath = "/Android/info/1/" + NoticeName;
    private readonly byte[] notice;
    public string Fingerprint { get; }
    private readonly int baseResourceRevision;
    private readonly LocalContentOverlay? dynamicOverlay;
    public int ResourceRevision => dynamicOverlay?.CaptureDynamicContent()?.ResourceRevision ?? baseResourceRevision;
    private readonly int baseMasterRevision;
    public int MasterRevision => dynamicOverlay?.CaptureDynamicContent()?.MasterRevision ?? baseMasterRevision;

    private BootstrapSnapshot(byte[] config, byte[] notice, LocalContentOverlay? overlay)
    {
        this.notice = notice;
        dynamicOverlay = overlay;
        baseResourceRevision = overlay?.ResourceRevision ?? 142;
        baseMasterRevision = overlay?.MasterRevision ?? 136;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var bytes in new[] { config, notice })
        {
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        if (overlay is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(overlay.Fingerprint);
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        Fingerprint = Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static BootstrapSnapshot Load(string configPath, string catalogPath, LocalContentOverlay? overlay = null)
    {
        var config = ReadBounded(configPath, 64 * 1024);
        using var document = JsonDocument.Parse(config, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        ExactKeys(root, "schema_version", "resource_revision", "master_revision", "clock_offset", "notice", "terms", "privacy");
        RequireInt(root, "schema_version", 1);
        RequireInt(root, "resource_revision", 142);
        RequireInt(root, "master_revision", 136);
        if (root.GetProperty("clock_offset").ValueKind != JsonValueKind.String ||
            root.GetProperty("clock_offset").GetString() != "+09:00")
            throw new InvalidDataException("Explicit local clock_offset +09:00 is required.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var entry = root.GetProperty("notice");
        ExactKeys(entry, "revision", "file");
        RequireInt(entry, "revision", 1);
        var file = entry.GetProperty("file");
        var relative = file.ValueKind == JsonValueKind.String ? file.GetString() : null;
        if (string.IsNullOrWhiteSpace(relative) || relative is "." or ".." ||
            relative != Path.GetFileName(relative) || relative.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new InvalidDataException("Notice file must be an explicit sibling filename.");
        var notice = ReadBounded(Path.Combine(directory, relative), 64 * 1024);
        _ = new UTF8Encoding(false, true).GetString(notice);
        using (var data = JsonDocument.Parse(notice, new JsonDocumentOptions { MaxDepth = 4 }))
        {
            ExactKeys(data.RootElement, "info");
            var info = data.RootElement.GetProperty("info");
            if (info.ValueKind != JsonValueKind.Array || info.GetArrayLength() != 0)
                throw new InvalidDataException("This local bootstrap requires an explicit empty info array.");
        }
        // Consent text is an original RawAsset, not an invented local HTTP route.
        var catalogBytes = ReadBounded(catalogPath, 16 * 1024 * 1024);
        VerifiedClient.RequireHash(catalogBytes, ContentSnapshot.CatalogSha256);
        using var catalog = JsonDocument.Parse(catalogBytes);
        foreach (var (name, loadPath) in new[] { ("terms", "TermOfService/term_of_service"),
            ("privacy", "TermOfService/privacy_policy") })
        {
            var documentEntry = root.GetProperty(name);
            ExactKeys(documentEntry, "revision", "load_path", "file_name", "md5");
            RequireInt(documentEntry, "revision", 1);
            if (documentEntry.GetProperty("load_path").GetString() != loadPath)
                throw new InvalidDataException("Consent binding has an incorrect logical path.");
            var matches = catalog.RootElement.GetProperty("Items").EnumerateArray()
                .Where(item => item.GetProperty("LoadPath").GetString() == loadPath).ToArray();
            if (matches.Length != 1 ||
                matches[0].GetProperty("FileName").GetString() != documentEntry.GetProperty("file_name").GetString() ||
                matches[0].GetProperty("Hash").GetString() != documentEntry.GetProperty("md5").GetString())
                throw new InvalidDataException("Consent binding does not match the verified catalog.");
        }
        return new BootstrapSnapshot(config, notice, overlay);
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Bootstrap content cannot be a reparse-point file.");
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length == 0 || source.Length > maximum)
            throw new InvalidDataException("Bootstrap content size is invalid.");
        var bytes = new byte[checked((int)source.Length)];
        source.ReadExactly(bytes);
        return bytes;
    }

    private static void ExactKeys(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Bootstrap JSON must be an object.");
        var actual = element.EnumerateObject().Select(p => p.Name).ToArray();
        if (actual.Length != keys.Length || !actual.ToHashSet(StringComparer.Ordinal).SetEquals(keys))
            throw new InvalidDataException("Missing, duplicate or unknown bootstrap JSON property.");
    }

    private static void RequireInt(JsonElement element, string key, int expected)
    {
        var value = element.GetProperty(key);
        if (!value.TryGetInt32(out var number) || number != expected)
            throw new InvalidDataException("Unsupported bootstrap revision or schema.");
    }

    public byte[] Response(string? nonce = null, object? action = null, object[]? sendPresentBoxItems = null, int? presentItemBadge = null, int? missionBadge = null,int? presentEquipBadge=null,int? beginnerBadge=null,int? newFollowerBadge=null,bool isSendPresentBox=false,bool? beginnerClearBadge=null,int protocolErrorId=0,string protocolErrorMessage="")
    {
        var content = dynamicOverlay?.CaptureDynamicContent();
        var common = new
        {
            error_id = protocolErrorId,error_msg=protocolErrorMessage,
            datetime = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9))
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            resource_rev = content?.ResourceRevision ?? baseResourceRevision, master_data_rev = content?.MasterRevision ?? baseMasterRevision, info_rev = 1,
            terms_ver = 1, policy_ver = 1, is_service_close = 0, is_update_required = false,
            // The original gacha suspend writer iterates this Common array
            // unconditionally, even when no acquired item went to the gift box.
            send_present_box_items = sendPresentBoxItems ?? Array.Empty<object>(), is_send_present_box=isSendPresentBox
        };
        object commonValue = common;
        if (presentItemBadge is not null || missionBadge is not null || presentEquipBadge is not null || beginnerBadge is not null || newFollowerBadge is not null || beginnerClearBadge is not null)
        {
            var badge = presentItemBadge ?? 0;
            if (badge < 0 || missionBadge < 0 || presentEquipBadge<0 || beginnerBadge<0 || newFollowerBadge<0) throw new InvalidDataException("Invalid present item badge.");
            var projected = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(common))!.AsObject();
            projected["badge"] = JsonSerializer.SerializeToNode(new { present_item = badge, present_equip = presentEquipBadge ?? 0,
                new_follower_count = newFollowerBadge ?? 0, mission = missionBadge ?? 0, beginner_mission = beginnerBadge ?? 0, beginner_mission_clear = beginnerClearBadge ?? false });
            commonValue = projected;
        }
        action ??= nonce is null ? null : new { nonce };
        return action is null ? JsonSerializer.SerializeToUtf8Bytes(new { common = commonValue })
            : JsonSerializer.SerializeToUtf8Bytes(new { common = commonValue, action });
    }

    public bool TryResolveNotice(string path, IReadOnlyDictionary<string, IReadOnlyList<string>> query, out byte[]? bytes)
    {
        bytes = null;
        if (path == NoticePath && query.Count == 1 && query.TryGetValue("v", out var version) &&
            version.Count == 1 && version[0] == "1")
            bytes = notice;
        return bytes is not null;
    }
}
