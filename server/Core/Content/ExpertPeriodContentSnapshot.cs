using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Brs.Local.Accounts;

namespace Brs.Local.Content;

// One immutable publication. Old requests keep their own ContentItem/body alive
// even when a later publication evicts that generation from route lookup.
public sealed class ExpertPeriodContentSnapshot : IExpertPeriodContentSnapshot
{
    private static readonly ConditionalWeakTable<ContentItem, byte[]> Bodies = new();
    private readonly Generation current, baseline;
    private readonly Generation? previous;
    public long SelectionRevision { get; }
    public int QuestTypeId { get; }
    public int ContentPolicyVersion { get; }
    public int ResourceRevision => current.ResourceRevision;
    public int MasterRevision => current.MasterRevision;

    internal ExpertPeriodContentSnapshot(ExpertPeriodSelection selection, Generation current, Generation baseline, Generation? previous)
    { SelectionRevision = selection.Revision; QuestTypeId = selection.QuestTypeId; ContentPolicyVersion = selection.ContentPolicyVersion; this.current = current; this.baseline = baseline; this.previous = previous; }

    public bool TryResolve(string path, IReadOnlyDictionary<string, IReadOnlyList<string>> query, out ContentItem? item)
    {
        foreach (var generation in new[] { current, previous, baseline })
        {
            if (generation is null) continue;
            if (path == generation.CatalogPath && ExactVersion(query, generation.ResourceRevision)) { item = generation.Catalog; return true; }
            if (path == generation.MasterPath && query.Count == 0) { item = generation.Master; return true; }
        }
        item = null; return false;
    }

    public bool ValidAssetQuery(IReadOnlyDictionary<string, IReadOnlyList<string>> query) =>
        ExactVersion(query, 142) || ExactVersion(query, current.ResourceRevision) || ExactVersion(query, baseline.ResourceRevision) ||
        previous is not null && ExactVersion(query, previous.ResourceRevision);

    private static bool ExactVersion(IReadOnlyDictionary<string, IReadOnlyList<string>> query, int revision) =>
        query.Count == 1 && query.TryGetValue("v", out var values) && values.Count == 1 && values[0] == revision.ToString(CultureInfo.InvariantCulture);

    public static bool TryOpenBody(ContentItem item, out Stream? stream)
    {
        if (Bodies.TryGetValue(item, out var bytes)) { stream = new MemoryStream(bytes, writable: false); return true; }
        stream = null; return false;
    }

    internal sealed class Generation
    {
        public int ResourceRevision { get; }
        public int MasterRevision { get; }
        public string CatalogPath => $"/Android/{ResourceRevision}/" + ContentSnapshot.CatalogName;
        public string MasterPath => $"/Android/MasterData/{MasterRevision}/" + ContentSnapshot.MasterName;
        public ContentItem Catalog { get; }
        public ContentItem Master { get; }
        public Generation(int resource, int master, byte[] catalogBytes, byte[] masterBytes)
        {
            ResourceRevision = resource; MasterRevision = master;
            Catalog = new($"expert-period/{resource}/catalog", catalogBytes.Length, Convert.ToHexStringLower(SHA256.HashData(catalogBytes)), true);
            Master = new($"expert-period/{master}/master", masterBytes.Length, Convert.ToHexStringLower(MD5.HashData(masterBytes)), false);
            Bodies.Add(Catalog, catalogBytes); Bodies.Add(Master, masterBytes);
        }
    }

