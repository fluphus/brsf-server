using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brs.Local.Transport;

namespace Brs.Local.Accounts;

public sealed record InventoryMaterial(int EquipId, int EquipGroupId, int EquipType, int UsageType, string FreePropertyIds, int SlotCount)
{
    public GachaEquip Equipment => new(EquipId, EquipGroupId, FreePropertyIds, SlotCount);
}

// Complete original usage3/4 classification; independent of any shop SKU.
public sealed record InventoryMaterialConfig(string PolicyVersion, int ResourceRevision, int MasterRevision,
    int SourceMasterRevision, string SourceMasterSha256, string SourceNativeSha256, string SourceExcerptSha256,
    int QuantityCap, InventoryMaterial[] Materials)
{
    public const string ExpectedFingerprint = "674766d5cdc55f77007cd876d57e549bef565d5b0c45ebbf2a7503a2302af6d5";
    public static InventoryMaterialConfig Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 16384 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid equipment material configuration.");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ExpectedFingerprint)
            throw new InvalidDataException("Equipment material fingerprint mismatch.");
        var config = JsonSerializer.Deserialize<InventoryMaterialConfig>(bytes, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Missing equipment material configuration.");
        if (config.PolicyVersion != "local-equipment-material-v1" || config.ResourceRevision != 144 || config.MasterRevision != 138 ||
            config.SourceMasterRevision != 136 || config.SourceMasterSha256 != Brs.Local.Content.LocalContentOverlay.SourceMasterSha256 ||
            config.SourceNativeSha256 != VerifiedClient.NativeSha256 ||
            config.SourceExcerptSha256 != "6520515f603f1f4c5f6a6bd922c0fe1b7c957a2e2b8729e8992de5e9fd81cc4c" ||
            config.QuantityCap != 99999999 || config.Materials.Length != 8 || config.Materials.Select(m => m.EquipId).Distinct().Count() != 8 ||
            config.Materials.Any(m => m.EquipId <= 0 || m.EquipGroupId <= 0 || m.SlotCount != 0 || m.FreePropertyIds != "" ||
                m.EquipType is not (301 or 302) || m.UsageType != (m.EquipType == 301 ? 3 : 4)))
            throw new InvalidDataException("Unsupported equipment material policy.");
        return config;
    }
    public InventoryMaterial? Find(int equipId) => Materials.SingleOrDefault(m => m.EquipId == equipId);
}
