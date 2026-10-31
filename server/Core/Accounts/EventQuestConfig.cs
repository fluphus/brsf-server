using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brs.Local.Accounts;

public sealed record EventQuestChapter(int QuestTypeId,int EventId,int Type,int ClearEffectType,int DailyExecMaxCount);
public sealed record EventPlace(int Id,int QuestId,int DropPlaceId,int ConditionType,string ConditionValue,int Rarity,int ItemType,int ItemId,int ItemQty);
public sealed record EventPointReward(int EventPointRewardId,int EventId,int Point,int ItemGroupId);
public sealed record EventReward(int Id,int EventId,int ItemGroupId,int ItemType,int ItemId,int ItemQty);
public sealed record EventEffect(int EventSpecialEffectId,int EventId,int PointRate,int AddDropEventItemId1,int AddDropEventItemQty1,int AddDropEventItemId2,int AddDropEventItemQty2,int AddDropEventItemId3,int AddDropEventItemQty3);
public sealed record EventCardEffect(int Id,int EventId,int CardId,int AwakeningCount,int EventSpecialEffectId);
public sealed record EventEquipEffect(int Id,int EventId,int EquipId,int OverLimitCount,int EventSpecialEffectId);
public sealed record EventOwnedItem(int EventId,int ItemId);
public sealed record OrnamentDrop(int Id,int EventId,int Type,int PartNo,string ChapterNo,int StageNo,int Rarity,int DropFrameId,int DropItemId);
public sealed record OrnamentItem(int Id,int EventId,int DropItemId,int ItemType,int ItemId,int ItemQty,int ItemQtyStaminaRate,int Weight);

public sealed record EventQuestConfig(MainlineConfig Data,ActivityEvent[] Events,EventQuestChapter[] EventChapters,EventPlace[] Places,
    EventPointReward[] PointRewards,EventReward[] EventRewards,EventEffect[] Effects,EventCardEffect[] CardEffects,
    EventEquipEffect[] EquipEffects,EventOwnedItem[] EventItems,int[] ConsumeIds,int[] TitleIds,
    OrnamentDrop[] OrnamentDrops,MainlineFrame[] OrnamentFrames,OrnamentItem[] OrnamentItems)
{
    public const string ExpectedFingerprint="696704c51f3cb890021f33f3aac5e08c0e76e09f55a6ddaf194607f1c78756aa";
    public static EventQuestConfig Load(string path)
    {
        var f=new FileInfo(path);
        if(!f.Exists || f.Length is <1 or >4*1024*1024 || (f.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Invalid event quest configuration.");
        var bytes=File.ReadAllBytes(path);
        if(Convert.ToHexStringLower(SHA256.HashData(bytes))!=ExpectedFingerprint)throw new InvalidDataException("Event quest configuration fingerprint differs.");
        var c=JsonSerializer.Deserialize<EventQuestConfig>(bytes,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new InvalidDataException("Missing event catalog.");
        c.Validate();return c;
    }
    public EventQuestChapter Chapter(MainlineQuest q)=>EventChapters.Single(ch=>ch.QuestTypeId==q.QuestTypeId);
    public int Owner(int item)=>EventItems.Single(x=>x.ItemId==item).EventId;
    public bool IsOpen(MainlineQuest quest,long now)
    {
        var e=Events.Single(x=>x.EventId==Chapter(quest).EventId);
        return Data.IsOpen(quest,now)&&ActivityDates.IsOpen(e.OpenDate,e.CloseDate,now);
    }
    private void Validate()
    {
        void Need(bool b){if(!b)throw new InvalidDataException("Inconsistent original event catalog.");}
        Need(Data.PolicyVersion=="local-event-quest-v1"&&Data.ContentPolicy=="local-content-v2-all-open"&&Data.ResourceRevision==144&&Data.MasterRevision==138&&Data.SourceMasterSha256=="8082ce3e3746a8e38d58c7e640892b80caef374f767a1d39f84db94d7108945b");
        Need(Data.Chapters.Length==40&&EventChapters.Length==40&&Data.Quests.Length==358&&Data.Missions.Length==900&&Data.Drops.Length==1225&&Data.Frames.Length==1645&&Data.DropItems.Length==3261&&Places.Length==170&&PointRewards.Length==1200);
        Need(Data.Quests.Select(q=>q.QuestId).Distinct().Count()==358&&EventChapters.Select(x=>x.QuestTypeId).Distinct().Count()==40);
        foreach(var ch in EventChapters)Need(Events.Any(e=>e.EventId==ch.EventId&&e.EventType is 1 or 2)&&ch.Type is >=1 and <=3&&ch.DailyExecMaxCount>=0);
        foreach(var q in Data.Quests)Need(EventChapters.Any(x=>x.QuestTypeId==q.QuestTypeId)&&q.Stamina is >=0 and <=999&&q.ClearEventPoint>=0&&q.ClearGameCoin>=0&&q.ClearUserExp>=0&&q.ClearCardMasteryExp>=0&&(q.BattleId!=0||q.BeforeStoryId>0&&q.Stamina==0));
        foreach(var m in Data.Missions)Need(Data.Find(m.QuestId)!=null&&m.MissionType is 1 or 2 or 3 or 7 or 8&&ValidReward(m.ItemType,m.ItemId,m.ItemQty));
        foreach(var d in Data.Drops)
        {
            Need(Data.Find(d.QuestId)!=null);
            var frames=Data.Frames.Where(f=>f.DropFrameId==d.DropFrameId).ToArray();var items=Data.DropItems.Where(i=>i.DropItemId==d.DropItemId).ToArray();
            Need(frames.Length>0&&items.Length>0&&frames.All(f=>f.FrameCount>=0&&f.Weight>=0)&&frames.Sum(f=>(long)f.Weight) is >0 and <=int.MaxValue&&items.All(i=>i.Weight>=0&&ValidReward(i.ItemType,i.ItemId,i.ItemQty))&&items.Sum(i=>(long)i.Weight) is >0 and <=int.MaxValue);
        }
        Need(Places.Select(p=>(p.QuestId,p.DropPlaceId)).Distinct().Count()==Places.Length);
        foreach(var p in Places)Need(Data.Find(p.QuestId)!=null&&p.ConditionType==1&&p.ConditionValue==""&&ValidReward(p.ItemType,p.ItemId,p.ItemQty));
        foreach(var p in PointRewards)Need(Events.Any(e=>e.EventId==p.EventId)&&p.Point>0&&EventRewards.Any(r=>r.EventId==p.EventId&&r.ItemGroupId==p.ItemGroupId));
        foreach(var r in EventRewards)Need(ValidReward(r.ItemType,r.ItemId,r.ItemQty));
        Need(Effects.All(e=>e.PointRate>=100));
        foreach(var x in CardEffects)Need(Effects.Any(e=>e.EventSpecialEffectId==x.EventSpecialEffectId&&e.EventId==x.EventId)&&x.AwakeningCount>=0);
        foreach(var x in EquipEffects)Need(Effects.Any(e=>e.EventSpecialEffectId==x.EventSpecialEffectId&&e.EventId==x.EventId)&&x.OverLimitCount>=0);
    }
    private bool ValidReward(int type,int id,int qty)=>qty>0&&(type is 2 or 3 or 4? id==0:type switch
    {
        5=>id>0,6=>ConsumeIds.Contains(id),7=>Data.Equips.Any(e=>e.EquipId==id),10=>TitleIds.Contains(id),11=>id>0,
        12=>Data.EnhanceIds.Contains(id),14=>EventItems.Any(e=>e.ItemId==id),_=>false
    });
}
