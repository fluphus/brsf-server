using System.Security.Cryptography;
using System.Text.Json;
using Brs.Local.Transport;

namespace Brs.Local.Content;

// A single reviewed local package, never a generic filesystem-to-HTTP mapping.
public sealed class LocalContentOverlay
{
    public const string ExpectedManifestSha256 = "c26a609471b4aee8209b87ae48ecdafd8f4030bcdda921e66e7116818e9e12fb";
    public const string AllOpenManifestSha256 = "3675c911bf5036badd96ee290830b4ece3eae5eab320682666b3091ef37191c0";
    public const string GachaDedupManifestSha256 = "767fbf996162daff88aaebd8b3c550463ca995a4e378225e07442080ee5c871e";
    public const string SingleExpertManifestSha256 = "91fd93a2ab3573fe4cd7b7ce83e22fbcfd247b3aff4ab8c91ad0b161565675e3";
    public const string PortableSingleExpertManifestSha256 = "b908d9b57bdef8fb1a4f11fe4bf935967cfd901411c11987f73e9a78d6521b08";
    public const string SourceMasterSha256 = "8082ce3e3746a8e38d58c7e640892b80caef374f767a1d39f84db94d7108945b";
    public const string CatalogStorageKey = "local-content-v1/catalog";
    public const string MasterStorageKey = "local-content-v1/master";
    private readonly byte[] catalogBytes;
    private readonly byte[] masterBytes;
    public int ResourceRevision { get; }
    public int MasterRevision { get; }
    public string Fingerprint { get; }
    public string ManifestSha256 { get; }
    public int? ActiveExpertQuestTypeId { get; }
    public string CatalogPath => $"/Android/{ResourceRevision}/" + ContentSnapshot.CatalogName;
    public string MasterPath => $"/Android/MasterData/{MasterRevision}/" + ContentSnapshot.MasterName;
    public long CatalogLength => catalogBytes.Length;
    public long MasterLength => masterBytes.Length;
    public string CatalogHash { get; }
    public string MasterHash { get; }

    private LocalContentOverlay(byte[] catalogBytes, byte[] masterBytes, bool allOpen, bool gachaDedup, bool singleExpert, int? activeExpert, string manifestSha256)
    {
        ResourceRevision = singleExpert ? 146 : gachaDedup ? 145 : allOpen ? 144 : 143; MasterRevision = singleExpert ? 140 : gachaDedup ? 139 : allOpen ? 138 : 137;
        Fingerprint = singleExpert ? SingleExpertManifestSha256 : gachaDedup ? GachaDedupManifestSha256 : allOpen ? AllOpenManifestSha256 : ExpectedManifestSha256;
        ManifestSha256 = manifestSha256;
        ActiveExpertQuestTypeId=activeExpert;
        this.catalogBytes = catalogBytes;
        this.masterBytes = masterBytes;
        CatalogHash = Convert.ToHexStringLower(SHA256.HashData(catalogBytes));
        MasterHash = Convert.ToHexStringLower(MD5.HashData(masterBytes));
    }

    private Func<ExpertPeriodContentSnapshot>? captureExpertContent;
    public ExpertPeriodContentSnapshot? CaptureDynamicContent() => captureExpertContent?.Invoke();
    public void ConfigureDynamicExpertContent(Brs.Local.Accounts.AccountStore accounts)
    {
        if (Fingerprint != SingleExpertManifestSha256 || captureExpertContent is not null)
            throw new InvalidDataException("Dynamic Expert content requires the accepted baseline.");
        var factory = new ExpertPeriodContentSnapshot.Factory(catalogBytes, masterBytes);
        accounts.ConfigureExpertPeriodContent(selection => factory.Prepare(selection, accounts.TryCaptureExpertPeriodContent() as ExpertPeriodContentSnapshot));
        captureExpertContent = () => (ExpertPeriodContentSnapshot)accounts.CaptureExpertPeriodContent();
    }

