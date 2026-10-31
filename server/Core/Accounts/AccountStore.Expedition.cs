using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
 private ExpeditionCatalog? expeditionCatalog;
 private GrowthCatalog? expeditionGrowth;
 private sealed class ExpeditionRejectedException:Exception;
 private sealed record ExpeditionRun(int Id,int Slot,int Destination,string Hash,string Frozen,int[] Roster,long Start,long End,int State,string Result);
 private sealed record ExpeditionMutation(string Player,Dictionary<string,object?> Action,object Audit,object Cost);
 public void ConfigureExpedition(ExpeditionCatalog catalog,GrowthCatalog growth)
 {
  lock(gate)
  {
   if(expeditionCatalog is not null||activityConfig is null||miscCatalog is null||formationConfig is null||allGachaConfig is null||commerceConfig is null||growth.Fingerprint!=GrowthCatalog.ExpectedFingerprint)throw new InvalidDataException("Expedition requires proven inventory, progress and growth catalogs.");
   foreach(var d in catalog.Drops)
   {
    if(d.ItemType is 6 or 12&&!QuestRewardReference(d.ItemType,d.ItemId)||d.ItemType==7&&(inventoryMaterialConfig.Find(d.ItemId)is null||CommerceEquip(d.ItemId)is null))throw new InvalidDataException("Unknown expedition item reference.");
   }
   expeditionCatalog=catalog;expeditionGrowth=growth;
  }
 }
 public AccountResult Expedition(ExpeditionRequest request,Func<int,int>? random=null)
 {
  if(expeditionCatalog is null||expeditionGrowth is null)return new(501);
  if(!ExpeditionRequest.Routes.Contains(request.Route,StringComparer.Ordinal))return new(400);
  if(request.Route=="expedition/start"&&(!ExpeditionRequest.ValidCards(request.CardIds)||request.ExpeditionId<=0))return new(400);
  if(request.Route=="expedition/restart")
  {if(request.SlotNos.Length is <1 or >3||request.SlotNos.Any(x=>x is <1 or >3)||request.SlotNos.Distinct().Count()!=request.SlotNos.Length)return new(400);}
  else if(request.SlotNo is <1 or >3)return new(400);
  if(string.IsNullOrEmpty(request.AuthToken))return new(401);
  lock(gate)
  {
   using var c=Open();using var t=c.BeginTransaction(deferred:false);var grantContext = new EquipmentGrantContext(request.EquipmentOptions); var now=Now;
   if(!TryReadAuthenticatedAccount(c,t,request.AuthToken,now,out var account,out var player))return new(401);
   var hash=Hash(request.RetryToken);var old=FindEconomyReceipt(c,t,account,hash);
   if(old is not null)
   {
    if(old.Route!=request.Route||old.Semantic!=request.Semantic)return new(409);
    using var frozen=JsonDocument.Parse(old.Result);var replay=Snapshot(c,t,account,player,nowOverride:now);
    foreach(var a in frozen.RootElement.GetProperty("action").EnumerateObject())replay[a.Name]=a.Value.Clone();return new(200,replay);
   }
   if(!HasFinishedTutorial(player))return new(409);
   try
   {
    var result=MutateExpedition(c,t,account,player,request,hash,now,random);
    if(result.Player!=player)SavePlayer(c,t,account,result.Player);
    var receipt=JsonSerializer.Serialize(new{source="local-expedition",action=result.Action,audit=result.Audit});
    InsertEconomyReceipt(c,t,account,hash,request.Route,request.Semantic,ExpeditionCatalog.ExpectedFingerprint,receipt,JsonSerializer.Serialize(result.Cost),now);
    ObserveActivityDailyClear(c,t,account,result.Player,now);
    var action=Snapshot(c,t,account,result.Player,nowOverride:now);foreach(var a in result.Action)action[a.Key]=a.Value;t.Commit();return new(200,action);
   }
   catch(ExpeditionRejectedException){return new(409);}
   catch(GrowthRejectedException){return new(409);}
   catch(GachaCapacityException){return new(503);}
   catch(OverflowException){return new(503);}
  }
 }
 private ExpeditionMutation MutateExpedition(SqliteConnection c,SqliteTransaction t,long account,string player,ExpeditionRequest request,byte[] hash,long now,Func<int,int>? random)
 {
  switch(request.Route)
  {
   case "expedition/start":
    var id=DispatchExpedition(c,t,account,request.SlotNo,request.ExpeditionId,request.CardIds,hash,now,false);
    return new(player,[],new{dispatch_run_ids=new[]{id}},new{free_paid_coin=0,paid_coin=0});
   case "expedition/restart":
    var runs=new List<int>();
    foreach(var slot in request.SlotNos.Order())
    {
     var prior=ReadExpeditionRun(c,t,account,slot);if(prior is not{State:1})throw new ExpeditionRejectedException();
     runs.Add(DispatchExpedition(c,t,account,slot,prior.Destination,prior.Roster,hash,now,true));
    }
    return new(player,[],new{dispatch_run_ids=runs.ToArray()},new{free_paid_coin=0,paid_coin=0});
   case "expedition/skip":
    var skip=PendingExpedition(c,t,account,request.SlotNo);
    if(now>=skip.End)return new(player,[],new{run_id=skip.Id,already_due=true},new{free_paid_coin=0,paid_coin=0});
    var frozen=ReadFrozenExpedition(skip);var paid=DebitCurrency(player,frozen.Destination.SkipPaidCoin,out var freeSpent,out var paidSpent);if(paid is null)throw new ExpeditionRejectedException();
    using(var q=Command(c,t,"UPDATE expedition_runs SET end_at=$now,skip_at=$now,skip_paid_qty=$paid,skip_free_qty=$free,skip_retry_hash=$hash WHERE id=$run AND account_id=$account AND state=0 AND skip_at=0"))
    {
     q.Parameters.AddWithValue("$now",now);q.Parameters.AddWithValue("$paid",checked((long)paidSpent));q.Parameters.AddWithValue("$free",checked((long)freeSpent));q.Parameters.AddWithValue("$hash",hash);q.Parameters.AddWithValue("$run",skip.Id);q.Parameters.AddWithValue("$account",account);if(q.ExecuteNonQuery()!=1)throw new ExpeditionRejectedException();
    }
    return new(paid,[],new{run_id=skip.Id,already_due=false},new{free_paid_coin=freeSpent,paid_coin=paidSpent});
   case "expedition/stop":
    var stop=PendingExpedition(c,t,account,request.SlotNo);FinishExpedition(c,t,account,stop,2,"{}",hash,now);
    return new(player,[],new{stopped_run_id=stop.Id},new{free_paid_coin=0,paid_coin=0});
   case "expedition/result":
    var run=PendingExpedition(c,t,account,request.SlotNo);if(now<run.End)throw new ExpeditionRejectedException();
    var definition=ReadFrozenExpedition(run);var settlement=ExpeditionEngine.Draw(definition,random);var grants=settlement.Drops.Select(x=>new CommerceReward(x.ItemType,x.ItemId,x.ItemQty)).ToArray();
    var grantContext = new EquipmentGrantContext(request.EquipmentOptions); var applied=GrantActivityRewards(activityConfig!,c,t,account,player,now,grants, grantContext);var experience=new List<object>();
    foreach(var card in run.Roster.Where(x=>x>0))
    {
     int lv,exp,awakening;
     using(var q=Command(c,t,"SELECT lv,exp,awakening_count FROM owned_cards WHERE account_id=$account AND card_id=$card"))
     {q.Parameters.AddWithValue("$account",account);q.Parameters.AddWithValue("$card",card);using var r=q.ExecuteReader();if(!r.Read())throw new ExpeditionRejectedException();lv=r.GetInt32(0);exp=r.GetInt32(1);awakening=r.GetInt32(2);}
     var change=ExpeditionEngine.CardExperience(expeditionGrowth!,card,lv,exp,awakening,settlement.CardExp);
     using(var q=Command(c,t,"UPDATE owned_cards SET lv=$lv,exp=$exp WHERE account_id=$account AND card_id=$card"))
     {q.Parameters.AddWithValue("$lv",change.Level);q.Parameters.AddWithValue("$exp",change.Exp);q.Parameters.AddWithValue("$account",account);q.Parameters.AddWithValue("$card",card);if(q.ExecuteNonQuery()!=1)throw new ExpeditionRejectedException();}
     experience.Add(new{card_id=card,before_lv=lv,before_exp=exp,after_lv=change.Level,after_exp=change.Exp,requested_exp=settlement.CardExp,granted_exp=change.EffectiveXp,discarded_exp=settlement.CardExp-change.EffectiveXp,awakening_at_claim=awakening});
    }
    var extras=new Dictionary<string,object?>{{"success_type",settlement.SuccessType},{"drop_items",settlement.Drops.Select(x=>new{rarity=x.Rarity,item_type=x.ItemType,item_id=x.ItemId,item_qty=x.ItemQty}).ToArray()}};
    var result=JsonSerializer.Serialize(new{action=extras,typed_grants=grants,card_experience=experience});FinishExpedition(c,t,account,run,1,result,hash,now);
    return new(applied.Player,extras,new{completed_run_id=run.Id,typed_grants=grants,card_experience=experience},new{free_paid_coin=0,paid_coin=0});
   default:throw new ExpeditionRejectedException();
  }
 }
 private int DispatchExpedition(SqliteConnection c,SqliteTransaction t,long account,int slot,int destination,int[] roster,byte[] hash,long now,bool restarting)
 {
  var d=expeditionCatalog!.Destinations.SingleOrDefault(x=>x.ExpeditionId==destination&&!x.IsDeleted);var progress=ProfileMainQuest(c,t,account);
  var unlocked=expeditionCatalog.Unlocks.Where(x=>!x.IsDeleted&&(x.ConditionType==1||x.ConditionType==2&&progress>=x.ConditionValue)).Sum(x=>x.AddSlotCount);
  var previous=ReadExpeditionRun(c,t,account,slot);
  if(d is null||slot<1||slot>unlocked||d.ConditionQuestId>progress||!ExpeditionRequest.ValidCards(roster)||previous is{State:0}||restarting&&previous is not{State:1})throw new ExpeditionRejectedException();
  var owned=FormationCards(c,t,account);
  foreach(var card in roster.Where(x=>x>0))
  {
   if(!owned.ContainsKey(card))throw new ExpeditionRejectedException();_ =expeditionGrowth!.Card(card);
   using var q=Command(c,t,"SELECT EXISTS(SELECT 1 FROM expedition_allocations WHERE account_id=$account AND card_id=$card)");q.Parameters.AddWithValue("$account",account);q.Parameters.AddWithValue("$card",card);if((long)q.ExecuteScalar()!!=0)throw new ExpeditionRejectedException();
  }
  using(var q=Command(c,t,"SELECT MAX(value) FROM (SELECT COALESCE(MAX(id),0) value FROM expedition_runs UNION ALL SELECT COALESCE(MAX(seq),0) FROM sqlite_sequence WHERE name='expedition_runs')"))if((long)q.ExecuteScalar()!>=int.MaxValue)throw new GachaCapacityException();
  var frozen=expeditionCatalog.Freeze(destination,roster.Count(x=>x>0));var cards=JsonSerializer.Serialize(roster);
  using(var q=Command(c,t,"INSERT INTO expedition_slots(account_id,slot_no,last_expedition_id,last_card_ids_json,current_run_id) VALUES($account,$slot,$destination,$cards,NULL) ON CONFLICT(account_id,slot_no) DO UPDATE SET last_expedition_id=excluded.last_expedition_id,last_card_ids_json=excluded.last_card_ids_json"))
  {q.Parameters.AddWithValue("$account",account);q.Parameters.AddWithValue("$slot",slot);q.Parameters.AddWithValue("$destination",destination);q.Parameters.AddWithValue("$cards",cards);q.ExecuteNonQuery();}
  int id;using(var q=Command(c,t,"INSERT INTO expedition_runs(account_id,slot_no,expedition_id,config_hash,frozen_json,roster_json,started_at,scheduled_end_at,end_at,state,finished_at,result_json,skip_at,skip_paid_qty,skip_free_qty,dispatch_retry_hash) VALUES($account,$slot,$destination,$config,$frozen,$cards,$now,$end,$end,0,0,'{}',0,0,0,$hash) RETURNING id"))
  {
   q.Parameters.AddWithValue("$account",account);q.Parameters.AddWithValue("$slot",slot);q.Parameters.AddWithValue("$destination",destination);q.Parameters.AddWithValue("$config",ExpeditionCatalog.ExpectedFingerprint);q.Parameters.AddWithValue("$frozen",JsonSerializer.Serialize(frozen,ExpeditionCatalog.Json));q.Parameters.AddWithValue("$cards",cards);q.Parameters.AddWithValue("$now",now);q.Parameters.AddWithValue("$end",checked(now+d.Time));q.Parameters.AddWithValue("$hash",hash);id=checked((int)(long)q.ExecuteScalar()!);
  }
  using(var q=Command(c,t,"UPDATE expedition_slots SET current_run_id=$run WHERE account_id=$account AND slot_no=$slot"))
  {q.Parameters.AddWithValue("$run",id);q.Parameters.AddWithValue("$account",account);q.Parameters.AddWithValue("$slot",slot);if(q.ExecuteNonQuery()!=1)throw new ExpeditionRejectedException();}
  foreach(var card in roster.Where(x=>x>0))
  {using var q=Command(c,t,"INSERT INTO expedition_allocations(account_id,card_id,slot_no,run_id) VALUES($account,$card,$slot,$run)");q.Parameters.AddWithValue("$account",account);q.Parameters.AddWithValue("$card",card);q.Parameters.AddWithValue("$slot",slot);q.Parameters.AddWithValue("$run",id);q.ExecuteNonQuery();}
  return id;
 }
 private static ExpeditionRun? ReadExpeditionRun(SqliteConnection c,SqliteTransaction t,long account,int slot)
 {
  using var q=Command(c,t,"SELECT r.id,r.expedition_id,r.config_hash,r.frozen_json,r.roster_json,r.started_at,r.end_at,r.state,r.result_json FROM expedition_slots s JOIN expedition_runs r ON r.account_id=s.account_id AND r.slot_no=s.slot_no AND r.id=s.current_run_id WHERE s.account_id=$account AND s.slot_no=$slot");q.Parameters.AddWithValue("$account",account);q.Parameters.AddWithValue("$slot",slot);using var r=q.ExecuteReader();
  return r.Read()?new(r.GetInt32(0),slot,r.GetInt32(1),r.GetString(2),r.GetString(3),JsonSerializer.Deserialize<int[]>(r.GetString(4))!,r.GetInt64(5),r.GetInt64(6),r.GetInt32(7),r.GetString(8)):null;
 }
 private static ExpeditionRun PendingExpedition(SqliteConnection c,SqliteTransaction t,long account,int slot)=>ReadExpeditionRun(c,t,account,slot)is{State:0}run?run:throw new ExpeditionRejectedException();
 private static ExpeditionFrozen ReadFrozenExpedition(ExpeditionRun run)
 {
  if(run.Hash!=ExpeditionCatalog.ExpectedFingerprint)throw new ExpeditionRejectedException();return JsonSerializer.Deserialize<ExpeditionFrozen>(run.Frozen,ExpeditionCatalog.Json)??throw new InvalidDataException("Missing frozen expedition.");
 }
 private static void FinishExpedition(SqliteConnection c,SqliteTransaction t,long account,ExpeditionRun run,int state,string result,byte[] hash,long now)
 {
  using(var q=Command(c,t,"UPDATE expedition_runs SET state=$state,finished_at=$now,result_json=$result,finish_retry_hash=$hash WHERE id=$run AND account_id=$account AND state=0"))
  {q.Parameters.AddWithValue("$state",state);q.Parameters.AddWithValue("$now",now);q.Parameters.AddWithValue("$result",result);q.Parameters.AddWithValue("$hash",hash);q.Parameters.AddWithValue("$run",run.Id);q.Parameters.AddWithValue("$account",account);if(q.ExecuteNonQuery()!=1)throw new ExpeditionRejectedException();}
  using(var q=Command(c,t,"DELETE FROM expedition_allocations WHERE account_id=$account AND run_id=$run"))
  {q.Parameters.AddWithValue("$account",account);q.Parameters.AddWithValue("$run",run.Id);if(q.ExecuteNonQuery()!=run.Roster.Count(x=>x>0))throw new ExpeditionRejectedException();}
 }
 private int ExpeditionClaimedCount(SqliteConnection c,SqliteTransaction t,long account)
 {
  if(expeditionCatalog is null)return 0;using var q=Command(c,t,"SELECT COUNT(*) FROM expedition_runs WHERE account_id=$account AND state=1");q.Parameters.AddWithValue("$account",account);return checked((int)(long)q.ExecuteScalar()!);
 }
 private void ProjectExpedition(SqliteConnection c,SqliteTransaction t,long account,Dictionary<string,object?> action)
 {
  if(expeditionCatalog is null)return;var rows=new List<JsonElement>();
  for(var slot=1;slot<=3;slot++)
  {
   var r=ReadExpeditionRun(c,t,account,slot);if(r is null)continue;var cards=r.State==2?new int[3]:r.Roster;
   rows.Add(JsonSerializer.SerializeToElement(new{slot_no=slot,expedition_id=r.Destination,card_id_1=cards[0],card_id_2=cards[1],card_id_3=cards[2],end_date=r.State==0?DateTimeOffset.FromUnixTimeSeconds(r.End).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture):""}));
  }
  action["u_expedition"]=rows.ToArray();
 }
}
