using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
    private sealed class MainlineRejectedException : Exception;
    private sealed record MainlineMutation(string Player, Dictionary<string, object?> Extras, object Audit, object Cost, string ConfigHash = MainlineConfig.ExpectedFingerprint,int ErrorId=0,string ErrorMessage="",object[]? NotificationItems=null);

    private AccountResult MutateMainline(string? auth, string retry, string route, string semantic,
        Func<SqliteConnection, SqliteTransaction, long, string, long, MainlineMutation> mutate)
    {
        if (mainlineConfig is null) return new(501);
        if (string.IsNullOrEmpty(auth)) return new(401);
        lock (gate)
        {
            using var c = Open(); using var t = c.BeginTransaction(deferred: false); var now = Now;
            if (!TryReadAuthenticatedAccount(c, t, auth, now, out var account, out var player)) return new(401);
            var hash = Hash(retry); var old = FindEconomyReceipt(c, t, account, hash);
            if (old is not null)
            {
                if (old.Route != route || old.Semantic != semantic) return new(409);
                using var frozen = JsonDocument.Parse(old.Result);
                var replay = Snapshot(c, t, account, player, nowOverride: now);
                foreach (var p in frozen.RootElement.GetProperty("action").EnumerateObject()) replay[p.Name] = p.Value.Clone();
                var notifications = frozen.RootElement.TryGetProperty("notification_items", out var notices)
                    ? notices.EnumerateArray().Select(x => (object)x.Clone()).ToArray() : [];
                var badge = notifications.Length > 0 ? PresentPendingCounts(c,t,account,now).Equip : (int?)null;
                return new(200, replay, SendPresentBoxItems:notifications, PresentEquipBadge:badge, IsSendPresentBox:notifications.Length>0, ProtocolErrorId:frozen.RootElement.TryGetProperty("error_id",out var error)?error.GetInt32():0, ProtocolErrorMessage:frozen.RootElement.TryGetProperty("error_msg",out var message)?message.GetString()??"":"");
            }
            try
            {
                var changed = mutate(c, t, account, player, now);
                var frozen = JsonSerializer.Serialize(new { error_id=changed.ErrorId,error_msg=changed.ErrorMessage,source = changed.ConfigHash==LoopFingerprint?"local-quest-loop":changed.ConfigHash==QuestTaCatalog.Fingerprint?"local-training-ta":changed.ConfigHash == QuestModesConfig.ExpectedFingerprint ? "local-quest-modes" : changed.ConfigHash == EventQuestConfig.ExpectedFingerprint ? "local-event-quest" : "local-normal-mainline", transaction_id = Guid.NewGuid().ToString("D"),
                    action = changed.Extras, audit = changed.Audit, notification_items = changed.NotificationItems ?? [] });
                InsertEconomyReceipt(c, t, account, hash, route, semantic, changed.ConfigHash, frozen,
                    JsonSerializer.Serialize(changed.Cost), now);
                if (changed.Player != player) SavePlayer(c, t, account, changed.Player);
                ObserveActivityDailyClear(c,t,account,changed.Player,now);
                var result = Snapshot(c, t, account, changed.Player, nowOverride: now);
                foreach (var p in changed.Extras) result[p.Key] = p.Value;
                var notifications = changed.NotificationItems ?? [];
                var badge = notifications.Length > 0 ? PresentPendingCounts(c,t,account,now).Equip : (int?)null;
                t.Commit(); return new(200, result,SendPresentBoxItems:notifications,PresentEquipBadge:badge,IsSendPresentBox:notifications.Length>0,ProtocolErrorId:changed.ErrorId,ProtocolErrorMessage:changed.ErrorMessage);
            }
            catch (MainlineRejectedException) { return new(409); }
            catch (GachaCapacityException) { return new(503); }
            catch (OverflowException) { return new(503); }
        }
    }

    private MainlineQuest RequireMainlineQuest(SqliteConnection c, SqliteTransaction t, long account, int quest, long now, bool allowFrozenRun = false)
    {
        var definition = QuestData(quest).Find(quest);
        if(definition is null)throw new MainlineRejectedException();
        var active=allowFrozenRun&&IsModeQuest(quest)&&RealQuestType(quest)==3?ActiveMainlineRun(c,t,account):null;
        if(active is not null&&active.QuestId==quest&&active.ConfigHash==QuestHash(quest))return definition;
        if (!(QuestTaCatalog.Contains(quest)?QuestTaCatalog.Data.IsOpen(definition,now):IsModeQuest(quest) ? IsModeQuestAvailable(definition, now) : IsEventQuest(quest) ? eventQuestConfig!.IsOpen(definition, now) : mainlineConfig!.IsOpen(definition, now)) ||
            !OwnsExtraShopGate(c,t,account,definition.ConditionExtraShopProductId) || definition.Prerequisites.Any(q => !QuestCleared(c, t, account, q))) throw new MainlineRejectedException();
        return definition;
    }

    private int InsertMainlineRun(SqliteConnection c, SqliteTransaction t, long account, int quest, int kind,
        object payload, int cost, long now)
    {
        EnsureInstanceCapacity(c, t, "quest_runs", 1);
        using var q = Command(c, t, "INSERT INTO quest_runs(account_id,quest_id,kind,state,config_hash,payload_json,result_json,stamina_spent,created_at,finished_at) VALUES($account,$quest,$kind,$kind,$config,$payload,'{}',$cost,$now,0) RETURNING id");
        q.Parameters.AddWithValue("$account", account); q.Parameters.AddWithValue("$quest", quest); q.Parameters.AddWithValue("$kind", kind);
        q.Parameters.AddWithValue("$config", QuestHash(quest)); q.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload));
        q.Parameters.AddWithValue("$cost", cost); q.Parameters.AddWithValue("$now", now); return checked((int)(long)q.ExecuteScalar()!);
    }

    public AccountResult PrepareQuestStory(QuestStoryRequest request)
    {
        if (request.QuestId <= 0 || request.StoryId <= 0) return new(400);
        return MutateMainline(request.AuthToken, request.RetryToken, QuestStoryRequest.Route, request.Semantic,
            (c, t, account, player, now) =>
            {
                var quest = RequireMainlineQuest(c, t, account, request.QuestId, now, allowFrozenRun:true);
                var before = quest.BeforeStoryId == request.StoryId; var after = quest.AfterStoryId == request.StoryId;
                var active = ActiveMainlineRun(c, t, account);
                var ownBattle = active is { Kind: 1, State: 1 } && active.QuestId == quest.QuestId;
                if (!before && !after || after && !ownBattle && !QuestCleared(c, t, account, quest.QuestId)) throw new MainlineRejectedException();
                if (active is not null && active.QuestId != quest.QuestId)
                {
                    if (active.Kind != 0) throw new MainlineRejectedException();
                    FinishMainlineRun(c, t, account, active.Id, 4, new { reason = "superseded-story-preparation" }, now);
                    active = null;
                }
                int? run = active?.Id;
                if (quest.BattleId == 0)
                {
                    if (active is not null && active.Kind != 0) throw new MainlineRejectedException();
                    run ??= InsertMainlineRun(c, t, account, quest.QuestId, 0,
                        new { story_id = request.StoryId, card_ids = Array.Empty<int>() }, 0, now);
                }
                // Story preparation is not completion. No quest/progress/reward
                // row is written until the separate authenticated result call.
                return new(player, [], new { quest_id = quest.QuestId, story_id = request.StoryId, run_id = run }, new { stamina_spent = 0 }, QuestHash(quest.QuestId));
            });
    }

    public AccountResult ListQuestSupport(QuestSupportRequest request) => ReadQuestState(request.AuthToken, (c, t, account, now) =>
        BotSupportList(c,t,account,now));

    public AccountResult ActiveQuestSupport(QuestActiveSupportRequest request) => request.SupportUserId==0||LocalBotCatalog.Known(request.SupportUserId)
        ? ActiveLocalBotSupport(request) : string.IsNullOrEmpty(request.AuthToken)?new(401):new(409);

    private AccountResult ReadQuestState(string? auth, Func<SqliteConnection, SqliteTransaction, long, long, Dictionary<string, object?>> project)
    {
        if (mainlineConfig is null) return new(501);
        if (string.IsNullOrEmpty(auth)) return new(401);
        lock (gate)
        {
            using var c = Open(); using var t = c.BeginTransaction(deferred: true); var now = Now;
            if (!TryReadAuthenticatedAccount(c, t, auth, now, out var account, out var player)) return new(401);
            var action = Snapshot(c, t, account, player, nowOverride: now);
            foreach (var p in project(c, t, account, now)) action[p.Key] = p.Value;
            return new(200, action);
        }
    }

    public AccountResult CheckQuest(QuestCheckRequest request)
    {
        if (request.QuestId <= 0 || request.RestartPoint < 0) return new(400);
        if (mainlineConfig is not null && QuestData(request.QuestId).Find(request.QuestId) is null) return new(409);
        return ReadQuestState(request.AuthToken, (c, t, account, now) =>
        {
            var run = ActiveMainlineRun(c, t, account);
            return new() { ["is_restart"] = run is { Kind: 1, State: 1 } && run.QuestId == request.QuestId && run.ConfigHash == QuestHash(request.QuestId) };
        });
    }

    public AccountResult StartQuest(QuestStartRequest request)
    {
        if (request.QuestId <= 0 || request.DeckNo is < 1 or > 10 || request.SetLoopCount is <0 or >10 || request.LoopCount<0 || request.SetLoopCount==0&&(request.LoopCount!=0||request.IsCureStaminaUseCoin) || request.SetLoopCount>0&&(request.LoopCount<1||request.LoopCount>request.SetLoopCount) || !float.IsFinite(request.DeckTotalPower) ||
            !float.IsFinite(request.MaxUnitPower) || request.DeckTotalPower < 0 || Math.Ceiling((double)request.DeckTotalPower)>int.MaxValue || request.MaxUnitPower < 0 || Math.Ceiling((double)request.MaxUnitPower)>int.MaxValue)
            return new(400);
        return MutateMainline(request.AuthToken, request.RetryToken, QuestStartRequest.Route, request.Semantic,
            (c, t, account, player, now) =>
            {
                var stop=LoopKnownStop(c,t,account,player,request,now);if(stop is not null)return stop;
                var quest = RequireMainlineQuest(c, t, account, request.QuestId, now);
                CheckEventStartLimit(c,t,account,quest,now);
                if (quest.BattleId == 0 || formationConfig is null)
                    throw new MainlineRejectedException();
                var active = ActiveMainlineRun(c, t, account);
                if (active is { Kind: 1 } || request.SetLoopCount>0&&active is not null) throw new MainlineRejectedException();
                if (active is not null) FinishMainlineRun(c, t, account, active.Id, 4, new { reason = "superseded-story-preparation" }, now);
                var deck = DeckRows(c, t, account).SingleOrDefault(d => d.GetProperty("deck_no").GetInt32() == request.DeckNo);
                if (deck.ValueKind != JsonValueKind.Object) throw new MainlineRejectedException();
                var slots = Enumerable.Range(1, 6).Select(slot => new DeckSlot(deck.GetProperty($"slot_{slot}_card_id").GetInt32(),
                    FormationRequestFields.EquipIds(deck.GetProperty($"slot_{slot}_user_equip_ids")))).ToArray();
                if (!FormationRules.ValidateDeck(formationConfig, slots, FormationCards(c, t, account), FormationEquips(c, t, account)))
                    throw new MainlineRejectedException();
                var loop=CheckLoopStart(c,t,account,request,quest,deck,now);
                var cure=PlanLoopCure(c,t,account,player,quest,request,now);
                if(cure is null){if(loop is not null)return EndLoopError(c,t,account,player,loop,now,2218);throw new MainlineRejectedException();}
                var wallet = JsonNode.Parse(cure.Player)!.AsObject(); var stamina = MainlineStamina.Read(wallet, now).Spend(quest.Stamina, now);
                if (stamina is null) throw new MainlineRejectedException();
                stamina.Save(wallet);
                var ids = slots.Where(s => s.CardId > 0).Select(s => s.CardId).ToArray();
                var inventory = Snapshot(c, t, account, player, nowOverride: now);
                var frozenCards = ((JsonElement[])inventory["u_card"]!).Where(row => ids.Contains(row.GetProperty("card_id").GetInt32())).ToArray();
                var equipIds = slots.SelectMany(s => s.EquipIds).Where(id => id > 0).ToHashSet();
                var frozenEquips = ((JsonElement[])inventory["u_equip"]!).Where(row => equipIds.Contains(row.GetProperty("user_equip_id").GetInt32())).ToArray();
                var effects = IsEventQuest(quest.QuestId) ? FrozenEventEffects(eventQuestConfig!.Chapter(quest).EventId,frozenCards,frozenEquips) : [];
                ReserveDailyChallenge(c,t,account,quest,now);
                var offer=OfferExpertRewards(c,t,account,quest,now);
                var botSupport=loop is null?FreezeLocalBotSupport(c,t,account,quest,request.SupportUserId,now):loop.Frozen.GetProperty("bot_support").Clone();
                SpendLoopFoods(c,t,account,cure);
                var loopSession=BeginLoopRun(c,t,account,request,loop,deck,botSupport,now);
                var run = InsertMainlineRun(c, t, account, quest.QuestId, 1, new { loop_session_id=loopSession,loop_count=request.LoopCount,ta_entry=FreezeTaEntry(c,t,account,quest.QuestId),quest_type=RealQuestType(quest.QuestId), mode_config=QuestHash(quest.QuestId), expert_availability=RealQuestType(quest.QuestId)==3?expertAvailabilityFingerprint:null, vip_drop_rate=FrozenVipRate(c,t,account,quest,now), select_reward_ids=offer, ranking_display=FreezeRankingDisplay(c,t,account,player,quest,deck,frozenCards,request.SupportUserId!=0), deck, cards = frozenCards, equips = frozenEquips, card_ids = ids, event_point_up = effects.Sum(e=>e.PointRate-100), event_effects = effects,
                    bot_support=botSupport, support_user_id = request.SupportUserId, client_deck_total_power = request.DeckTotalPower, client_max_unit_power = request.MaxUnitPower }, quest.Stamina, now);
                BindLoopRun(c,t,account,loopSession,run);
                if (IsEventQuest(quest.QuestId)) TouchEventQuest(c,t,account,quest,0,now);
                return new(wallet.ToJsonString(), new() { ["select_reward_ids"] = offer, ["is_follow_max"] = IsLocalBotFollowMax(c,t,account,player) }, new { run_id = run, quest_id = quest.QuestId,loop_session_id=loopSession, own_deck_report = request.SupportUserId==0 ? new { source="client-own-deck-report", power=checked((int)Math.Ceiling((double)request.DeckTotalPower)), support_user_id=0 } : null }, new { stamina_spent = quest.Stamina,items=cure.Foods,free_spent=cure.Free,paid_spent=cure.Paid,stamina_added=cure.Added }, QuestHash(quest.QuestId));
            });
    }

    public AccountResult RetireQuest(QuestRetireRequest request)
    {
        if (request.QuestId <= 0) return new(400);
        return MutateMainline(request.AuthToken, request.RetryToken, QuestRetireRequest.Route, request.Semantic,
            (c, t, account, player, now) =>
            {
                var run = ActiveMainlineRun(c, t, account);
                if (run is null || run.QuestId != request.QuestId) throw new MainlineRejectedException();
                var extras = new Dictionary<string, object?> { ["is_follow_max"] = IsLocalBotFollowMax(c,t,account,player), ["is_restoration_stamina"] = false };
                FinishMainlineRun(c, t, account, run.Id, 4, extras, now);
                CompleteLoopRun(c,t,account,run,false,now);
                return new(player, extras, new { run_id = run.Id, quest_id = run.QuestId, retired = true }, new { stamina_refund = 0 }, run.ConfigHash);
            });
    }

    private static void FinishMainlineRun(SqliteConnection c, SqliteTransaction t, long account, int run, int state, object result, long now)
    {
        using var q = Command(c, t, "UPDATE quest_runs SET state=$state,result_json=$result,finished_at=$now WHERE id=$run AND account_id=$account AND state IN (0,1)");
        q.Parameters.AddWithValue("$state", state); q.Parameters.AddWithValue("$result", JsonSerializer.Serialize(result)); q.Parameters.AddWithValue("$now", now);
        q.Parameters.AddWithValue("$run", run); q.Parameters.AddWithValue("$account", account);
        if (q.ExecuteNonQuery() != 1) throw new MainlineRejectedException();
    }

    public AccountResult ReportQuestTotalPower(MissionTotalPowerRequest request)
    {
        if (request.TotalPower < 0) return new(400);
        return MutateMainline(request.AuthToken, request.RetryToken, MissionTotalPowerRequest.Route, request.Semantic,
            (c, t, account, player, now) =>
            {
                using var q = Command(c, t, "INSERT INTO quest_power_reports(account_id,last_total_power,maximum_total_power,reported_at) VALUES($account,$power,$power,$now) ON CONFLICT(account_id) DO UPDATE SET last_total_power=excluded.last_total_power,maximum_total_power=MAX(maximum_total_power,excluded.maximum_total_power),reported_at=excluded.reported_at");
                q.Parameters.AddWithValue("$account", account); q.Parameters.AddWithValue("$power", request.TotalPower); q.Parameters.AddWithValue("$now", now); q.ExecuteNonQuery();
                return new(player, [], new { client_reported_total_power = request.TotalPower, authority = "client-report-only" }, new { });
            });
    }
}
