using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brs.Local.Accounts;

public sealed record MiscStory(int StoryId, int StoryType, bool IsDeleted);
public sealed record MiscVoice(int CharaVoiceId, int CharaId, int CardId, int PlaceId, int ReleaseType,
    string GeneralParam1, string GeneralParam2, bool IsDeleted);
public sealed record MiscChara(int CharaId, string Birthday, bool IsDeleted);
public sealed record MiscQuest(int QuestId, int QuestType, int PartNo, int ChapterNo, int StageNo,
    int BeforeStoryId, int AfterStoryId, bool IsDeleted);

public sealed record MiscCatalog(string PolicyVersion, string SourceMasterSha256, int BirthdayTermDays,
    MiscStory[] Stories, MiscVoice[] Voices, MiscChara[] Charas, MiscQuest[] Quests)
{
    public const string ExpectedFingerprint = "90ea0ec0c22fc08ec4457afe81ee17bbd70f1e6d4aea8bbe9fcf5978c53f712a";
    public const string DateFormat = "yyyy-MM-dd HH:mm:ss";
    public static MiscCatalog Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 2*1024*1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid misc configuration.");
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ExpectedFingerprint)
            throw new InvalidDataException("Misc snapshot fingerprint mismatch.");
        var result = JsonSerializer.Deserialize<MiscCatalog>(bytes, new JsonSerializerOptions
        { PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Missing misc configuration.");
        if (result.PolicyVersion != "local-misc-archive-voice-support-v1" ||
            result.SourceMasterSha256 != "8082ce3e3746a8e38d58c7e640892b80caef374f767a1d39f84db94d7108945b" ||
            result.BirthdayTermDays != 1 || result.Stories.Length != 321 || result.Voices.Length != 2608 ||
            result.Charas.Length != 32 || result.Quests.Length != 604 ||
            result.Stories.Select(x=>x.StoryId).Distinct().Count()!=321 ||
            result.Voices.Select(x=>x.CharaVoiceId).Distinct().Count()!=2608 ||
            result.Quests.Select(x=>x.QuestId).Distinct().Count()!=604 ||
            result.Charas.Select(x=>x.CharaId).Distinct().Count()!=32 ||
            result.Voices.Any(v=>v.ReleaseType is <1 or >3 || v.ReleaseType!=1&&!result.Charas.Any(c=>c.CharaId==v.CharaId)))
            throw new InvalidDataException("Unsupported misc snapshot.");
        return result;
    }

    // Native birthday term: most recent annual birthday at midnight, inclusive
    // start and exclusive one-day end. Leap-day clamps to February 28.
    internal bool InBirthdayTerm(DateTime date, string birthday)
    {
        if (!ProfileBirthdayRequest.Valid(birthday)) return false;
        var p=birthday.Split('/');var month=int.Parse(p[0],CultureInfo.InvariantCulture);var day=int.Parse(p[1],CultureInfo.InvariantCulture);
        DateTime Birthday(int year)=>new(year,month,Math.Min(day,DateTime.DaysInMonth(year,month)));
        var start=Birthday(date.Year);if(start>date)start=Birthday(date.Year-1);
        return date>=start && date<start.AddDays(BirthdayTermDays);
    }
}
