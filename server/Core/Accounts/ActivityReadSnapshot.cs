using System.Text.Json;
using Microsoft.Data.Sqlite;
using static Brs.Local.Accounts.ActivitySchema;

namespace Brs.Local.Accounts;

// Request-local account snapshot. Bounded SQL reads, no per-mission SQL and no cross-request state cache.
internal sealed class ActivityReadSnapshot
{
    private sealed record ReceiptFact(long Time, int Draws, int ExchangeContentsId, int ExchangeQty, CommerceReward[] Grants);
    private sealed record Fact(long Time, int Type, int Qty);
    private sealed record BattleFact(long Time,int QuestId,int QuestTypeId,int Type,int PartNo,int EventId,
        Dictionary<int,int> Enemies,int Bosses,int[] Characters,int[] OwnTeamCharacters,int NewMissions,int ManualPlaces,int MasteryDelta,int EventPoint,int Support);
    internal long Epoch { get; private set; }
    internal int SqlReadCount { get; private set; }
    internal HashSet<(int Type, int Id, string Period)> Claims { get; } = [];
    private readonly Dictionary<int,(int Level,int Awakening,int Skill)> cards=[];
    private readonly List<GrowthReceiptFact> growth=[];
    private readonly List<ReceiptFact> receipts=[];
    private readonly List<Fact> facts=[];
    private readonly List<BattleFact> battles=[];
    private readonly List<(long Time,int Qty)> stamina=[];
    private readonly List<(long Time,int Power)> ownDeckReports=[];
    private readonly HashSet<int> cleared=[];
    // Exact nondeleted original CharaMaster.TeamId members for the five original106 missions.
    private static readonly IReadOnlyDictionary<int,int[]> OriginalTeamMembers=new Dictionary<int,int[]>{[1]=[1,2,3,4],[2]=[5,6,7,8],[3]=[9,10,11],[4]=[12,13,14],[5]=[15,16,17,18]};
    private long maxEquipLevel,acquiredEquipGroups,rank,botFollowCount;
    private static readonly JsonSerializerOptions ReceiptJson=new(){PropertyNameCaseInsensitive=true};
    internal static ActivityReadSnapshot Load(ActivityConfig config,SqliteConnection c,SqliteTransaction t,long account,string player,long now,MainlineConfig? mainline=null,EventQuestConfig? events=null,QuestModesConfig? modes=null)
    {
        var snapshot=new ActivityReadSnapshot();
        snapshot.botFollowCount=AccountStore.ReadLocalBotFollowCount(c,t,account);snapshot.SqlReadCount++;
        SqliteCommand Query(string sql) { snapshot.SqlReadCount++;return Command(c,t,sql,("$id",account),("$now",now)); }
        using(var q=Query("SELECT epoch_seconds,(SELECT COALESCE(MAX(lv),0) FROM owned_equips WHERE account_id=$id AND is_deleted=0),(SELECT COUNT(*) FROM acquired_equip_groups WHERE account_id=$id) FROM mission_epochs WHERE account_id=$id"))
        using(var r=q.ExecuteReader())
        {
            if(!r.Read() || (snapshot.Epoch=r.GetInt64(0))<=0) throw new InvalidDataException("Mission epoch must be anchored before querying.");
            snapshot.maxEquipLevel=r.GetInt64(1);snapshot.acquiredEquipGroups=r.GetInt64(2);
        }
        using(var p=JsonDocument.Parse(player))snapshot.rank=p.RootElement.GetProperty("rank").GetInt32();
        using(var q=Query("SELECT card_id,lv,awakening_count,main_skill_lv,kill_ability_lv,kill_zone_skill_lv FROM owned_cards WHERE account_id=$id"))
        using(var r=q.ExecuteReader())while(r.Read())snapshot.cards.Add(r.GetInt32(0),(r.GetInt32(1),r.GetInt32(2),Math.Max(r.GetInt32(3),Math.Max(r.GetInt32(4),r.GetInt32(5)))));
        using(var q=Query("SELECT mission_type,mission_id,period_key FROM mission_claims WHERE account_id=$id"))
        using(var r=q.ExecuteReader())while(r.Read())snapshot.Claims.Add((r.GetInt32(0),r.GetInt32(1),r.GetString(2)));
        using(var q=Query("SELECT e.route,e.config_hash,e.result_json,e.created_at,e.cost_json,q.kind,q.quest_id,q.payload_json,q.config_hash FROM economy_transactions e LEFT JOIN quest_runs q ON q.account_id=e.account_id AND q.id=json_extract(e.result_json,'$.audit.run_id') WHERE e.account_id=$id AND e.created_at<=$now AND (e.created_at>(SELECT epoch_seconds FROM mission_epochs WHERE account_id=$id) OR (e.created_at=(SELECT epoch_seconds FROM mission_epochs WHERE account_id=$id) AND NOT EXISTS(SELECT 1 FROM mission_epoch_excluded_receipts x WHERE x.account_id=e.account_id AND x.retry_hash=e.retry_hash))) ORDER BY e.created_at,hex(e.retry_hash)"))
        using(var r=q.ExecuteReader())
        {
            var products=config.Products.ToDictionary(p=>p.ProductId,StringComparer.Ordinal);
            while(r.Read())
            {
                var route=r.GetString(0);var hash=r.GetString(1);var time=r.GetInt64(3);
                if(route=="gacha/result" && hash==config.GachaConfigSha256)
                {
                    using var d=JsonDocument.Parse(r.GetString(2));var draws=d.RootElement.GetProperty("lot_contents").GetArrayLength();
                    if(draws<=0)throw new InvalidDataException("Empty frozen gacha result.");
                    snapshot.receipts.Add(new(time,draws,0,0,[]));
                }
                else if(route=="shop/purchase" && hash==config.CommerceConfigSha256)
                {
                    using var d=JsonDocument.Parse(r.GetString(2));var result=d.RootElement;
                    if(result.GetProperty("external_payment").GetBoolean())throw new InvalidDataException("Unexpected external payment receipt.");
                    if(!products.TryGetValue(result.GetProperty("product_id").GetString()!,out var product) || product.ContentsType!=result.GetProperty("contents_type").GetInt32())throw new InvalidDataException("Unknown frozen product.");
                    var qty=result.GetProperty("qty").GetInt32();if(qty is <1 or >50)throw new InvalidDataException("Invalid frozen quantity.");
                    // Frozen Commerce serializes product.Rewards (one unit), NOT plan*qty. Verify and multiply exactly once.
                    var rewards=JsonSerializer.Deserialize<CommerceReward[]>(result.GetProperty("rewards"),ReceiptJson)??throw new InvalidDataException("Missing typed rewards.");
                    if(!rewards.SequenceEqual(product.Rewards))throw new InvalidDataException("Frozen reward/catalog mismatch.");
                    snapshot.receipts.Add(new(time,0,product.ContentsType is 5 or 6 or 7 or 8?product.ContentsId:0,
                        product.ContentsType is 5 or 6 or 7 or 8?qty:0,rewards.Select(x=>x with {ItemQty=checked(x.ItemQty*qty)}).ToArray()));
                }
                else if(route==ActivityEngine.ReceiveRoute && hash==ActivityConfig.ExpectedFingerprint)
                {
                    using var d=JsonDocument.Parse(r.GetString(2));var result=d.RootElement;
                    if(result.GetProperty("source").GetString()!="mission")throw new InvalidDataException("Unknown mission receipt.");
                    var rewards=JsonSerializer.Deserialize<CommerceReward[]>(result.GetProperty("typed_rewards"),ReceiptJson)??throw new InvalidDataException("Missing mission rewards.");
                    if(rewards.Any(x=>x.ItemQty<=0))throw new InvalidDataException("Invalid mission grant.");
                    snapshot.receipts.Add(new(time,0,0,0,rewards));
                }
                else if(route is "quest/start" or "quest/result" && hash is MainlineConfig.ExpectedFingerprint or EventQuestConfig.ExpectedFingerprint or QuestModesConfig.ExpectedFingerprint or QuestTaCatalog.Fingerprint)
                {
                    if(r.IsDBNull(5)||r.GetString(8)!=hash)throw new InvalidDataException("Quest mission source has no matching frozen run.");
                    if(r.GetInt32(5)!=1)continue; // A pure story result is not a battle or AP-spend fact.
                    var questId=r.GetInt32(6);var isEvent=hash==EventQuestConfig.ExpectedFingerprint;
                    var isMode=hash==QuestModesConfig.ExpectedFingerprint;
                    var data=hash==QuestTaCatalog.Fingerprint?QuestTaCatalog.Data:isMode?modes?.Data:isEvent?events?.Data:mainline;var quest=data?.Find(questId)??throw new InvalidDataException("Unknown quest mission source.");
                    if(route=="quest/start")
                    {
                        using var cost=JsonDocument.Parse(r.GetString(4));var spent=cost.RootElement.GetProperty("stamina_spent").GetInt32();
                        if(spent!=quest.Stamina)throw new InvalidDataException("Frozen quest AP/catalog mismatch.");
                        snapshot.stamina.Add((time,spent));
                        using var startResult=JsonDocument.Parse(r.GetString(2));
                        if(startResult.RootElement.GetProperty("audit").TryGetProperty("own_deck_report",out var report)&&report.ValueKind!=JsonValueKind.Null)
                        {
                            using var frozen=JsonDocument.Parse(r.GetString(7));var startPayload=frozen.RootElement;
                            var raw=startPayload.GetProperty("client_deck_total_power").GetDouble();
                            if(report.GetProperty("source").GetString()!="client-own-deck-report"||report.GetProperty("support_user_id").GetInt32()!=0||startPayload.GetProperty("support_user_id").GetInt32()!=0||!double.IsFinite(raw)||raw<0||Math.Ceiling(raw)>int.MaxValue||report.GetProperty("power").GetInt32()!=Math.Ceiling(raw))throw new InvalidDataException("Invalid own-deck report provenance.");
                            snapshot.ownDeckReports.Add((time,report.GetProperty("power").GetInt32()));
                        }
                        continue;
                    }
                    using var d=JsonDocument.Parse(r.GetString(2));var result=d.RootElement;var audit=result.GetProperty("audit");
                    if(audit.GetProperty("original_config").GetString()!=hash)throw new InvalidDataException("Frozen quest audit/catalog mismatch.");
                    if(audit.GetProperty("outcome").GetString()!="clear")continue;
                    using var payload=JsonDocument.Parse(r.GetString(7));
                    if(isMode&&(payload.RootElement.GetProperty("quest_type").GetInt32()!=modes!.Chapter(quest).Type||payload.RootElement.GetProperty("mode_config").GetString()!=hash||audit.GetProperty("quest_type").GetInt32()!=payload.RootElement.GetProperty("quest_type").GetInt32()))throw new InvalidDataException("Frozen quest mode source differs.");
                    var characters=payload.RootElement.GetProperty("cards").EnumerateArray().Select(card=>card.GetProperty("chara_id").GetInt32()).ToArray();
                    // Local conservative106 policy: borrowed support does not count as an owned team member;
                    // its replaced owned slot6 is also excluded. Never inspect today's owned/deck state.
                    var ownSlotCount=payload.RootElement.GetProperty("support_user_id").GetInt32()==0?6:5;
                    var participating=Enumerable.Range(1,ownSlotCount).Select(i=>payload.RootElement.GetProperty("deck").GetProperty($"slot_{i}_card_id").GetInt32()).Where(id=>id>0).ToHashSet();
                    var ownTeamCharacters=payload.RootElement.GetProperty("cards").EnumerateArray().Where(card=>participating.Contains(card.GetProperty("card_id").GetInt32())).Select(card=>card.GetProperty("chara_id").GetInt32()).Distinct().ToArray();
                    var client=audit.GetProperty("client_result");var enemies=JsonSerializer.Deserialize<Dictionary<int,int>>(client.GetProperty("DefeatEnemy"))??[];
                    var bosses=JsonSerializer.Deserialize<Dictionary<int,int>>(client.GetProperty("DefeatBoss"))??[];
                    var chapter=data!.Chapters.Single(ch=>ch.QuestTypeId==quest.QuestTypeId);
                    var grants=JsonSerializer.Deserialize<CommerceReward[]>(audit.GetProperty("typed_grants"),ReceiptJson)??throw new InvalidDataException("Missing typed quest grant source.");
                    if(grants.Any(x=>x.ItemQty<=0))throw new InvalidDataException("Invalid typed quest grant.");
                    snapshot.receipts.Add(new(time,0,0,0,grants));
                    snapshot.battles.Add(new(time,questId,quest.QuestTypeId,hash==QuestTaCatalog.Fingerprint?4:isMode?modes!.Chapter(quest).Type:isEvent?2:1,chapter.PartNo,isEvent?events!.Chapter(quest).EventId:0,
                        enemies,checked((int)bosses.Values.Sum(v=>(long)v)),characters,ownTeamCharacters,result.GetProperty("action").GetProperty("new_clear_mission_nos").GetArrayLength(),
                        audit.GetProperty("manual_place_count").GetInt32(),audit.GetProperty("mastery_level_delta").GetInt32(),audit.GetProperty("event_point_delta").GetInt32(),payload.RootElement.GetProperty("support_user_id").GetInt32()));
                }
                else if(GrowthRequest.Routes.Contains(route,StringComparer.Ordinal) && hash==GrowthCatalog.ExpectedFingerprint)
                {
                    using var d=JsonDocument.Parse(r.GetString(2));var fact=GrowthReceiptFact.Read(time,route,d.RootElement);
                    snapshot.growth.Add(fact);
                    if(fact.Grants.Length>0)snapshot.receipts.Add(new(time,0,0,0,fact.Grants));
                }
                else if(route is "present/item/receive" or "present/equip/receive" or "present/equip/sell" && hash==PresentCatalog.ExpectedFingerprint)
                {
                    using var d=JsonDocument.Parse(r.GetString(2));var result=d.RootElement;
                    if(result.GetProperty("source").GetString()!="local-general-present-v1")throw new InvalidDataException("Unknown present credit source.");
                    var grants=JsonSerializer.Deserialize<CommerceReward[]>(result.GetProperty("audit").GetProperty("typed_grants"),ReceiptJson)??throw new InvalidDataException("Missing real present credits.");
                    if(grants.Any(x=>x.ItemQty<=0))throw new InvalidDataException("Invalid present typed credit.");snapshot.receipts.Add(new(time,0,0,0,grants));
                }
                else if(route==VipPresentReceiveRequest.Route && hash==VipPassConfig.ExpectedFingerprint)
                {
                    using var d=JsonDocument.Parse(r.GetString(2));var result=d.RootElement;
                    if(result.GetProperty("source").GetString()!="vip-present-receive")throw new InvalidDataException("Unknown legacy VIP credit source.");
                    var grants=result.GetProperty("rewards").EnumerateArray().Select(x=>new CommerceReward(x.GetProperty("item_type").GetInt32(),x.GetProperty("item_id").GetInt32(),x.GetProperty("item_qty").GetInt32())).ToArray();
                    if(grants.Any(x=>x.ItemType!=2||x.ItemQty<=0))throw new InvalidDataException("Invalid legacy VIP credit.");snapshot.receipts.Add(new(time,0,0,0,grants));
                }
                // Manual SET, old configs and unrelated routes never acquire inferred mission facts.
            }
        }
        using(var q=Query("SELECT f.occurred_at,f.fact_type,f.result_json FROM (SELECT r.started_at occurred_at,406 fact_type,r.result_json,e.config_hash,e.retry_hash,e.created_at FROM expedition_runs r JOIN economy_transactions e ON e.account_id=r.account_id AND e.retry_hash=r.dispatch_retry_hash WHERE r.account_id=$id AND r.config_hash='"+ExpeditionCatalog.ExpectedFingerprint+"' AND e.route IN('expedition/start','expedition/restart') UNION ALL SELECT r.finished_at,415,r.result_json,e.config_hash,e.retry_hash,e.created_at FROM expedition_runs r JOIN economy_transactions e ON e.account_id=r.account_id AND e.retry_hash=r.finish_retry_hash WHERE r.account_id=$id AND r.state=1 AND r.config_hash='"+ExpeditionCatalog.ExpectedFingerprint+"' AND e.route='expedition/result') f WHERE f.config_hash='"+ExpeditionCatalog.ExpectedFingerprint+"' AND f.occurred_at=f.created_at AND f.occurred_at<=$now AND (f.created_at>(SELECT epoch_seconds FROM mission_epochs WHERE account_id=$id) OR (f.created_at=(SELECT epoch_seconds FROM mission_epochs WHERE account_id=$id) AND NOT EXISTS(SELECT 1 FROM mission_epoch_excluded_receipts x WHERE x.account_id=$id AND x.retry_hash=f.retry_hash)))"))
        using(var r=q.ExecuteReader())while(r.Read())
        {
            var time=r.GetInt64(0);var type=r.GetInt32(1);snapshot.facts.Add(new(time,type,1));
            if(type==415)
            {
                using var d=JsonDocument.Parse(r.GetString(2));var grants=JsonSerializer.Deserialize<CommerceReward[]>(d.RootElement.GetProperty("typed_grants"),ReceiptJson)??throw new InvalidDataException("Missing expedition typed grants.");
                if(grants.Any(x=>x.ItemQty<=0))throw new InvalidDataException("Invalid expedition typed grant.");snapshot.receipts.Add(new(time,0,0,0,grants));
            }
        }
        using(var q=Query("SELECT occurred_at,fact_type,qty FROM mission_facts WHERE account_id=$id AND occurred_at>=(SELECT epoch_seconds FROM mission_epochs WHERE account_id=$id) AND occurred_at<=$now"))
        using(var r=q.ExecuteReader())while(r.Read())snapshot.facts.Add(new(r.GetInt64(0),r.GetInt32(1),r.GetInt32(2)));
        using(var q=Query("SELECT quest_id FROM user_quests WHERE account_id=$id UNION SELECT quest_id FROM event_quests WHERE account_id=$id AND clear_rate>0"))
        using(var r=q.ExecuteReader())while(r.Read())snapshot.cleared.Add(r.GetInt32(0));
        return snapshot;
    }
    internal long Score(ActivityConfig config,ActivityMission m,long now)
    {
        var filter=m.ContentValue.Split(',').Select(int.Parse).ToArray();
        var start=Math.Max(Epoch,ActivityDates.Start(m.MissionType,now));
        long score=m.ContentType switch
        {
            201=>filter.Single()==0?cards.Values.Select(c=>c.Level).DefaultIfEmpty().Max():cards.GetValueOrDefault(filter.Single()).Level,
            202=>filter.Single()==0?cards.Values.Select(c=>c.Awakening).DefaultIfEmpty().Max():cards.GetValueOrDefault(filter.Single()).Awakening,
            203=>filter.Single()==0?cards.Values.Select(c=>c.Skill).DefaultIfEmpty().Max():cards.GetValueOrDefault(filter.Single()).Skill,
            204=>growth.Where(g=>g.Time>=start&&g.Route=="card/awakening"&&(filter.Contains(0)||filter.Contains(g.CardId))).Sum(g=>(long)g.AwakeningDelta),
            303=>growth.Where(g=>g.Time>=start&&g.Route=="equip/grant_property"&&(filter.Contains(0)||filter.Contains(g.EquipId))).Sum(g=>(long)g.Operations),
            304=>growth.Where(g=>g.Time>=start&&g.Route=="equip/enhance"&&(filter.Contains(0)||filter.Contains(g.EquipId))).Sum(g=>(long)g.OverLimitDelta),
            306=>growth.Where(g=>g.Time>=start&&g.Route=="equip/enhance"&&(filter.Contains(0)||filter.Contains(g.EquipId))).Sum(g=>(long)g.Operations),
            207=>cards.Count,
            307=>maxEquipLevel,
            308=>acquiredEquipGroups,
            405=>filter.Length==1&&filter[0]==0?botFollowCount:0,
            409=>rank,
            411=>filter.Length==1&&filter[0]==0?ownDeckReports.Where(p=>p.Time>=start).Select(p=>(long)p.Power).DefaultIfEmpty().Max():0,
            113=>filter.Count(id=>cleared.Contains(id)),
            414 or 601=>facts.Where(f=>f.Type==m.ContentType && f.Time>=start).Sum(f=>(long)f.Qty),
            406 or 415=>filter.Length==1&&filter[0]==0?facts.Where(f=>f.Type==m.ContentType&&f.Time>=start).Sum(f=>(long)f.Qty):0,
            401=>receipts.Where(r=>r.Time>=start).Sum(r=>(long)r.Draws),
            408=>receipts.Where(r=>r.Time>=start && (filter.Single()==0 || filter.Single()==r.ExchangeContentsId)).Sum(r=>(long)r.ExchangeQty),
            407 or 412=>receipts.Where(r=>r.Time>=start).SelectMany(r=>r.Grants).Where(r=>
                m.ContentType==407 ? filter.Length==2 && r.ItemType==filter[0] && r.ItemId==filter[1]
                : filter.Length==2 && filter[0]==12 && r.ItemType==12 && config.ItemCategories.GetValueOrDefault(r.ItemId)==filter[1]).Sum(r=>(long)r.ItemQty),
            101=>battles.Count(b=>b.Time>=start&&(filter.Contains(0)||filter.Contains(b.QuestId))),
            103=>battles.Where(b=>b.Time>=start).Sum(b=>b.Enemies.Where(e=>filter.Contains(0)||filter.Contains(e.Key)).Sum(e=>(long)e.Value)),
            104=>battles.Count(b=>b.Time>=start&&b.Support!=0&&(filter.Contains(0)||filter.Contains(b.QuestId))),
            105=>battles.Where(b=>b.Time>=start).Sum(b=>(long)b.Characters.Count(c=>filter.Contains(c))),
            106=>filter.Length==1&&OriginalTeamMembers.TryGetValue(filter[0],out var team)?battles.Count(b=>b.Time>=start&&team.All(id=>b.OwnTeamCharacters.Contains(id))):0,
            107=>battles.Count(b=>b.Time>=start&&filter.Contains(b.Type)),
            108=>filter.Length==2?battles.Count(b=>b.Time>=start&&b.Type==filter[0]&&b.PartNo==filter[1]):0,
            109=>battles.Count(b=>b.Time>=start&&filter.Contains(b.QuestTypeId)),
            110=>battles.Where(b=>b.Time>=start).Sum(b=>(long)b.Bosses),
            112=>battles.Where(b=>b.Time>=start).Sum(b=>(long)b.NewMissions),
            114=>battles.Count(b=>b.Time>=start&&b.Characters.Any(c=>filter.Contains(c))),
            115=>battles.Where(b=>b.Time>=start).Sum(b=>(long)b.ManualPlaces),
            211=>battles.Where(b=>b.Time>=start).Sum(b=>(long)b.MasteryDelta),
            413=>stamina.Where(s=>s.Time>=start).Sum(s=>(long)s.Qty),
            501=>filter.Length==1&&filter[0]==0&&m.ResolvedScopeEventId>0?battles.Count(b=>b.Time>=start&&b.Type==2&&b.EventId==m.ResolvedScopeEventId):0,
            503=>battles.Where(b=>b.Time>=start&&b.EventId==m.ResolvedScopeEventId).Sum(b=>(long)b.EventPoint),
            _=>0
        };
        if(score<0)throw new InvalidDataException("Negative mission source.");return score;
    }
}
