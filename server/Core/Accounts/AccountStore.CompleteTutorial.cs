using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;
public sealed partial class AccountStore
{
 private CompleteTutorialCatalog? completeTutorial;
 private GrowthCatalog? completeTutorialGrowth;
 public void ConfigureCompleteTutorial(CompleteTutorialCatalog catalog,GrowthCatalog growth)
 {
  lock(gate){if(completeTutorial is not null||mainlineConfig is null||formationConfig is null||profileConfig is null)throw new InvalidDataException("Tutorial dependencies unavailable.");completeTutorial=catalog;completeTutorialGrowth=growth;}
 }
 private static bool HasLegacyTutorialRetry(SqliteConnection c,SqliteTransaction t,long account,byte[] hash)
 {using var q=Command(c,t,"SELECT EXISTS(SELECT 1 FROM tutorial_receipts WHERE account_id=$a AND retry_hash=$h)");q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$h",hash);return(long)q.ExecuteScalar()! !=0;}
 private static bool CompleteTutorialReceiptConfig(SqliteConnection c,SqliteTransaction t,long account,byte[] hash)
 {using var q=Command(c,t,"SELECT config_hash FROM economy_transactions WHERE account_id=$a AND retry_hash=$h");q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$h",hash);return q.ExecuteScalar() is string fp&&fp==CompleteTutorialCatalog.Fingerprint;}
 private void EnsureCompleteTutorialStarter(SqliteConnection c,SqliteTransaction t,long account,string player,long now, EquipmentGrantContext grantContext)
 {
  if(completeTutorial is null||HasFinishedTutorial(player))return;
  var hash=Hash("local-tutorial-starter:"+account);var old=FindEconomyReceipt(c,t,account,hash);
  if(old is not null){if(old.Route!="tutorial/initialize"||old.Semantic!="starter-v1"||!CompleteTutorialReceiptConfig(c,t,account,hash))throw new InvalidDataException("Tutorial starter receipt differs.");return;}
  var actual=new List<CommerceReward>();var cards=FormationCards(c,t,account);
  foreach(var card in completeTutorial.Cards)
   if(!cards.ContainsKey(card.CardId)){EnsureInstanceCapacity(c,t,"owned_cards",1);_ =GrantCard(c,t,account,card);actual.Add(new(5,card.CardId,1));}
  int knife;
  using(var q=Command(c,t,"SELECT user_equip_id FROM owned_equips WHERE account_id=$a AND equip_id=110100101 AND is_deleted=0 ORDER BY user_equip_id LIMIT 1"))
  {q.Parameters.AddWithValue("$a",account);knife=q.ExecuteScalar()is long n?checked((int)n):0;}
  if(knife==0)
  {EnsureEquipmentCapacity(c,t,account,[(completeTutorial.Equip,1)], grantContext);_ =GrantEquip(c,t,account,now,completeTutorial.Equip, grantContext);using var row=Command(c,t,"SELECT last_insert_rowid()");knife=checked((int)(long)row.ExecuteScalar()!);actual.Add(new(7,110100101,1));}
  bool deckExists;using(var q=Command(c,t,"SELECT EXISTS(SELECT 1 FROM user_decks WHERE account_id=$a AND deck_no=1)")){q.Parameters.AddWithValue("$a",account);deckExists=(long)q.ExecuteScalar()! !=0;}
  if(!deckExists)
  {EnsureDeckHeader(c,t,account,1);ReplaceDeckSlots(c,t,account,1,[new(0,[0,0,0]),new(30010003,[0,0,0]),new(30010004,[0,0,0]),new(0,[0,0,0]),new(0,[0,0,0]),new(0,[0,0,0])]);using var mark=Command(c,t,"UPDATE user_decks SET is_main=1 WHERE account_id=$a AND deck_no=1 AND NOT EXISTS(SELECT 1 FROM user_decks WHERE account_id=$a AND is_main=1)");mark.Parameters.AddWithValue("$a",account);mark.ExecuteNonQuery();}
  InsertEconomyReceipt(c,t,account,hash,"tutorial/initialize","starter-v1",CompleteTutorialCatalog.Fingerprint,JsonSerializer.Serialize(new{source="local-tutorial-starter-v1",actual_grants=actual,created_deck=!deckExists,phone_starter_unequipped=true,phone_starter_empty_first_slot=true,original_initial_server_payload_unknown=true}),"{}",now);
 }
 public AccountResult CompleteTutorial(CompleteTutorialRequest request,Func<int,int>? random=null)
 {
  if(completeTutorial is null||completeTutorialGrowth is null)return new(501);if(string.IsNullOrEmpty(request.Auth))return new(401);
  if(string.IsNullOrWhiteSpace(request.Retry)||request.Retry.Length>256)return new(400);
  lock(gate)
  {
   using var c=Open();using var t=c.BeginTransaction(deferred:false);var grantContext = new EquipmentGrantContext(request.EquipmentOptions); var now=Now;
   try
   {
    if(!TryReadAuthenticatedAccount(c,t,request.Auth,now,out var account,out var player))return new(401);
    var hash=Hash(request.Retry);var old=FindEconomyReceipt(c,t,account,hash);
    if(old is not null)
    {
     if(old.Route!=request.Route||old.Semantic!=request.Semantic||!CompleteTutorialReceiptConfig(c,t,account,hash))return new(409);
     using var d=JsonDocument.Parse(old.Result);var replay=Snapshot(c,t,account,player,nowOverride:now);foreach(var p in d.RootElement.GetProperty("action").EnumerateObject())replay[p.Name]=p.Value.Clone();return new(200,replay);
    }
    if(HasLegacyTutorialRetry(c,t,account,hash)||HasFinishedTutorial(player))return new(409);
    using var pd=JsonDocument.Parse(player);var step=TutorialProgression.ReadStep(pd.RootElement);
    var next=request.Route switch{"tutorial/name_set"=>step,"tutorial/quest_result" when step==100&&request.QuestId==90010101=>200,"tutorial/quest_result" when step==400&&request.QuestId==90010102=>500,"tutorial/card_enhance" when step==700=>800,"tutorial/equip_enhance" when step==900=>1000,"tutorial/deck_edit" when step==1400=>1500,_=>-1};
    if(next<0)return new(409);
    if(request.Route=="tutorial/name_set"&&!profileConfig!.ValidText(request.Name!,true))return new(400);
    EnsureCompleteTutorialStarter(c,t,account,player,now, grantContext);
    var wallet=JsonNode.Parse(player)!.AsObject();var extras=new Dictionary<string,object?>();var audit=new Dictionary<string,object?>();
    switch(request.Route)
    {
     case "tutorial/name_set":wallet["name"]=request.Name;audit["name_changed"]=request.Name!=pd.RootElement.GetProperty("name").GetString();break;
     case "tutorial/quest_result":
     {
      var quest=completeTutorial.Quest(request.QuestId);var first=request.QuestId==90010101;var gf=CompleteTutorialCatalog.I(quest,"ClearGameCoin");var xp=CompleteTutorialCatalog.I(quest,"ClearUserExp");
      var reward=new MainlineReward(first?12:7,first?10001:110100101,1,1);var cfg=completeTutorial.RewardConfig(mainlineConfig!);
      if(!first)EnsureEquipmentCapacity(c,t,account,[(completeTutorial.Equip,1)], grantContext);
      var treasure=new[]{ApplyMainlineReward(c,t,account,wallet,new(4,0,gf,0),now,cfg, grantContext),ApplyMainlineReward(c,t,account,wallet,reward,now,cfg, grantContext)};
      ApplyMainlineExperience(c,t,account,wallet,xp,0,[],now);SaveMainlineProgress(c,t,account,request.QuestId,6,now);
      extras=new(){["treasure"]=treasure,["is_convert"]=false,["add_user_exp"]=xp,["add_card_mastery_exp"]=0,["is_follow_max"]=false,["new_clear_mission_nos"]=Array.Empty<int>(),["is_restoration_stamina"]=false,["chara_voice_id"]=0,["get_drop_items"]=Array.Empty<object>()};
      audit["quest_id"]=request.QuestId;audit["typed_grants"]=new CommerceReward[]{new(4,0,gf),new(reward.Type,reward.Item,1)};audit["preset_type"]=9;break;
     }
     case "tutorial/card_enhance":
     {
      var req=request.Card!;if(req.CardId!=30010002||req.Items.Length!=1||req.Items[0]!=new GrowthQuantity(12,10001,1))return new(409);
      int level,exp,awakening;using(var q=Command(c,t,"SELECT lv,exp,awakening_count FROM owned_cards WHERE account_id=$a AND card_id=30010002")){q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();if(!r.Read())return new(409);level=r.GetInt32(0);exp=r.GetInt32(1);awakening=r.GetInt32(2);}
      if(CommerceQuantity(c,t,account,12,10001,0)<1)return new(409);
      var plan=GrowthEngine.EnhanceCard(completeTutorialGrowth,30010002,level,exp,awakening,req.Items,GrowthEngine.SuccessType(random?.Invoke(10000)??System.Security.Cryptography.RandomNumberGenerator.GetInt32(10000)));
      SpendGf(plan.GameCoin);Exec("UPDATE owned_enhances SET item_qty=item_qty-1 WHERE account_id=$a AND item_id=10001 AND item_qty>0");Exec("UPDATE owned_cards SET lv=$lv,exp=$exp WHERE account_id=$a AND card_id=30010002",("$lv",plan.Level),("$exp",plan.Exp));extras["success_type"]=plan.SuccessType;audit["card_id"]=30010002;audit["plan"]=plan;audit["gf_spent"]=plan.GameCoin;break;
     }
     case "tutorial/equip_enhance":
     {
      if(request.Donors is not{Length:1}||request.Donors[0]==request.Equip)return new(409);
      var target=ReadEquip(request.Equip);var donor=ReadEquip(request.Donors[0]);if(target.EquipId!=110100101||donor.EquipId!=110100101||donor.Locked) return new(409);
      var plan=GrowthEngine.EnhanceEquip(completeTutorialGrowth,target.EquipId,target.Level,target.Exp,target.OverLimit,[new(donor.EquipId,1,donor.OverLimit,donor.Id)],GrowthEngine.SuccessType(random?.Invoke(10000)??System.Security.Cryptography.RandomNumberGenerator.GetInt32(10000)));
      SpendGf(plan.GameCoin);DetachSupportEquipReferences(c,t,account,new[]{donor.Id});
      using(var q=Command(c,t,"DELETE FROM deck_slot_equips WHERE account_id=$a AND user_equip_id=$e")){q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$e",donor.Id);q.ExecuteNonQuery();}
      Exec("UPDATE owned_equips SET is_deleted=1 WHERE account_id=$a AND user_equip_id=$e AND is_deleted=0 AND is_lock=0",("$e",donor.Id));Exec("UPDATE owned_equips SET lv=$lv,exp=$exp,over_limit_count=$ol WHERE account_id=$a AND user_equip_id=$e AND is_deleted=0",("$lv",plan.Level),("$exp",plan.Exp),("$ol",plan.OverLimitCount),("$e",target.Id));extras["success_type"]=plan.SuccessType;audit["target"]=target;audit["donor"]=donor;audit["plan"]=plan;audit["gf_spent"]=plan.GameCoin;break;
     }
     case "tutorial/deck_edit":
     {
      var req=request.Deck!;if(req.Decks.Any(pair=>!FormationRules.ValidateDeck(formationConfig!,pair.Value,FormationCards(c,t,account),FormationEquips(c,t,account))))return new(409);
      foreach(var pair in req.Decks.OrderBy(p=>p.Key)){EnsureDeckHeader(c,t,account,pair.Key);ReplaceDeckSlots(c,t,account,pair.Key,pair.Value);}audit["edit_decks"]=req.Decks;audit["client_total_power_audit_only"]=req.TotalPower;break;
     }
     default:return new(400);
    }
    player=TutorialProgression.SetStep(wallet.ToJsonString(),next,now);SavePlayer(c,t,account,player);
    InsertEconomyReceipt(c,t,account,hash,request.Route,request.Semantic,CompleteTutorialCatalog.Fingerprint,JsonSerializer.Serialize(new{source="local-complete-tutorial-v1",step_before=step,step_after=next,action=extras,audit}),"{}",now);
    var action=Snapshot(c,t,account,player,nowOverride:now);foreach(var p in extras)action[p.Key]=p.Value;t.Commit();return new(200,action);
    void Exec(string sql,params(string Key,object Value)[] args){using var q=Command(c,t,sql);q.Parameters.AddWithValue("$a",account);foreach(var a in args)q.Parameters.AddWithValue(a.Key,a.Value);if(q.ExecuteNonQuery()!=1)throw new GrowthRejectedException();}
    void SpendGf(int cost){var before=wallet["game_coin"]!.GetValue<ulong>();if(cost<0||before<(ulong)cost)throw new GrowthRejectedException();wallet["game_coin"]=before-(ulong)cost;}
    GrowthEquipState ReadEquip(int id){using var q=Command(c,t,"SELECT equip_id,lv,exp,over_limit_count,is_lock,free_property_ids FROM owned_equips WHERE account_id=$a AND user_equip_id=$e AND is_deleted=0");q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$e",id);using var r=q.ExecuteReader();if(!r.Read())throw new GrowthRejectedException();return new(id,r.GetInt32(0),r.GetInt32(1),r.GetInt32(2),r.GetInt32(3),r.GetInt32(4)!=0,r.GetString(5));}
   }
   catch(GrowthRejectedException){return new(409);}
   catch(Exception ex)when(ex is GachaCapacityException or OverflowException or SqliteException){return new(503);}
  }
 }
}