    internal sealed class Factory
    {
        private readonly Generation baseline;
        private readonly Dictionary<int, byte[]> templates = new();
        private readonly byte[] catalogJson, key, iv, xor;
        public Factory(byte[] catalogBytes, byte[] masterBytes)
        {
            using var policy = JsonDocument.Parse(Resource("policy.json", 4096));
            if (policy.RootElement.GetProperty("content_policy_version").GetInt32() != AccountStore.CurrentContentPolicyVersion)
                throw new InvalidDataException("Expert template policy differs.");
            if (policy.RootElement.GetProperty("approval").GetString() != "root-approved" ||
                policy.RootElement.GetProperty("policy_name").GetString() != ApprovedContentPolicy.Name ||
                policy.RootElement.GetProperty("approved_policy_sha256").GetString() != ApprovedContentPolicy.PolicySha256 ||
                policy.RootElement.GetProperty("approved_date_changes_sha256").GetString() != ApprovedContentPolicy.DateChangesSha256 ||
                policy.RootElement.GetProperty("template_manifest_sha256").GetString() != ApprovedContentPolicy.TemplateManifestSha256)
                throw new InvalidDataException("Expert templates are not approved.");
            baseline = new(146, 140, catalogBytes, masterBytes);
            catalogJson = Resource("catalog.json", 16 * 1024 * 1024);
            var templateManifest = Resource("manifest.json", 16384);
            if (Convert.ToHexStringLower(SHA256.HashData(templateManifest)) != ApprovedContentPolicy.TemplateManifestSha256)
                throw new InvalidDataException("Expert template manifest identity differs.");
            using var metadata = JsonDocument.Parse(templateManifest);
            foreach (var entry in metadata.RootElement.EnumerateArray())
            {
                var id = entry.GetProperty("id").GetInt32(); var bytes = Resource(id + ".bin", 4 * 1024 * 1024);
                if (bytes.Length != entry.GetProperty("length").GetInt32() || Convert.ToHexStringLower(SHA256.HashData(bytes)) != entry.GetProperty("sha256").GetString())
                    throw new InvalidDataException("Expert master template identity differs.");
                templates.Add(id, bytes);
            }
            if (!templates.Keys.Order().SequenceEqual(new[] { 300101, 300201, 300202, 300203, 300204, 300205, 300206 }))
                throw new InvalidDataException("Expert template set differs.");
            using var material = new BinaryReader(new MemoryStream(Resource("codec.bin", 1024), false));
            byte[] Field() { var length = material.ReadInt32(); if (length is < 1 or > 256) throw new InvalidDataException("Invalid resource codec."); var value = material.ReadBytes(length); if (value.Length != length) throw new InvalidDataException("Invalid resource codec."); return value; }
            key = Field(); iv = Field(); xor = Field();
            if (key.Length is not (16 or 24 or 32) || iv.Length != 16 || material.BaseStream.Position != material.BaseStream.Length) throw new InvalidDataException("Invalid resource codec.");
        }

        public ExpertPeriodContentSnapshot Prepare(ExpertPeriodSelection selection, ExpertPeriodContentSnapshot? previous)
        {
            if (!templates.TryGetValue(selection.QuestTypeId, out var master) || selection.Revision < 0 || selection.Revision > int.MaxValue - 146)
                throw new InvalidDataException("Unsupported Expert selection.");
            if (selection.Revision == 0)
            {
                if (selection.QuestTypeId != 300206 || selection.ContentPolicyVersion != 0) throw new InvalidDataException("Expert baseline differs.");
                return new(selection, baseline, baseline, null);
            }
            if (selection.ContentPolicyVersion != AccountStore.CurrentContentPolicyVersion) throw new InvalidDataException("Expert content policy differs.");
            var resource = checked(146 + (int)selection.Revision); var masterRevision = checked(140 + (int)selection.Revision);
            var catalog = JsonNode.Parse(catalogJson)!.AsObject();
            catalog["ResourceVersion"] = resource.ToString(CultureInfo.InvariantCulture); catalog["MasterVersion"] = masterRevision.ToString(CultureInfo.InvariantCulture);
            var matches = catalog["Items"]!.AsArray().Where(i => i!["LoadPath"]!.GetValue<string>() == "MasterData").ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Master catalog binding differs.");
            var entry = matches[0]!; var oldSize = entry["Size"]!.GetValue<long>();
            entry["Size"] = master.Length; entry["Hash"] = Convert.ToHexStringLower(MD5.HashData(master));
            catalog["TotalSize"] = checked(catalog["TotalSize"]!.GetValue<long>() - oldSize + master.Length);
            var json = System.Text.Encoding.UTF8.GetBytes(catalog.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            var generation = new Generation(resource, masterRevision, Encode(json), master);
            return new(selection, generation, baseline, previous?.current);
        }

        private byte[] Encode(byte[] decoded)
        {
            // K4os v0 header=0 is the existing accepted uncompressed envelope.
            var payload = new byte[checked(decoded.Length + 1)]; decoded.CopyTo(payload, 1);
            using var aes = Aes.Create(); aes.Key = key; aes.IV = iv;
            var encoded = aes.EncryptCbc(payload, iv, PaddingMode.PKCS7);
            for (var i = 0; i < encoded.Length; i++) encoded[i] ^= xor[i % xor.Length];
            return encoded;
        }

        private static byte[] Resource(string name, int maximum)
        {
            using var stream = typeof(ExpertPeriodContentSnapshot).Assembly.GetManifestResourceStream("Brs.Local.ExpertTemplates." + name)
                ?? RuntimeMaterialFiles.Open("templates/" + name)
                ?? throw new InvalidDataException("setup_required_resource_archive");
            if (stream.Length is < 1 || stream.Length > maximum) throw new InvalidDataException("Invalid Expert template length.");
            var result = new byte[checked((int)stream.Length)]; stream.ReadExactly(result); return result;
        }
    }
}
