using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    public AccountResult FinishQuest(QuestResultRequest request,Func<int,int>? voiceRandom=null)
    {
        if (!ValidMainlineResultShape(request)) return new(400);
        return MutateMainline(request.AuthToken, request.RetryToken, QuestResultRequest.Route, request.Semantic,
            (c, t, account, player, now) =>
            {
                var run = ActiveMainlineRun(c, t, account);
                if (run is null || run.QuestId != request.QuestId || run.ConfigHash != QuestHash(request.QuestId))
                    throw new MainlineRejectedException();
                ValidateLoopResult(c,t,account,run,request);
                var grantContext = new EquipmentGrantContext(request.EquipmentOptions, run.Kind == 1 ? new BattleEquipmentOverflow(run.Id, Convert.ToHexStringLower(Hash(request.RetryToken)), run.ConfigHash) : null);
                var cfg = QuestData(run.QuestId);
                var quest = cfg.Find(run.QuestId) ?? throw new MainlineRejectedException();
                var story = quest.BattleId == 0;
                if (story != (run.Kind == 0) || story && !ValidPureStoryResult(request)) throw new MainlineRejectedException();
                if (!story)
                {
                    // These are the original six Int32 report keys. MinValue
                    // is a legitimate original 'no recorded damage' sentinel.
                    var decoded = new byte[request.BattleData.Length];
                    if (!Convert.TryFromBase64String(request.BattleData, decoded, out var length)) throw new MainlineRejectedException();
                    using var battle = JsonDocument.Parse(decoded.AsMemory(0, length));
                    FormationRequestFields.Fields(battle.RootElement, "max_unit_power", "max_atk", "max_hp", "max_attack_damage", "max_min_skill_damage", "max_kill_ability_damage");
                    foreach (var p in battle.RootElement.EnumerateObject())
                        if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out _)) throw new MainlineRejectedException();
                }
                if(!IsEventQuest(quest.QuestId) && request.GetDropPlaceIds.Length!=0) throw new MainlineRejectedException();
                var won = request.ClearRate > 0;
                var newMissions = won ? cfg.Missions.Where(m => m.QuestId == quest.QuestId && MainlineMissionAchieved(m, request) &&
                    !HasMainlineMission(c, t, account, quest.QuestId, m.MissionNo)).OrderBy(m => m.MissionNo).ToArray() : [];
                var drops = won && !story ? DrawMainlineRewards(quest,cfg) : [];
                var taResult=ApplyTrainingTaResult(c,t,account,run,request,drops);
                var selectedChoice=ApplyModeResultRules(c,t,account,quest,run,request,drops,now);
                var ranked=ResultRanking(quest,run,request,now);
                var manualDrops = won && !story ? PrepareEventManualDrops(c,t,account,quest,request) : [];
                if(won && !story) AddOrnamentDrops(quest,drops,run.StaminaSpent,now);
                if(won && !story) ApplyEventFlatDrops(run,drops);
                if (won && quest.ClearGameCoin > 0) drops.Insert(0, new(4, 0, quest.ClearGameCoin, 0));
                EnsureEquipmentCapacity(c, t, account, drops.Concat(manualDrops.Select(d=>d.Reward)).Where(r => r.Type == 7).Select(r =>
                    (cfg.Equips.Single(e => e.EquipId == r.Item), r.Qty)), grantContext);
                var wallet = JsonNode.Parse(player)!.AsObject();
                var botFp=GrantLocalBotVictory(wallet,run,won,story);
                var oldMastery = ReadQuestMastery(c,t,account);
                var oldRank = wallet["rank"]!.GetValue<int>();
                var preRankStamina = won && !story ? MainlineStamina.Read(wallet, now).Current : wallet["stamina"]!.GetValue<int>();
                var rate = won ? cfg.ClearRates.Single(r => r.ClearRate == request.ClearRate) : null;
                // Local reconstruction: original percent multiplier then final
                // nonnegative floor. No campaign or VIP mainline multiplier.
                var exp = rate is null || story ? 0 : checked((int)((long)quest.ClearUserExp * rate.UserExpRate / 100));
                var mastery = rate is null || story ? 0 : checked((int)((long)quest.ClearCardMasteryExp * rate.CardMasteryExpRate / 100));
                using var payload = JsonDocument.Parse(run.Payload);
                var cardIds = payload.RootElement.GetProperty("card_ids").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                if (won && !story) ApplyMainlineExperience(c, t, account, wallet, exp, mastery, cardIds, now);
                var treasure = drops.Select(r => ApplyMainlineReward(c, t, account, wallet, r, now,cfg, grantContext)).ToArray();
                var manualTreasure = manualDrops.Select(r=>ApplyMainlineReward(c,t,account,wallet,r.Reward,now,cfg, grantContext)).ToArray();
                var typedGrants = drops.Select(r=>new CommerceReward(r.Type,r.Item,r.Qty)).Concat(manualDrops.Select(r=>new CommerceReward(r.Reward.Type,r.Reward.Item,r.Reward.Qty))).ToList();
                if(botFp>0)typedGrants.Add(new(3,0,botFp));
                typedGrants.AddRange(cfg.Ranks.Where(r=>r.Rank>oldRank&&r.Rank<=wallet["rank"]!.GetValue<int>()&&r.ItemType==2).Select(r=>new CommerceReward(r.ItemType,r.ItemId,r.ItemQty)));
                var first = false;
                if (won)
                {
                    first = !QuestCleared(c, t, account, quest.QuestId);
                    if(IsEventQuest(quest.QuestId)) TouchEventQuest(c,t,account,quest,request.ClearRate,now);
                    else SaveMainlineProgress(c, t, account, quest.QuestId, request.ClearRate, now);
                    if(selectedChoice) SaveExpertChoice(c,t,account,quest.QuestId,now);
                    foreach (var mission in newMissions)
                    {
                        var reward = new MainlineReward(mission.ItemType, mission.ItemId, mission.ItemQty, 0);
                        _ = ApplyMainlineReward(c, t, account, wallet, reward, now,cfg, grantContext);
                        typedGrants.Add(new(reward.Type,reward.Item,reward.Qty));
                        SaveQuestMission(c,t,account,quest,mission.MissionNo,reward,now);
                    }
                    var chapter = cfg.Chapters.Single(ch => ch.QuestTypeId == quest.QuestTypeId);
                    if(RealQuestType(quest.QuestId)==1) wallet["main_quest_part_no"] = Math.Max(wallet["main_quest_part_no"]!.GetValue<int>(), chapter.PartNo);
                }
                SaveEventManualDrops(c,t,account,quest,run,manualDrops,now);
                var pointGrants=AddEventPoints(c,t,account,quest,run,request,wallet,now,out var earnedPoint, grantContext);
                typedGrants.AddRange(pointGrants);
                var rankRestoredStamina = wallet["rank"]!.GetValue<int>() > oldRank && wallet["stamina"]!.GetValue<int>() > preRankStamina;
                var voice = won && !story ? GrantBattleVoice(c,t,account,payload.RootElement,now,voiceRandom) : new BattleVoiceSelection(0,[],[],[],false);
                var extras = new Dictionary<string, object?>
                {
                    ["treasure"] = treasure, ["is_convert"] = false, ["add_user_exp"] = exp, ["add_card_mastery_exp"] = mastery,
                    ["is_follow_max"] = IsLocalBotFollowMax(c,t,account,wallet.ToJsonString()), ["new_clear_mission_nos"] = newMissions.Select(m => m.MissionNo).ToArray(),
                    ["is_restoration_stamina"] = rankRestoredStamina, ["chara_voice_id"] = voice.CharaVoiceId, ["get_drop_items"] = manualTreasure
                };
                FinishMainlineRun(c, t, account, run.Id, won ? 2 : 3, extras, now);
                CompleteLoopRun(c,t,account,run,won,now);
                return new(wallet.ToJsonString(), extras, new { run_id = run.Id, quest_id = quest.QuestId, quest_type = RealQuestType(quest.QuestId), ta_score=taResult.Score,ta_participation_award=taResult.Awarded,ranking = ranked, first_clear = first,
                    expert_random_token_delta = RealQuestType(quest.QuestId)==3 ? drops.Where(r=>r.Type==6&&r.Item==50002).Sum(r=>r.Qty) : 0,
                    expert_mission_token_delta = RealQuestType(quest.QuestId)==3 ? newMissions.Where(m=>m.ItemType==6&&m.ItemId==50002).Sum(m=>m.ItemQty) : 0,
                    voice_selection=voice, bot_friend_point_delta=botFp, support_user_id=story?0:payload.RootElement.GetProperty("support_user_id").GetInt32(), outcome = won ? "clear" : "failure", client_result = new { request.ClearRate, request.BattleTime,
                        request.KillAbilityCount, request.MainSkillCount, request.DefeatEnemy, request.DefeatBoss, request.IsAllDefenseBaseSafety,
                        request.BattleData, request.BattleTimeTa, request.TotalPower }, original_config = run.ConfigHash, typed_grants = typedGrants, event_point_delta = earnedPoint, manual_place_count = manualDrops.Count, mastery_level_delta = ReadQuestMastery(c,t,account).Sum(p=>Math.Max(0,p.Value-oldMastery.GetValueOrDefault(p.Key))) },
                    new { stamina_previously_spent = run.StaminaSpent, stamina_refund = 0 }, run.ConfigHash,
                    NotificationItems: BattleEquipmentOverflowNotifications(c,t,account,grantContext));
            });
    }

    private bool HasMainlineMission(SqliteConnection c, SqliteTransaction t, long account, int quest, int mission)
    {
        using var q = Command(c, t, "SELECT EXISTS(SELECT 1 FROM " + (IsEventQuest(quest) ? "event_quest_mission_claims" : "quest_mission_claims") + " WHERE account_id=$account AND quest_id=$quest AND mission_no=$no)");
        q.Parameters.AddWithValue("$account", account); q.Parameters.AddWithValue("$quest", quest); q.Parameters.AddWithValue("$no", mission);
        return (long)q.ExecuteScalar()! == 1;
    }

    private static void SaveMainlineProgress(SqliteConnection c, SqliteTransaction t, long account, int quest, int rate, long now)
    {
        using (var count = Command(c, t, "SELECT clear_count FROM user_quests WHERE account_id=$account AND quest_id=$quest"))
        {
            count.Parameters.AddWithValue("$account", account); count.Parameters.AddWithValue("$quest", quest);
            if (count.ExecuteScalar() is long n)
            {
                if (n >= int.MaxValue) throw new GachaCapacityException();
                using var update = Command(c, t, "UPDATE user_quests SET clear_rate=MAX(clear_rate,$rate),clear_count=clear_count+1,last_clear_at=$now WHERE account_id=$account AND quest_id=$quest");
                update.Parameters.AddWithValue("$rate", rate); update.Parameters.AddWithValue("$now", now);
                update.Parameters.AddWithValue("$account", account); update.Parameters.AddWithValue("$quest", quest); update.ExecuteNonQuery(); return;
            }
        }
        EnsureInstanceCapacity(c, t, "user_quests", 1);
        using var insert = Command(c, t, "INSERT INTO user_quests(account_id,quest_id,clear_rate,clear_count,special_reward_received_at,last_clear_at) VALUES($account,$quest,$rate,1,0,$now)");
        insert.Parameters.AddWithValue("$account", account); insert.Parameters.AddWithValue("$quest", quest); insert.Parameters.AddWithValue("$rate", rate);
        insert.Parameters.AddWithValue("$now", now); insert.ExecuteNonQuery();
    }

    private static bool MainlineMissionAchieved(MainlineMission mission, QuestResultRequest r) => mission.MissionType switch
    {
        1 => true,
        2 => r.ClearRate == 6,
        3 => r.BattleTime <= mission.MissionValue,
        7 => r.IsAllDefenseBaseSafety,
        8 => r.DefeatEnemy.Values.Sum(n => (long)n) >= mission.MissionValue,
        _ => throw new InvalidDataException("Unsupported mainline mission predicate.")
    };

    private static bool ValidMainlineResultShape(QuestResultRequest r) => r.QuestId > 0 && r.ClearRate is >= 0 and <= 6 &&
        r.BattleTime is >= 0 and <= 604800 && r.KillAbilityCount is >= 0 and <= 1000000 && r.MainSkillCount is >= 0 and <= 1000000 &&
        r.DefeatEnemy.Count <= 4096 && r.DefeatBoss.Count <= 4096 && r.DefeatEnemy.All(p => p.Key > 0 && p.Value is >= 0 and <= 1000000) &&
        r.DefeatBoss.All(p => p.Key > 0 && p.Value is >= 0 and <= 1000000) && (r.IsLoopMode?r.LoopCount is >=1 and <=10:r.LoopCount==0) && r.SelectRewardId >= 0 &&
        float.IsFinite(r.BattleTimeTa) && r.BattleTimeTa is >= 0 and <= 604800 && float.IsFinite(r.TotalPower) && r.TotalPower is >= 0 and <= int.MaxValue &&
        r.GetDropPlaceIds.Length <= 4096 && r.GetDropPlaceIds.All(p=>p>0) && r.GetDropPlaceIds.Distinct().Count()==r.GetDropPlaceIds.Length && r.BattleData.Length <= 65536;

    private static bool ValidPureStoryResult(QuestResultRequest r) => r.ClearRate == 6 && r.BattleTime == 0 && r.KillAbilityCount == 0 &&
        r.MainSkillCount == 0 && r.DefeatEnemy.Count == 0 && r.DefeatBoss.Count == 0 && !r.IsAllDefenseBaseSafety &&
        !r.IsLoopMode && r.LoopCount==0 && r.BattleData == "" && r.BattleTimeTa == 0 && r.TotalPower == 0 && r.GetDropPlaceIds.Length==0;
}
