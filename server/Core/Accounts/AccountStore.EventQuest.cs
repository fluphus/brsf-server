using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private void ObserveActivityDailyClear(SqliteConnection c,SqliteTransaction t,long account,string player,long now)
    {
        if(activityConfig is null)return;
        new ActivityEngine(activityConfig,mainlineConfig,eventQuestConfig,questModesConfig).ObserveDailyClear(c,t,account,player,now);
    }
    private bool IsEventQuest(int id)=>eventQuestConfig?.Data.Find(id) is not null;
    private MainlineConfig QuestData(int id)=>QuestTaCatalog.Contains(id)?QuestTaCatalog.Data:IsModeQuest(id)?questModesConfig!.Data:IsEventQuest(id)?eventQuestConfig!.Data:mainlineConfig!;
    private string QuestHash(int id)=>QuestTaCatalog.Contains(id)?QuestTaCatalog.Fingerprint:IsModeQuest(id)?QuestModesConfig.ExpectedFingerprint:IsEventQuest(id)?EventQuestConfig.ExpectedFingerprint:MainlineConfig.ExpectedFingerprint;
    private bool QuestCleared(SqliteConnection c,SqliteTransaction t,long account,int quest)
    {
        if(!IsEventQuest(quest))return MainlineCleared(c,t,account,quest);
        using var q=Command(c,t,"SELECT EXISTS(SELECT 1 FROM event_quests WHERE account_id=$a AND quest_id=$q AND clear_rate>0)");
        q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$q",quest);return (long)q.ExecuteScalar()! == 1;
    }
    private void TouchEventQuest(SqliteConnection c,SqliteTransaction t,long account,MainlineQuest quest,int rate,long now)
    {
        var owner=eventQuestConfig!.Chapter(quest).EventId;
        using var q=Command(c,t,"INSERT INTO event_quests(account_id,event_id,quest_id,clear_rate,last_update_at) VALUES($a,$e,$q,$r,$n) ON CONFLICT(account_id,event_id,quest_id) DO UPDATE SET clear_rate=MAX(clear_rate,excluded.clear_rate),last_update_at=excluded.last_update_at");
        q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$e",owner);q.Parameters.AddWithValue("$q",quest.QuestId);q.Parameters.AddWithValue("$r",rate);q.Parameters.AddWithValue("$n",now);q.ExecuteNonQuery();
    }
    private void CheckEventStartLimit(SqliteConnection c,SqliteTransaction t,long account,MainlineQuest quest,long now)
    {
        if(!IsEventQuest(quest.QuestId))return;
        var chapter=eventQuestConfig!.Chapter(quest);
        if(chapter.Type!=3 || chapter.DailyExecMaxCount==0)return;
        var ids=eventQuestConfig.Data.Quests.Where(q=>eventQuestConfig.Chapter(q).EventId==chapter.EventId&&eventQuestConfig.Chapter(q).Type==3).Select(q=>q.QuestId).ToArray();
        using var count=Command(c,t,"SELECT COUNT(*) FROM quest_runs WHERE account_id=$a AND kind=1 AND config_hash=$h AND created_at>=$d AND created_at<$end AND quest_id IN ("+string.Join(',',ids)+")");
        count.Parameters.AddWithValue("$a",account);count.Parameters.AddWithValue("$h",EventQuestConfig.ExpectedFingerprint);var day=ActivityDates.Start(2,now);
        count.Parameters.AddWithValue("$d",day);count.Parameters.AddWithValue("$end",day+86400);
        if((long)count.ExecuteScalar()!>=chapter.DailyExecMaxCount)throw new MainlineRejectedException();
    }
    private bool OwnsExtraShopGate(SqliteConnection c,SqliteTransaction t,long account,int product)
    {
        if(product==0)return true;
        using var q=Command(c,t,"SELECT EXISTS(SELECT 1 FROM owned_extra_shop_counts WHERE account_id=$a AND product_id=$p AND count>0)");
        q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$p",product);return (long)q.ExecuteScalar()! == 1;
    }
    private bool OwnsQuestTitle(SqliteConnection c,SqliteTransaction t,long account,int title)
    {
        if(activityConfig?.TitleIds.Contains(title)!=true&&eventQuestConfig?.TitleIds.Contains(title)!=true&&questModesConfig?.TitleIds.Contains(title)!=true)return false;
        using var q=Command(c,t,"SELECT EXISTS(SELECT 1 FROM owned_titles WHERE account_id=$a AND title_id=$title)");
        q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$title",title);return (long)q.ExecuteScalar()! == 1;
    }
    private int QuestEventItemOwner(int item)=>eventQuestConfig?.EventItems.SingleOrDefault(e=>e.ItemId==item)?.EventId??commerceConfig!.Owner(14,item);
    private bool QuestRewardReference(int type,int id)=>commerceConfig?.ItemReferences.Any(r=>r.ItemType==type&&r.ItemId==id)==true || type switch
    {
        6=>eventQuestConfig?.ConsumeIds.Contains(id)==true||questModesConfig?.ConsumeIds.Contains(id)==true,11=>allGachaConfig?.Cards.Any(c=>c.CardId==id)==true,
        12=>eventQuestConfig?.Data.EnhanceIds.Contains(id)==true||questModesConfig?.Data.EnhanceIds.Contains(id)==true,14=>eventQuestConfig?.EventItems.Any(e=>e.ItemId==id)==true,_=>false
    };
    private void ProjectEventState(SqliteConnection c,SqliteTransaction t,long account,long now,Dictionary<string,object?> action)
    {
        if(eventQuestConfig is null)return;
        var day=ActivityDates.Start(2,now);var executions=new Dictionary<int,int>();
        using(var q=Command(c,t,"SELECT quest_id,COUNT(*) FROM quest_runs WHERE account_id=$a AND config_hash=$h AND kind=1 AND created_at>=$d AND created_at<$end GROUP BY quest_id"))
        {
            q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$h",EventQuestConfig.ExpectedFingerprint);q.Parameters.AddWithValue("$d",day);q.Parameters.AddWithValue("$end",day+86400);
            using var r=q.ExecuteReader();while(r.Read())
            { var quest=eventQuestConfig.Data.Find(r.GetInt32(0)); if(quest is not null && eventQuestConfig.Chapter(quest) is {Type:3,DailyExecMaxCount:>0}) executions.Add(r.GetInt32(0),checked((int)r.GetInt64(1))); }
        }
        var missions=new Dictionary<int,List<int>>();
        using(var q=Command(c,t,"SELECT quest_id,mission_no FROM event_quest_mission_claims WHERE account_id=$a ORDER BY quest_id,mission_no"))
        {q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();while(r.Read()){if(!missions.TryGetValue(r.GetInt32(0),out var l))missions[r.GetInt32(0)]=l=[];l.Add(r.GetInt32(1));}}
        var places=new Dictionary<int,List<(int Place,long Day)>>();
        using(var q=Command(c,t,"SELECT quest_id,drop_place_id,business_day FROM event_drop_claims WHERE account_id=$a ORDER BY quest_id,drop_place_id"))
        {q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();while(r.Read()){if(!places.TryGetValue(r.GetInt32(0),out var l))places[r.GetInt32(0)]=l=[];l.Add((r.GetInt32(1),r.GetInt64(2)));}}
        var rows=new List<object>();
        using(var q=Command(c,t,"SELECT event_id,quest_id,clear_rate,last_update_at FROM event_quests WHERE account_id=$a ORDER BY event_id,quest_id"))
        {
            q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();while(r.Read())
            {
                var id=r.GetInt32(1);var drops=places.GetValueOrDefault(id)??[];
                // Original IQuest consumer splits every non-null value and converts each token to Int32.
                rows.Add(new {event_id=r.GetInt32(0),quest_id=id,clear_rate=r.GetInt32(2),clear_mission_nos=missions.TryGetValue(id,out var cleared)&&cleared.Count>0?string.Join(',',cleared):null,today_exec_count=executions.GetValueOrDefault(id),
                    today_drop_item_place_ids=string.Join(',',drops.Where(p=>p.Day==day).Select(p=>p.Place)),total_drop_item_place_ids=string.Join(',',drops.Select(p=>p.Place)),
                    last_update_date=DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture)});
            }
        }
        action["u_event_quest"]=rows.ToArray();
        var points=new List<object>();using(var q=Command(c,t,"SELECT event_id,point FROM event_points WHERE account_id=$a ORDER BY event_id"))
        {q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();while(r.Read())points.Add(new {event_id=r.GetInt32(0),point=r.GetInt32(1)});}
        action["u_event_point"]=points.ToArray();
    }
    private EventEffect[] FrozenEventEffects(int eventId,JsonElement[] cards,JsonElement[] equips)
    {
        var cfg=eventQuestConfig!;var selected=new List<int>();
        foreach(var card in cards)
        {
            var id=card.GetProperty("card_id").GetInt32();var awakening=card.GetProperty("awakening_count").GetInt32();
            var e=cfg.CardEffects.Where(e=>e.EventId==eventId&&e.CardId==id&&e.AwakeningCount<=awakening).OrderByDescending(e=>e.AwakeningCount).FirstOrDefault();if(e!=null)selected.Add(e.EventSpecialEffectId);
        }
        foreach(var equip in equips)
        {
            var id=equip.GetProperty("equip_id").GetInt32();var over=equip.GetProperty("over_limit_count").GetInt32();
            var e=cfg.EquipEffects.Where(e=>e.EventId==eventId&&e.EquipId==id&&e.OverLimitCount<=over).OrderByDescending(e=>e.OverLimitCount).FirstOrDefault();if(e!=null)selected.Add(e.EventSpecialEffectId);
        }
        return selected.Select(id=>cfg.Effects.Single(e=>e.EventSpecialEffectId==id)).ToArray();
    }
    private List<CommerceReward> AddEventPoints(SqliteConnection c,SqliteTransaction t,long account,MainlineQuest quest,MainlineRun run,QuestResultRequest request,JsonObject wallet,long now,out int earned, EquipmentGrantContext grantContext)
    {
        earned=0;var grants=new List<CommerceReward>();
        if(!IsEventQuest(quest.QuestId)||request.ClearRate==0||quest.BattleId==0)return grants;
        var cfg=eventQuestConfig!;var owner=cfg.Chapter(quest).EventId;
        using var payload=JsonDocument.Parse(run.Payload);
        var pointUp=payload.RootElement.GetProperty("event_point_up").GetInt32();
        var rate=cfg.Data.ClearRates.Single(r=>r.ClearRate==request.ClearRate).EventPointRate;
        earned=checked((int)(checked((long)quest.ClearEventPoint*rate*(100L+pointUp))/10000));
        if(earned==0)return grants;
        using var read=Command(c,t,"SELECT point FROM event_points WHERE account_id=$a AND event_id=$e");read.Parameters.AddWithValue("$a",account);read.Parameters.AddWithValue("$e",owner);
        var before=read.ExecuteScalar() is long n?n:0;var after=checked(before+earned);if(after>int.MaxValue)throw new GachaCapacityException();
        using(var put=Command(c,t,"INSERT INTO event_points(account_id,event_id,point) VALUES($a,$e,$p) ON CONFLICT(account_id,event_id) DO UPDATE SET point=excluded.point"))
        {put.Parameters.AddWithValue("$a",account);put.Parameters.AddWithValue("$e",owner);put.Parameters.AddWithValue("$p",after);put.ExecuteNonQuery();}
        foreach(var threshold in cfg.PointRewards.Where(r=>r.EventId==owner&&r.Point>before&&r.Point<=after).OrderBy(r=>r.Point))
        {
            using var claimed=Command(c,t,"SELECT EXISTS(SELECT 1 FROM event_point_reward_claims WHERE account_id=$a AND event_id=$e AND reward_id=$r)");claimed.Parameters.AddWithValue("$a",account);claimed.Parameters.AddWithValue("$e",owner);claimed.Parameters.AddWithValue("$r",threshold.EventPointRewardId);
            if((long)claimed.ExecuteScalar()! !=0)throw new InvalidDataException("EP threshold already granted below frozen points.");
            var rewards=cfg.EventRewards.Where(r=>r.EventId==owner&&r.ItemGroupId==threshold.ItemGroupId).Select(r=>new CommerceReward(r.ItemType,r.ItemId,r.ItemQty)).ToArray();
            ApplyQuestTypedRewards(c,t,account,wallet,rewards,now, grantContext);grants.AddRange(rewards);
            using var claim=Command(c,t,"INSERT INTO event_point_reward_claims(account_id,event_id,reward_id,reward_json,claimed_at) VALUES($a,$e,$r,$json,$n)");claim.Parameters.AddWithValue("$a",account);claim.Parameters.AddWithValue("$e",owner);claim.Parameters.AddWithValue("$r",threshold.EventPointRewardId);claim.Parameters.AddWithValue("$json",JsonSerializer.Serialize(rewards));claim.Parameters.AddWithValue("$n",now);claim.ExecuteNonQuery();
        }
        return grants;
    }
    private void ApplyQuestTypedRewards(SqliteConnection c,SqliteTransaction t,long account,JsonObject wallet,CommerceReward[] rewards,long now, EquipmentGrantContext grantContext)
    {
        var result=GrantActivityRewards(activityConfig!,c,t,account,wallet.ToJsonString(),now,rewards, grantContext);
        var updated=JsonNode.Parse(result.Player)!.AsObject();wallet.Clear();foreach(var p in updated)wallet[p.Key]=p.Value?.DeepClone();
    }
    private sealed record EventManualDrop(int Place,MainlineReward Reward);
    private List<EventManualDrop> PrepareEventManualDrops(SqliteConnection c,SqliteTransaction t,long account,MainlineQuest quest,QuestResultRequest request)
    {
        var result=new List<EventManualDrop>();
        if(!IsEventQuest(quest.QuestId))return result;
        foreach(var place in request.GetDropPlaceIds)
        {
            var definition=eventQuestConfig!.Places.SingleOrDefault(p=>p.QuestId==quest.QuestId&&p.DropPlaceId==place)??throw new MainlineRejectedException();
            using var old=Command(c,t,"SELECT EXISTS(SELECT 1 FROM event_drop_claims WHERE account_id=$a AND quest_id=$q AND drop_place_id=$p)");old.Parameters.AddWithValue("$a",account);old.Parameters.AddWithValue("$q",quest.QuestId);old.Parameters.AddWithValue("$p",place);
            if((long)old.ExecuteScalar()! !=0)throw new MainlineRejectedException();
            result.Add(new(place,new(definition.ItemType,definition.ItemId,definition.ItemQty,definition.Rarity)));
        }
        return result;
    }
    private void SaveEventManualDrops(SqliteConnection c,SqliteTransaction t,long account,MainlineQuest quest,MainlineRun run,List<EventManualDrop> drops,long now)
    {
        foreach(var p in drops)
        {
            using var q=Command(c,t,"INSERT INTO event_drop_claims(account_id,event_id,quest_id,run_id,drop_place_id,business_day,reward_json,claimed_at) VALUES($a,$e,$q,$r,$p,$d,$reward,$n)");
            q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$e",eventQuestConfig!.Chapter(quest).EventId);q.Parameters.AddWithValue("$q",quest.QuestId);q.Parameters.AddWithValue("$r",run.Id);q.Parameters.AddWithValue("$p",p.Place);q.Parameters.AddWithValue("$d",ActivityDates.Start(2,now));q.Parameters.AddWithValue("$reward",JsonSerializer.Serialize(p.Reward));q.Parameters.AddWithValue("$n",now);q.ExecuteNonQuery();
        }
    }
    private void SaveQuestMission(SqliteConnection c,SqliteTransaction t,long account,MainlineQuest quest,int number,MainlineReward reward,long now)
    {
        var isEvent=IsEventQuest(quest.QuestId);
        using var q=Command(c,t,isEvent?"INSERT INTO event_quest_mission_claims(account_id,event_id,quest_id,mission_no,reward_json,claimed_at) VALUES($a,$e,$q,$no,$r,$n)":"INSERT INTO quest_mission_claims(account_id,quest_id,mission_no,reward_json,claimed_at) VALUES($a,$q,$no,$r,$n)");
        q.Parameters.AddWithValue("$a",account);if(isEvent)q.Parameters.AddWithValue("$e",eventQuestConfig!.Chapter(quest).EventId);q.Parameters.AddWithValue("$q",quest.QuestId);q.Parameters.AddWithValue("$no",number);q.Parameters.AddWithValue("$r",JsonSerializer.Serialize(reward));q.Parameters.AddWithValue("$n",now);q.ExecuteNonQuery();
    }
    private static Dictionary<int,int> ReadQuestMastery(SqliteConnection c,SqliteTransaction t,long account)
    {
        using var q=Command(c,t,"SELECT card_id,mastery_lv FROM owned_cards WHERE account_id=$a");q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();var result=new Dictionary<int,int>();while(r.Read())result.Add(r.GetInt32(0),r.GetInt32(1));return result;
    }
    private void ApplyEventFlatDrops(MainlineRun run,List<MainlineReward> drops)
    {
        if(run.ConfigHash!=EventQuestConfig.ExpectedFingerprint)return;
        using var payload=JsonDocument.Parse(run.Payload);
        var effects=JsonSerializer.Deserialize<EventEffect[]>(payload.RootElement.GetProperty("event_effects"),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??[];
        var flat=new Dictionary<int,int>();
        foreach(var e in effects)
            foreach(var (id,qty) in new[]{(e.AddDropEventItemId1,e.AddDropEventItemQty1),(e.AddDropEventItemId2,e.AddDropEventItemQty2),(e.AddDropEventItemId3,e.AddDropEventItemQty3)})
                if(id>0&&qty>0)flat[id]=checked(flat.GetValueOrDefault(id)+qty);
        // Explicit local policy: one flat grant per actually dropped matching item, never per random frame.
        foreach(var (item,qty) in flat)
        {
            var index=drops.FindIndex(d=>d.Type==14&&d.Item==item);
            if(index>=0)drops[index]=drops[index] with {Qty=checked(drops[index].Qty+qty)};
        }
    }
    private void AddOrnamentDrops(MainlineQuest quest,List<MainlineReward> rewards,int frozenStaminaSpent,long now)
    {
        if(eventQuestConfig is null)return;
        var cfg=eventQuestConfig;var data=QuestData(quest.QuestId);var chapter=data.Chapters.Single(c=>c.QuestTypeId==quest.QuestTypeId);var type=RealQuestType(quest.QuestId);
        foreach(var d in cfg.OrnamentDrops.Where(d=>d.Type==type&&(d.PartNo==0||d.PartNo==chapter.PartNo)&&(d.ChapterNo.Length==0||d.ChapterNo.Split(',').Contains(chapter.ChapterNo.ToString(CultureInfo.InvariantCulture)))&&(d.StageNo==0||d.StageNo==quest.StageNo)))
        {
            var e=cfg.Events.Single(e=>e.EventId==d.EventId);
            if(!ActivityDates.IsOpen(e.OpenDate,e.CloseDate,now))continue;
            var frames=cfg.OrnamentFrames.Where(f=>f.DropFrameId==d.DropFrameId).ToArray();var items=cfg.OrnamentItems.Where(i=>i.DropItemId==d.DropItemId).ToArray();
            var count=GachaWeights.Pick(frames,f=>f.Weight,nextGachaBelow??System.Security.Cryptography.RandomNumberGenerator.GetInt32).FrameCount;
            for(var n=0;n<count;n++)
            {
                var item=GachaWeights.Pick(items,i=>i.Weight,nextGachaBelow??System.Security.Cryptography.RandomNumberGenerator.GetInt32);
                var qty=checked(item.ItemQty+(int)((long)frozenStaminaSpent*item.ItemQtyStaminaRate/100));
                if(qty>0)rewards.Add(new(item.ItemType,item.ItemId,qty,d.Rarity));
            }
        }
    }
}
