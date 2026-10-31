using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brs.Local.Accounts;

public sealed record QuestModeChapter(int QuestTypeId,int Type,int PartNo,int SpecialMode);
public sealed record ExpertSchedule(int Id,int QuestTypeId,string OpenDate,string CloseDate,string ViewCloseDate,int CycleDay,int GetCycleQtyLimit);
public sealed record ExpertSelectReward(int SelectRewardId,int QuestTypeId,int QuestId,int ItemType,int ItemId,int ItemQty,int Weight);
public sealed record DailySchedule(int Id,int QuestTypeId,string DayOfWeek,int ExecMaxCount);

public sealed record QuestModesConfig(MainlineConfig Data,QuestModeChapter[] ModeChapters,ExpertSchedule[] ExpertSchedules,
    ExpertSelectReward[] SelectRewards,DailySchedule[] DailySchedules,int[] RankingQuestIds,int[] ConsumeIds,int[] TitleIds)
{
    public const string ExpectedFingerprint="37b5f1697d0650759aa4a3293355c9e1971947c15d9dfa2743fca31838f1686a";
    public static QuestModesConfig Load(string path)
    {
        var f=new FileInfo(path);
        if(!f.Exists||f.Length is <1 or >2*1024*1024||(f.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Invalid quest mode configuration path.");
        var bytes=File.ReadAllBytes(path);
        if(Convert.ToHexStringLower(SHA256.HashData(bytes))!=ExpectedFingerprint)throw new InvalidDataException("Quest mode fingerprint differs.");
        var cfg=JsonSerializer.Deserialize<QuestModesConfig>(bytes,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new InvalidDataException("Missing quest modes.");
        cfg.Validate();return cfg;
    }
    public QuestModeChapter Chapter(MainlineQuest q)=>ModeChapters.Single(c=>c.QuestTypeId==q.QuestTypeId);
    public bool IsOpen(MainlineQuest q,long now)
    {
        if(!Data.IsOpen(q,now))return false;
        var ch=Chapter(q);
        if(ch.Type==3)
        {
            var e=ExpertSchedules.Single(e=>e.QuestTypeId==q.QuestTypeId);
            return ActivityDates.IsOpen(e.OpenDate,e.CloseDate,now);
        }
        if(ch.Type==4)
        {
            // Original application day and weekday both roll over at05:00 JST.
            var day=DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(9)).AddHours(-5).DayOfWeek;
            var value=day==System.DayOfWeek.Sunday?7:(int)day;
            return DailySchedules.Single(d=>d.QuestTypeId==q.QuestTypeId).DayOfWeek.Split(',').Select(int.Parse).Contains(value);
        }
        return true;
    }
    public static long JstTimestamp(string date)=>new DateTimeOffset(AllGachaPool.ParseDate(date),TimeSpan.FromHours(9)).ToUnixTimeSeconds();
    public (long Start,long End) Cycle(int chapter,long now)
    {
        var e=ExpertSchedules.Single(e=>e.QuestTypeId==chapter);var start=JstTimestamp(e.OpenDate);
        var end=JstTimestamp(e.CloseDate);if(e.CycleDay<=0)return(start,end);
        var period=checked(e.CycleDay*86400L);var elapsed=now-start;
        // Calls before opening fail the gate; floor retains a well-defined projection.
        var index=(long)Math.Floor((double)elapsed/period);start=checked(start+index*period);
        return(start,checked(start+period));
    }
    private bool ValidReward(int type,int item,int qty)=>qty>0&&(type switch
    {
        2 or 4=>item==0,6=>ConsumeIds.Contains(item),7=>Data.Equips.Any(e=>e.EquipId==item),
        10=>TitleIds.Contains(item),12=>Data.EnhanceIds.Contains(item),_=>false
    });
    private void Validate()
    {
        void Need(bool b){if(!b)throw new InvalidDataException("Inconsistent original quest mode closure.");}
        Need(Data.PolicyVersion=="local-quest-modes-v1"&&Data.ContentPolicy=="local-content-v2-all-open"&&Data.MasterRevision==138&&Data.ResourceRevision==144&&Data.SourceMasterSha256=="8082ce3e3746a8e38d58c7e640892b80caef374f767a1d39f84db94d7108945b");
        Need(Data.Quests.Length==103&&Data.Chapters.Length==31&&ModeChapters.Length==31&&Data.Missions.Length==279&&Data.Drops.Length==259&&Data.Frames.Length==588&&Data.DropItems.Length==786&&SelectRewards.Length==547&&ExpertSchedules.Length==7&&DailySchedules.Length==19&&RankingQuestIds.Length==6);
        Need(Data.Quests.Select(q=>q.QuestId).Distinct().Count()==103&&ModeChapters.Select(c=>c.QuestTypeId).Distinct().Count()==31&&SelectRewards.Select(s=>s.SelectRewardId).Distinct().Count()==547);
        Need(ModeChapters.Count(c=>c.Type==3)==7&&ModeChapters.Count(c=>c.Type==4)==19&&ModeChapters.Count(c=>c.Type==5)==5&&ModeChapters.All(c=>c.SpecialMode==0));
        foreach(var q in Data.Quests)Need(ModeChapters.Any(c=>c.QuestTypeId==q.QuestTypeId)&&q.Prerequisites.All(p=>Data.Find(p)!=null||p is 10010007 or 10010110 or 10010212 or 10010312 or 10010413)&&q.ClearEventPoint==0&&q.ConditionExtraShopProductId==0&&q.Stamina is >=0 and <=999&&(q.BattleId>0||q.Stamina==0&&q.BeforeStoryId>0));
        foreach(var m in Data.Missions)Need(Data.Find(m.QuestId)!=null&&m.MissionType is 1 or 2 or 3 or 7 or 8&&ValidReward(m.ItemType,m.ItemId,m.ItemQty));
        foreach(var d in Data.Drops)
        {
            var f=Data.Frames.Where(f=>f.DropFrameId==d.DropFrameId).ToArray();var i=Data.DropItems.Where(i=>i.DropItemId==d.DropItemId).ToArray();
            Need(Data.Find(d.QuestId)!=null&&f.Length>0&&i.Length>0&&f.All(x=>x.FrameCount>=0&&x.Weight>=0)&&f.Sum(x=>(long)x.Weight) is >0 and <=int.MaxValue&&i.All(x=>x.Weight>=0&&ValidReward(x.ItemType,x.ItemId,x.ItemQty))&&i.Sum(x=>(long)x.Weight) is >0 and <=int.MaxValue);
        }
        foreach(var s in SelectRewards)Need(Data.Find(s.QuestId) is {} q&&q.QuestTypeId==s.QuestTypeId&&Chapter(q).Type==3&&s.Weight>0&&ValidReward(s.ItemType,s.ItemId,s.ItemQty));
        // Approved local boundary: cyclecap applies only to random battle drops.
        // The29 original one-time token missions keep their full separate rewards.
        Need(!SelectRewards.Any(s=>s.ItemType==6&&s.ItemId==50002));
        Need(ExpertSchedules.All(s=>s.CycleDay>0&&s.GetCycleQtyLimit>0&&JstTimestamp(s.OpenDate)<JstTimestamp(s.CloseDate))&&DailySchedules.All(d=>d.ExecMaxCount>=0&&d.DayOfWeek.Split(',').Select(int.Parse).All(n=>n is >=1 and <=7)));
        Need(RankingQuestIds.SequenceEqual(new[]{30020107,30020205,30020306,30020406,30020506,30020604}));
    }
}
