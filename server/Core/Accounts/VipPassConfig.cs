using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brs.Local.Accounts;

public sealed record VipPassConfig(string PolicyVersion, bool LocalMode, int MasterRevision,
    string SourceMasterSha256, string SourceVipExcerptSha256, string SourceLoginExcerptSha256,
    string ProductId, int ParentProductId, int ContentsId, int ContentsType, int Price, int Days,
    int PaidQty, int LoginBonusId, int LoginBonusItemGroupId, int DailyFreeQty, int ReceivePlace,
    int ConditionValue, string LocalReceiptMarker, string OpenDate, int DayBoundaryHourJst,
    int LifetimeSeconds, int FirstRewardBusinessDayOffset, int PresentExpireTimestamp, int TrainingRewardRate,
    string LocalTimingPolicy, string LocalPresentExpiryPolicy, string LoginAnimationPolicy)
{
    public const string ExpectedFingerprint = "e5181d505577165402b5809a1a672be1ae0d97f544e9b4fc9435476276537077";
    public static VipPassConfig Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 16384 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid VIP pass configuration.");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ExpectedFingerprint)
            throw new InvalidDataException("VIP pass snapshot fingerprint mismatch.");
        var value = JsonSerializer.Deserialize<VipPassConfig>(bytes, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Missing VIP pass snapshot.");
        if (value.PolicyVersion != "local-vip-pass-v1" || !value.LocalMode || value.MasterRevision != 136 ||
            value.SourceMasterSha256 != "8082ce3e3746a8e38d58c7e640892b80caef374f767a1d39f84db94d7108945b" ||
            value.SourceVipExcerptSha256 != "aa3ae8670d58c48c3a9759b12050a4904e21bd36fe8e1bbdf775f0ce0afda05c" ||
            value.SourceLoginExcerptSha256 != "3fb60c72dd1eaaaacfb31c2da3970484dfcb942ec485b1a77252414dd90b2da5" ||
            value.ProductId != "vip_pass_2" || value.ParentProductId != 4001 || value.ContentsId != 4001 || value.ContentsType != 4 ||
            value.Price != 1000 || value.Days != 30 || value.PaidQty != 500 || value.LoginBonusId != 9001 ||
            value.LoginBonusItemGroupId != 90010001 || value.DailyFreeQty != 50 || value.ReceivePlace != 1600 ||
            value.ConditionValue != 9001 || value.LocalReceiptMarker != "daruma_dummy_receipt" ||
            value.OpenDate != "2023/02/28 13:00:00" || value.DayBoundaryHourJst != 5 ||
            value.LifetimeSeconds != 2592000 || value.FirstRewardBusinessDayOffset != 1 || value.PresentExpireTimestamp != 0 ||
            value.TrainingRewardRate != 2 || value.LocalTimingPolicy != "exact30days-next05businessday-no-makeup" ||
            value.LocalPresentExpiryPolicy != "no-expiry-original-ttl-unknown" || value.LoginAnimationPolicy != "empty-until-normal1001-supported")
            throw new InvalidDataException("Unsupported VIP pass policy.");
        return value;
    }
}
