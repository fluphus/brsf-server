using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brs.Local.Accounts;

public sealed record LocalPurchaseProduct(int ParentProductId, string ProductId, int PaidQty, int FreeQty,
    string OpenDate, string CloseDate)
{
    public bool IsOpen(long now) => LocalPolicyDates.IsOpen(OpenDate, CloseDate, now);
}

public sealed record LocalPurchaseConfig(string PolicyVersion, bool LocalMode, int MasterRevision,
    string SourceMasterSha256, string SourceExcerptSha256, string LocalReceiptMarker, LocalPurchaseProduct[] Products)
{
    public const string ExpectedFingerprint = "219d2fd4480f11eea11caa7a928f08ff3d2188069f439b95c9597b8a9a311fc9";
    public static LocalPurchaseConfig Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 16384 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid local purchase configuration file.");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ExpectedFingerprint)
            throw new InvalidDataException("Local purchase snapshot fingerprint mismatch.");
        var value = JsonSerializer.Deserialize<LocalPurchaseConfig>(bytes, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Missing local purchase snapshot.");
        var paid = new[] { 80, 240, 500, 750, 1450, 2450, 5000 }; var free = new[] { 0, 9, 35, 70, 177, 420, 1060 };
        if (value.PolicyVersion != "local-claim-gems-v1" || !value.LocalMode || value.MasterRevision != 136 ||
            value.SourceMasterSha256 != "8082ce3e3746a8e38d58c7e640892b80caef374f767a1d39f84db94d7108945b" ||
            value.SourceExcerptSha256 != "30fed6a5ec5e8f6d2a9d924db8c227a7e848dc3a5a96f0a88c8182a3e5433904" ||
            value.LocalReceiptMarker != "daruma_dummy_receipt" || value.Products.Length != 7)
            throw new InvalidDataException("Unsupported local purchase policy.");
        for (var i = 0; i < value.Products.Length; i++)
        {
            var product = value.Products[i];
            if (product.ParentProductId != 1001001 + i || product.ProductId != "paidcoin_normal_" + (i + 1).ToString(CultureInfo.InvariantCulture) ||
                product.PaidQty != paid[i] || product.FreeQty != free[i] ||
                product.OpenDate != "2023/01/01 00:00:00" || product.CloseDate != "2099/12/31 23:59:59")
                throw new InvalidDataException("Invalid original ordinary gem reward.");
        }
        return value;
    }
}

internal static class LocalPolicyDates
{
    internal static bool IsOpen(string open, string close, long now)
    {
        var local = DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(9)).DateTime;
        return local >= DateTime.ParseExact(open, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture) &&
               local <= DateTime.ParseExact(close, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