    public static LocalContentOverlay Load(string manifestPath, string originalCatalogPath)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        if (Path.GetFileName(fullManifestPath) != "manifest.json")
            throw new InvalidDataException("Local content requires its fixed manifest basename.");
        var manifestBytes = ReadBounded(fullManifestPath, 64 * 1024);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        var allOpen = fingerprint == AllOpenManifestSha256;
        var gachaDedup = fingerprint == GachaDedupManifestSha256;
        // Removing unused source-path metadata preserves the existing policy identity.
        var singleExpert = fingerprint == SingleExpertManifestSha256 || fingerprint == PortableSingleExpertManifestSha256;
        if (!allOpen && !gachaDedup && !singleExpert && fingerprint != ExpectedManifestSha256) throw new InvalidDataException("Unsupported local content manifest fingerprint.");
        using var manifest = JsonDocument.Parse(manifestBytes);
        var root = manifest.RootElement;
        if (root.GetProperty("policy_version").GetString() != (singleExpert ? "local-content-v4-single-expert" : gachaDedup ? "local-content-v3-gacha-dedup" : allOpen ? "local-content-v2-all-open" : "local-content-v1-dates-only") ||
            root.GetProperty("source_native_sha256").GetString() != VerifiedClient.NativeSha256 ||
            root.GetProperty("source_metadata_sha256").GetString() != VerifiedClient.MetadataSha256 ||
            root.GetProperty("source_files").GetProperty("master.decoded.bin").GetProperty("sha256").GetString() != SourceMasterSha256 ||
            root.GetProperty("source_files").GetProperty("catalog.decoded.json").GetProperty("sha256").GetString() != ContentSnapshot.CatalogSha256)
            throw new InvalidDataException("Local content has unsupported source pins.");
        RequireVersions(root.GetProperty("original_versions"), "142", "136");
        RequireVersions(root.GetProperty("local_versions"), singleExpert ? "146" : gachaDedup ? "145" : allOpen ? "144" : "143", singleExpert ? "140" : gachaDedup ? "139" : allOpen ? "138" : "137");
        if (allOpen || gachaDedup || singleExpert)
        {
            RequireVersions(root.GetProperty("previous_versions"), singleExpert ? "145" : gachaDedup ? "144" : "143", singleExpert ? "139" : gachaDedup ? "138" : "137");
            if (root.GetProperty("base_manifest").GetProperty("sha256").GetString() != (singleExpert ? GachaDedupManifestSha256 : gachaDedup ? AllOpenManifestSha256 : ExpectedManifestSha256))
                throw new InvalidDataException("Unsupported local content predecessor.");
        }
        int? activeExpert=null;
        if(singleExpert)
        {
            var schedule=root.GetProperty("expert_schedule");
            var ids=schedule.GetProperty("quest_type_ids").EnumerateArray().Select(x=>x.GetInt32()).ToArray();
            var selected=schedule.GetProperty("active_quest_type_id").GetInt32();
            if(schedule.GetProperty("policy_version").GetString()!="single-original-expert-v1"||!ids.SequenceEqual(new[]{300101,300201,300202,300203,300204,300205,300206})||!ids.Contains(selected))throw new InvalidDataException("Invalid original Expert schedule.");
            activeExpert=selected;
        }
        var directory = Path.GetDirectoryName(fullManifestPath)!;
        var declared = root.GetProperty("files");
        var expectedNames = new[] { "master.decoded.bin", "master.encoded.bin", "catalog.decoded.json", "catalog.encoded.bin" };
        var names = declared.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != expectedNames.Length || !names.ToHashSet(StringComparer.Ordinal).SetEquals(expectedNames))
            throw new InvalidDataException("Local content must declare the four fixed sibling files.");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in expectedNames)
        {
            var data = ReadBounded(Path.Combine(directory, name), 16 * 1024 * 1024);
            var spec = declared.GetProperty(name);
            if (data.LongLength != spec.GetProperty("size").GetInt64() ||
                Convert.ToHexStringLower(MD5.HashData(data)) != spec.GetProperty("md5").GetString())
                throw new InvalidDataException("Local content file length or MD5 mismatch.");
            VerifiedClient.RequireHash(data, spec.GetProperty("sha256").GetString()!);
            files.Add(name, data);
        }
        var originalCatalogBytes = ReadBounded(originalCatalogPath, 16 * 1024 * 1024);
        VerifiedClient.RequireHash(originalCatalogBytes, ContentSnapshot.CatalogSha256);
        using var original = JsonDocument.Parse(originalCatalogBytes);
        using var local = JsonDocument.Parse(files["catalog.decoded.json"]);
        ValidateCatalog(original.RootElement, local.RootElement, root, files["master.encoded.bin"], allOpen, gachaDedup, singleExpert);
        return new LocalContentOverlay(files["catalog.encoded.bin"], files["master.encoded.bin"], allOpen, gachaDedup, singleExpert, activeExpert, fingerprint);
    }

    private static void RequireVersions(JsonElement versions, string resource, string master)
    {
        if (versions.GetProperty("resource").GetString() != resource || versions.GetProperty("master").GetString() != master)
            throw new InvalidDataException("Unsupported local content revisions.");
    }

    private static void ValidateCatalog(JsonElement original, JsonElement local, JsonElement manifest, byte[] masterBytes, bool allOpen, bool gachaDedup, bool singleExpert)
    {
        var keys = original.EnumerateObject().Select(property => property.Name).ToArray();
        var newKeys = local.EnumerateObject().Select(property => property.Name).ToArray();
        if (keys.Length != newKeys.Length || !keys.ToHashSet(StringComparer.Ordinal).SetEquals(newKeys) ||
            local.GetProperty("ResourceVersion").GetString() != (singleExpert ? "146" : gachaDedup ? "145" : allOpen ? "144" : "143") || local.GetProperty("MasterVersion").GetString() != (singleExpert ? "140" : gachaDedup ? "139" : allOpen ? "138" : "137"))
            throw new InvalidDataException("Invalid local catalog shape or revisions.");
        foreach (var key in keys.Where(key => key is not ("ResourceVersion" or "MasterVersion" or "TotalSize" or "Items")))
            if (!SameValue(original.GetProperty(key), local.GetProperty(key)))
                throw new InvalidDataException("Unexpected local catalog root change.");
        var oldItems = original.GetProperty("Items").EnumerateArray().ToArray();
        var newItems = local.GetProperty("Items").EnumerateArray().ToArray();
        if (oldItems.Length != 14374 || newItems.Length != oldItems.Length)
            throw new InvalidDataException("Invalid local catalog asset count.");
        long total = 0;
        var masterCount = 0;
        for (var i = 0; i < oldItems.Length; i++)
        {
            var oldItem = oldItems[i];
            var newItem = newItems[i];
            total = checked(total + newItem.GetProperty("Size").GetInt64());
            if (oldItem.GetProperty("LoadPath").GetString() != "MasterData")
            {
                if (!SameValue(oldItem, newItem)) throw new InvalidDataException("A non-master catalog asset changed.");
                continue;
            }
            masterCount++;
            if (!SameObjectExcept(oldItem, newItem, "Size", "Hash") ||
                newItem.GetProperty("Size").GetInt64() != masterBytes.LongLength ||
                newItem.GetProperty("Hash").GetString() != Convert.ToHexStringLower(MD5.HashData(masterBytes)) ||
                newItem.GetProperty("FileName").GetString() != manifest.GetProperty("master_data_catalog_filename").GetString() ||
                newItem.GetProperty("EncryptionType").GetInt32() != 2 ||
                !SameValue(manifest.GetProperty("master_catalog_old"), oldItem) ||
                !SameValue(manifest.GetProperty("master_catalog_new"), newItem))
                throw new InvalidDataException("The local MasterData catalog entry is invalid.");
        }
        if (masterCount != 1 || total != local.GetProperty("TotalSize").GetInt64() ||
            manifest.GetProperty("total_size").GetProperty("old").GetInt64() != original.GetProperty("TotalSize").GetInt64() ||
            manifest.GetProperty("total_size").GetProperty("new").GetInt64() != total)
            throw new InvalidDataException("The local catalog total is invalid.");
    }

    private static bool SameObjectExcept(JsonElement first, JsonElement second, params string[] except)
    {
        var a = first.EnumerateObject().ToArray();
        var b = second.EnumerateObject().ToArray();
        return a.Length == b.Length && a.Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(b.Select(p => p.Name)) &&
            a.All(p => except.Contains(p.Name, StringComparer.Ordinal) || SameValue(p.Value, second.GetProperty(p.Name)));
    }

    private static bool SameValue(JsonElement first, JsonElement second)
    {
        if (first.ValueKind != second.ValueKind) return false;
        return first.ValueKind switch
        {
            JsonValueKind.Object => SameObjectExcept(first, second),
            JsonValueKind.Array => first.GetArrayLength() == second.GetArrayLength() &&
                first.EnumerateArray().Zip(second.EnumerateArray()).All(pair => SameValue(pair.First, pair.Second)),
            JsonValueKind.String => first.GetString() == second.GetString(),
            JsonValueKind.Number => first.GetRawText() == second.GetRawText(),
            _ => first.GetRawText() == second.GetRawText()
        };
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Local content cannot use a reparse-point file.");
        for (var parent = file.Directory; parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Local content cannot traverse a reparse-point directory.");
        using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 || input.Length > maximum) throw new InvalidDataException("Invalid local content file size.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    internal bool TryGetContent(string storageKey, out ReadOnlyMemory<byte> bytes)
    {
        bytes = storageKey switch
        {
            CatalogStorageKey => catalogBytes,
            MasterStorageKey => masterBytes,
            _ => default
        };
        return !bytes.IsEmpty;
    }
}
