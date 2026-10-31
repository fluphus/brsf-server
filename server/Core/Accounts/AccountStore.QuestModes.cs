using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private readonly QuestModesConfig? questModesConfig;
    private static readonly Dictionary<string,string> QuestModesSchema=new(StringComparer.Ordinal)
    {
        ["quest_daily_counts"]="CREATE TABLE quest_daily_counts (id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(id BETWEEN 1 AND 2147483647), account_id INTEGER NOT NULL REFERENCES accounts(account_id), quest_type_id INTEGER NOT NULL CHECK(quest_type_id BETWEEN 1 AND 2147483647), business_day INTEGER NOT NULL CHECK(business_day>0), today_exec_count INTEGER NOT NULL CHECK(today_exec_count BETWEEN 0 AND 2147483647), last_exec_at INTEGER NOT NULL CHECK(last_exec_at>0), UNIQUE(account_id,quest_type_id)) STRICT",
        ["quest_expert_cycles"]="CREATE TABLE quest_expert_cycles (id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(id BETWEEN 1 AND 2147483647), account_id INTEGER NOT NULL REFERENCES accounts(account_id), quest_type_id INTEGER NOT NULL CHECK(quest_type_id BETWEEN 1 AND 2147483647), cycle_start_at INTEGER NOT NULL CHECK(cycle_start_at>0), total_cycle_qty INTEGER NOT NULL CHECK(total_cycle_qty BETWEEN 0 AND 2147483647), last_exec_at INTEGER NOT NULL CHECK(last_exec_at>0), UNIQUE(account_id,quest_type_id)) STRICT"
    };
    private bool IsModeQuest(int id)=>questModesConfig?.Data.Find(id)!=null;
    private int RealQuestType(int id)=>QuestTaCatalog.Contains(id)?4:IsModeQuest(id)?questModesConfig!.Chapter(questModesConfig.Data.Find(id)!).Type:IsEventQuest(id)?2:1;
    private static string QuestDate(long now)=>DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture);
    private static bool EnsureModeCounterCapacity(SqliteConnection c,SqliteTransaction t,long account,int chapter,string table)
    {
        if(table is not("quest_daily_counts" or "quest_expert_cycles"))throw new InvalidDataException("Unknown mode counter.");
        using var q=Command(c,t,$"SELECT EXISTS(SELECT 1 FROM {table} WHERE account_id=$a AND quest_type_id=$q)");q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$q",chapter);
        var exists=(long)q.ExecuteScalar()! !=0;if(!exists)EnsureInstanceCapacity(c,t,table,1);return exists;
    }
    private int DailyChallengeCount(SqliteConnection c,SqliteTransaction t,long account,int chapter,long now)
    {
        using var q=Command(c,t,"SELECT today_exec_count FROM quest_daily_counts WHERE account_id=$a AND quest_type_id=$q AND business_day=$d");
        q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$q",chapter);q.Parameters.AddWithValue("$d",ActivityDates.Start(2,now));return q.ExecuteScalar() is long n?checked((int)n):0;
    }
    private void ReserveDailyChallenge(SqliteConnection c,SqliteTransaction t,long account,MainlineQuest quest,long now)
    {
        if(RealQuestType(quest.QuestId)!=4||quest.BattleId==0)return;
        var rule=QuestTaCatalog.Contains(quest.QuestId)?new DailySchedule(quest.QuestTypeId,quest.QuestTypeId,"1,2,3,4,5,6,7",0):questModesConfig!.DailySchedules.Single(s=>s.QuestTypeId==quest.QuestTypeId);var before=DailyChallengeCount(c,t,account,quest.QuestTypeId,now);
        // Original count-up campaign50000101 has expired and was not date-overridden.
        // Its old cap+3 is therefore inactive, as in the original current catalog.
        if(rule.ExecMaxCount>0&&before>=rule.ExecMaxCount||before==int.MaxValue)throw new MainlineRejectedException();
        var exists=EnsureModeCounterCapacity(c,t,account,quest.QuestTypeId,"quest_daily_counts");
        using var q=Command(c,t,exists?"UPDATE quest_daily_counts SET business_day=$d,today_exec_count=$count,last_exec_at=$n WHERE account_id=$a AND quest_type_id=$q":"INSERT INTO quest_daily_counts(account_id,quest_type_id,business_day,today_exec_count,last_exec_at) VALUES($a,$q,$d,$count,$n)");
        q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$q",quest.QuestTypeId);q.Parameters.AddWithValue("$d",ActivityDates.Start(2,now));q.Parameters.AddWithValue("$count",before+1);q.Parameters.AddWithValue("$n",now);q.ExecuteNonQuery();
    }
    private bool ExpertChoiceClaimed(SqliteConnection c,SqliteTransaction t,long account,MainlineQuest quest,long now)
    {
        var cycle=questModesConfig!.Cycle(quest.QuestTypeId,now);
        using var q=Command(c,t,"SELECT EXISTS(SELECT 1 FROM user_quests WHERE account_id=$a AND quest_id=$q AND special_reward_received_at>=$s AND special_reward_received_at<$e)");
        q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$q",quest.QuestId);q.Parameters.AddWithValue("$s",cycle.Start);q.Parameters.AddWithValue("$e",cycle.End);return (long)q.ExecuteScalar()! ==1;
    }
    private int[] OfferExpertRewards(SqliteConnection c,SqliteTransaction t,long account,MainlineQuest quest,long now)
    {
        if(RealQuestType(quest.QuestId)!=3||ExpertChoiceClaimed(c,t,account,quest,now))return[];
        var pool=questModesConfig!.SelectRewards.Where(r=>r.QuestId==quest.QuestId).OrderBy(r=>r.SelectRewardId).ToList();var offer=new List<int>();
        while(pool.Count>0&&offer.Count<3)
        {
            var item=GachaWeights.Pick(pool.ToArray(),r=>r.Weight,nextGachaBelow??System.Security.Cryptography.RandomNumberGenerator.GetInt32);
            offer.Add(item.SelectRewardId);pool.Remove(item);
        }
        return offer.ToArray();
    }
    private int FrozenVipRate(SqliteConnection c,SqliteTransaction t,long account,MainlineQuest quest,long now)=>RealQuestType(quest.QuestId)==4&&quest.QuestTypeId is 400101 or 400102 or 400103&&HasActiveVip(c,t,account,now)?2:1;

    private sealed record QuestRankingState(bool Entry,float Score);
    private Dictionary<int,QuestRankingState> ReadQuestRankingStates(SqliteConnection c,SqliteTransaction t,long account)
    {
        var result=questModesConfig!.RankingQuestIds.ToDictionary(q=>q,_=>new QuestRankingState(false,0));
        using var q=Command(c,t,"SELECT route,result_json FROM economy_transactions WHERE account_id=$a AND config_hash=$h AND route IN ('quest/ta_entry','quest/result') ORDER BY created_at,rowid");
        q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$h",QuestModesConfig.ExpectedFingerprint);using var r=q.ExecuteReader();
        while(r.Read())
        {
            using var d=JsonDocument.Parse(r.GetString(1));if(d.RootElement.GetProperty("source").GetString()!="local-quest-modes")throw new InvalidDataException("Invalid typed mode receipt.");var audit=d.RootElement.GetProperty("audit");var id=audit.GetProperty("quest_id").GetInt32();
            if(!result.TryGetValue(id,out var old))continue;
            if(r.GetString(0)==QuestRankingEntryRequest.Route)result[id]=old with{Entry=audit.GetProperty("is_entry").GetBoolean()};
            else if(audit.TryGetProperty("ranking",out var ranked)&&ranked.ValueKind==JsonValueKind.Object)
            {
                var score=ranked.GetProperty("score").GetSingle();if(!float.IsFinite(score)||score<=0)throw new InvalidDataException("Invalid frozen ranking score.");
                if(old.Score==0||score<old.Score)result[id]=old with{Score=score};
            }
        }
        return result;
    }
    private object? FreezeRankingDisplay(SqliteConnection c,SqliteTransaction t,long account,string player,MainlineQuest quest,JsonElement deck,JsonElement[] cards,bool usesSupport=false)
    {
        if(!IsModeQuest(quest.QuestId)||!questModesConfig!.RankingQuestIds.Contains(quest.QuestId)||!ReadQuestRankingStates(c,t,account)[quest.QuestId].Entry)return null;
        using var p=JsonDocument.Parse(player);var user=p.RootElement;
        var display=new Dictionary<string,object?>{["ranking_rank"]=0,["user_id"]=checked((int)account),["name"]=user.GetProperty("name").GetString(),["user_rank"]=user.GetProperty("rank").GetInt32(),["title_id1"]=0,["title_id2"]=0,["score"]=0f,["exec_timestamp"]=0,["deck_total_power"]=0,["is_support"]=usesSupport?1:0};
        using(var q=Command(c,t,"SELECT title_id1,title_id2 FROM user_profiles WHERE account_id=$a"))
        {q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();if(r.Read()){display["title_id1"]=r.GetInt32(0);display["title_id2"]=r.GetInt32(1);}}
        for(var i=1;i<=6;i++)
        {
            var id=deck.GetProperty($"slot_{i}_card_id").GetInt32();var card=cards.SingleOrDefault(card=>card.GetProperty("card_id").GetInt32()==id);
            display[$"slot_{i}_card_id"]=id;display[$"slot_{i}_card_lv"]=id==0?0:card.GetProperty("lv").GetInt32();
            display[$"slot_{i}_card_awakening_count"]=id==0?0:card.GetProperty("awakening_count").GetInt32();display[$"slot_{i}_card_skin_id"]=id==0?0:card.GetProperty("card_skin_id").GetInt32();
        }
        return display;
    }
    private object? ResultRanking(MainlineQuest quest,MainlineRun run,QuestResultRequest request,long now)
    {
        if(!IsModeQuest(quest.QuestId)||!questModesConfig!.RankingQuestIds.Contains(quest.QuestId)||request.ClearRate==0)return null;
        using var frozen=JsonDocument.Parse(run.Payload);
        if(!frozen.RootElement.TryGetProperty("ranking_display",out var display)||display.ValueKind==JsonValueKind.Null)return null;
        // Original result sender floors elapsed seconds to milliseconds (1000 * .001),
        // whereas battle_time is CeilToInt of the same elapsed time. No other score is inferred.
        if(request.BattleTimeTa<=0||request.BattleTimeTa>request.BattleTime+0.001f||request.BattleTime-request.BattleTimeTa>1.001f)throw new MainlineRejectedException();
        var result=display.EnumerateObject().ToDictionary(p=>p.Name,p=>(object?)p.Value.Clone());
        result["score"]=request.BattleTimeTa;result["exec_timestamp"]=checked((int)now);
        result["deck_total_power"]=checked((int)frozen.RootElement.GetProperty("client_deck_total_power").GetSingle());return result;
    }
    private bool ApplyModeResultRules(SqliteConnection c,SqliteTransaction t,long account,MainlineQuest quest,MainlineRun run,QuestResultRequest request,List<MainlineReward> drops,long now)
    {
        if(!IsModeQuest(quest.QuestId)) {if(request.SelectRewardId!=0)throw new MainlineRejectedException();return false;}
        var type=RealQuestType(quest.QuestId);var won=request.ClearRate>0;using var frozen=JsonDocument.Parse(run.Payload);
        if((type!=3||!won||run.Kind==0)&&request.SelectRewardId!=0)throw new MainlineRejectedException();
        if(type==4&&won&&run.Kind==1)
        {
            var rate=frozen.RootElement.GetProperty("vip_drop_rate").GetInt32();if(rate is not(1 or 2))throw new InvalidDataException("Invalid frozen VIP drop rate.");
            for(var i=0;i<drops.Count;i++)if(rate==2)drops[i]=drops[i]with{Qty=checked(drops[i].Qty*2),Vip=true};
        }
        if(type!=3||!won||run.Kind!=1)return false;
        var selected=false;
        if(request.SelectRewardId>0)
        {
            if(ExpertChoiceClaimed(c,t,account,quest,now)||!frozen.RootElement.GetProperty("select_reward_ids").EnumerateArray().Any(r=>r.GetInt32()==request.SelectRewardId))throw new MainlineRejectedException();
            var reward=questModesConfig!.SelectRewards.Single(r=>r.SelectRewardId==request.SelectRewardId&&r.QuestId==quest.QuestId);
            drops.Add(new(reward.ItemType,reward.ItemId,reward.ItemQty,0));selected=true;
        }
        var cycle=questModesConfig!.Cycle(quest.QuestTypeId,now);var limit=questModesConfig.ExpertSchedules.Single(s=>s.QuestTypeId==quest.QuestTypeId).GetCycleQtyLimit;var before=0;
        using(var read=Command(c,t,"SELECT total_cycle_qty FROM quest_expert_cycles WHERE account_id=$a AND quest_type_id=$q AND cycle_start_at=$s"))
        {read.Parameters.AddWithValue("$a",account);read.Parameters.AddWithValue("$q",quest.QuestTypeId);read.Parameters.AddWithValue("$s",cycle.Start);if(read.ExecuteScalar() is long n)before=checked((int)n);}
        var total=before;
        for(var i=0;i<drops.Count;i++)if(drops[i].Type==6&&drops[i].Item==50002)
        {
            var earned=Math.Min(drops[i].Qty,Math.Max(0,limit-total));total=checked(total+earned);drops[i]=drops[i]with{Qty=earned};
        }
        drops.RemoveAll(r=>r.Qty==0);var exists=EnsureModeCounterCapacity(c,t,account,quest.QuestTypeId,"quest_expert_cycles");
        using var q=Command(c,t,exists?"UPDATE quest_expert_cycles SET cycle_start_at=$s,total_cycle_qty=$count,last_exec_at=$n WHERE account_id=$a AND quest_type_id=$q":"INSERT INTO quest_expert_cycles(account_id,quest_type_id,cycle_start_at,total_cycle_qty,last_exec_at) VALUES($a,$q,$s,$count,$n)");
        q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$q",quest.QuestTypeId);q.Parameters.AddWithValue("$s",cycle.Start);q.Parameters.AddWithValue("$count",total);q.Parameters.AddWithValue("$n",now);q.ExecuteNonQuery();return selected;
    }
    private static void SaveExpertChoice(SqliteConnection c,SqliteTransaction t,long account,int quest,long now)
    {
        using var q=Command(c,t,"UPDATE user_quests SET special_reward_received_at=$n WHERE account_id=$a AND quest_id=$q");
        q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$q",quest);q.Parameters.AddWithValue("$n",now);if(q.ExecuteNonQuery()!=1)throw new MainlineRejectedException();
    }
    private void ProjectQuestModes(SqliteConnection c,SqliteTransaction t,long account,long now,Dictionary<string,object?> action)
    {
        if(questModesConfig is null)return;
        var daily=new List<object>();
        using(var q=Command(c,t,"SELECT id,quest_type_id,business_day,today_exec_count,last_exec_at FROM quest_daily_counts WHERE account_id=$a ORDER BY id"))
        {q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();while(r.Read())daily.Add(new{id=r.GetInt32(0),user_id=checked((int)account),quest_type_id=r.GetInt32(1),today_exec_count=r.GetInt64(2)==ActivityDates.Start(2,now)?r.GetInt32(3):0,last_exec_date=QuestDate(r.GetInt64(4))});}
        var expert=new List<object>();
        using(var q=Command(c,t,"SELECT id,quest_type_id,cycle_start_at,total_cycle_qty,last_exec_at FROM quest_expert_cycles WHERE account_id=$a ORDER BY id"))
        {q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();while(r.Read())expert.Add(new{id=r.GetInt32(0),user_id=checked((int)account),quest_type_id=r.GetInt32(1),total_cycle_qty=r.GetInt64(2)==questModesConfig.Cycle(r.GetInt32(1),now).Start?r.GetInt32(3):0,last_exec_date=QuestDate(r.GetInt64(4))});}
        action["u_quest_daily"]=daily.ToArray();action["u_quest_hard"]=expert.ToArray();
        action["u_quest_ranking"]=ReadQuestRankingStates(c,t,account).OrderBy(r=>r.Key).Select(r=>new{quest_id=r.Key,is_entry=r.Value.Entry?1:0,score=r.Value.Score}).ToArray();
    }
    public AccountResult ChangeQuestRankingEntry(QuestRankingEntryRequest request)
    {
        if(QuestTaCatalog.Contains(request.QuestId))return ChangeTrainingTaEntry(request);
        if(questModesConfig is null)return new(501);if(!questModesConfig.RankingQuestIds.Contains(request.QuestId))return new(409);
        return MutateMainline(request.AuthToken,request.RetryToken,QuestRankingEntryRequest.Route,request.Semantic,(c,t,account,player,now)=>
        {
            _=RequireMainlineQuest(c,t,account,request.QuestId,now);
            return new(player,[],new{quest_id=request.QuestId,is_entry=request.IsEntry},new{},QuestModesConfig.ExpectedFingerprint);
        });
    }
    public AccountResult ListQuestRanking(QuestRankingRequest request)
    {
        if(questModesConfig is null)return new(501);if(!questModesConfig.RankingQuestIds.Contains(request.QuestId))return new(409);
        return ReadQuestState(request.AuthToken,(c,t,account,now)=>
        {
            var best=new Dictionary<int,(float Score,long Time,Dictionary<string,object?> Wire)>();
            using var q=Command(c,t,"SELECT account_id,result_json,created_at FROM economy_transactions WHERE route='quest/result' AND config_hash=$h AND created_at<=$n AND json_extract(result_json,'$.audit.quest_id')=$q ORDER BY created_at,rowid");
            q.Parameters.AddWithValue("$h",QuestModesConfig.ExpectedFingerprint);q.Parameters.AddWithValue("$q",request.QuestId);q.Parameters.AddWithValue("$n",now);
            long latest=0;using(var r=q.ExecuteReader())while(r.Read())
            {
                using var doc=JsonDocument.Parse(r.GetString(1));if(doc.RootElement.GetProperty("source").GetString()!="local-quest-modes")throw new InvalidDataException("Invalid ranking source.");var audit=doc.RootElement.GetProperty("audit");
                if(!audit.TryGetProperty("ranking",out var rank)||rank.ValueKind!=JsonValueKind.Object)continue;
                var id=r.GetInt32(0);var score=rank.GetProperty("score").GetSingle();var at=r.GetInt64(2);
                if(!float.IsFinite(score)||score<=0)throw new InvalidDataException("Invalid ranking receipt.");latest=Math.Max(latest,at);
                if(!best.TryGetValue(id,out var old)||score<old.Score)best[id]=(score,at,rank.EnumerateObject().ToDictionary(p=>p.Name,p=>(object?)p.Value.Clone()));
            }
            var ordered=best.OrderBy(p=>p.Value.Score).ThenBy(p=>p.Value.Time).ThenBy(p=>p.Key).Select((p,i)=>{p.Value.Wire["ranking_rank"]=i+1;return p;}).ToArray();
            return new(){["last_aggregated_date"]=latest==0?"":QuestDate(latest),["my_ranking_info"]=ordered.SingleOrDefault(p=>p.Key==account).Value.Wire,["ranking_info"]=ordered.Select(p=>p.Value.Wire).ToArray()};
        });
    }
}
