using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;
public sealed partial class AccountStore
{
 private sealed record PresentEntry(int Category,int Id,CommerceReward Reward,PresentDefinition Definition,int Place,int Condition,int Over,int Expiry,long Created,int State,long Terminal,bool Vip)
 {
  internal int EffectiveState(long now)=>State==0&&Expiry>0&&now>=Expiry?2:State;
  internal long EffectiveTerminal(long now)=>State==0&&EffectiveState(now)==2?Expiry:Terminal;
 }
 private List<PresentEntry> PresentEntries(SqliteConnection c,SqliteTransaction t,long account,int category)
 {
  var rows=new List<PresentEntry>();var table=category==0?"present_items":"present_equips";var id=category==0?"user_present_item_id":"user_present_equip_id";
  using(var q=Command(c,t,"SELECT "+id+",frozen_reward_json,payload_hash,receive_place,condition_value,is_over_qty_limit,expire_timestamp,created_at,state,terminal_at FROM "+table+" WHERE account_id=$a"))
  {
   q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();while(r.Read())
   {
    var json=r.GetString(1);if(PresentCatalog.Hash(json)!=r.GetString(2))throw new InvalidDataException("Frozen present payload changed.");var f=JsonSerializer.Deserialize<PresentFrozen>(json,PresentCatalog.Json)??throw new InvalidDataException("Missing frozen present.");
    if(f.PolicyVersion!="local-general-present-v1"||f.Reward.ItemType!=f.Definition.ItemType||f.Reward.ItemId!=f.Definition.ItemId||(category==1)!=(f.Reward.ItemType==7))throw new InvalidDataException("Unknown frozen present policy.");
    rows.Add(new(category,r.GetInt32(0),f.Reward,f.Definition,r.GetInt32(3),r.GetInt32(4),r.GetInt32(5),r.GetInt32(6),r.GetInt64(7),r.GetInt32(8),r.GetInt64(9),false));
   }
  }
  if(category==0)
  {
   using var q=Command(c,t,"SELECT user_present_item_id,item_type,item_id,item_qty,receive_place,condition_value,is_over_qty_limit,expire_timestamp,created_at,received_at FROM vip_present_items WHERE account_id=$a");q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();while(r.Read())rows.Add(new(0,r.GetInt32(0),new(r.GetInt32(1),r.GetInt32(2),r.GetInt32(3)),presentCatalog!.Definition(r.GetInt32(1),r.GetInt32(2)),r.GetInt32(4),r.GetInt32(5),r.GetInt32(6),r.GetInt32(7),r.GetInt64(8),r.GetInt64(9)>0?1:0,r.GetInt64(9),true));
  }
  if(rows.Select(x=>x.Id).Distinct().Count()!=rows.Count)throw new InvalidDataException("Present IDs collide within one category.");return rows;
 }
 private static (int Item,int Equip) PresentPendingCounts(SqliteConnection c,SqliteTransaction t,long account,long now)
 {
  using var q=Command(c,t,"SELECT (SELECT COUNT(*)FROM vip_present_items WHERE account_id=$a AND received_at=0)+(SELECT COUNT(*)FROM present_items WHERE account_id=$a AND state=0 AND(expire_timestamp=0 OR expire_timestamp>$now)),(SELECT COUNT(*)FROM present_equips WHERE account_id=$a AND state=0 AND(expire_timestamp=0 OR expire_timestamp>$now))");q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$now",now);using var r=q.ExecuteReader();r.Read();return(checked((int)r.GetInt64(0)),checked((int)r.GetInt64(1)));
 }
 private AccountResult PresentResult(SqliteConnection c,SqliteTransaction t,long account,string player,long now,Dictionary<string,object?>? extras=null,object[]? rewards=null)
 {
  var action=Snapshot(c,t,account,player,nowOverride:now);if(extras is not null)foreach(var p in extras)action[p.Key]=p.Value;var badge=PresentPendingCounts(c,t,account,now);return new(200,action,rewards,PresentItemBadge:badge.Item,PresentEquipBadge:badge.Equip);
 }
 public AccountResult GeneralPresents(PresentRequest request)
 {
  if(presentCatalog is null)return new(501);if(string.IsNullOrEmpty(request.Auth))return new(401);
  // Legacy item IDs stay in their original handler, receipt semantic and JSON.
  // Classify with a read transaction that is CLOSED before its write transaction.
  if(request.Route==VipPresentReceiveRequest.Route)
  {
   bool onlyVip;
   using(var c=new SqliteConnection(new SqliteConnectionStringBuilder(connectionString){Mode=SqliteOpenMode.ReadOnly}.ToString()))
   {
    c.Open();using var t=c.BeginTransaction(deferred:true);if(!TryReadAuthenticatedAccount(c,t,request.Auth,Now,out var account,out _))return new(401);
    var vip=new HashSet<int>();using var q=Command(c,t,"SELECT user_present_item_id FROM vip_present_items WHERE account_id=$a");q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();while(r.Read())vip.Add(r.GetInt32(0));onlyVip=request.Ids.All(vip.Contains);
   }
   if(onlyVip)return ReceiveVipPresents(new(request.Retry,request.Auth,request.Ids));
  }
  lock(gate)
  {
   if(request.IsRead)
   {
    using var c=new SqliteConnection(new SqliteConnectionStringBuilder(connectionString){Mode=SqliteOpenMode.ReadOnly}.ToString());c.Open();using var t=c.BeginTransaction(deferred:true);var now=Now;
    if(!TryReadAuthenticatedAccount(c,t,request.Auth,now,out var account,out var player))return new(401);if(!HasFinishedTutorial(player))return new(409);
    var extras=new Dictionary<string,object?>();
    if(request.Route=="present/history")
    {
     var all=PresentEntries(c,t,account,0).Concat(PresentEntries(c,t,account,1)).Where(x=>x.EffectiveState(now)!=0&&x.EffectiveTerminal(now)>=now-7*86400L).OrderByDescending(x=>x.EffectiveTerminal(now)).ThenBy(x=>x.Category).ThenByDescending(x=>x.Id).Take(100);
     extras["histories"]=all.Select(x=>new{item_id=x.Reward.ItemId,item_type=x.Reward.ItemType,item_qty=x.Reward.ItemQty,receive_place=x.Place,condition_value=x.Condition,is_over_qty_limit=x.Over!=0,status=x.EffectiveState(now),timestamp=checked((int)x.EffectiveTerminal(now))}).ToArray();
    }
    else
    {
     var filtered=PresentEntries(c,t,account,request.Category).Where(x=>x.EffectiveState(now)==0&&(request.Expiration==0||request.Expiration==1&&x.Expiry>0||request.Expiration==2&&x.Expiry==0));
     if(request.Category==0)filtered=filtered.Where(x=>request.ItemTypes.Length==0||request.ItemTypes.Contains(x.Reward.ItemType));
     else filtered=filtered.Where(x=>(request.Rarities.Length==0||request.Rarities.Contains(x.Definition.Rarity))&&(request.EquipTypes.Length==0||request.EquipTypes.Contains(x.Definition.EquipType))&&(request.Skills.Length==0||request.Skills.Contains(x.Definition.EffectType)));
     var values=filtered.ToArray();(long,long,long,long) Key(PresentEntry x)=>request.Sort switch{1=>(x.Created,0,0,x.Id),2=>(x.Expiry,0,x.Created,x.Id),3=>(x.Definition.Rarity,0,x.Created,x.Id),_=>(checked((long)x.Definition.SaleGameCoin*x.Reward.ItemQty),x.Definition.Rarity,x.Created,x.Id)};
     IEnumerable<PresentEntry> ordered=request.Order==1?values.OrderByDescending(Key):values.OrderBy(Key);if(request.Sort==2)ordered=ordered.OrderBy(x=>x.Expiry==0?1:0);var page=ordered.Take(1000).Skip((request.Page-1)*50).Take(50);
     extras[request.Category==0?"u_present_item":"u_present_equip"]=page.Select(x=>request.Category==0?(object)new{user_present_item_id=x.Id,user_id=account,item_id=x.Reward.ItemId,item_type=x.Reward.ItemType,item_qty=x.Reward.ItemQty,receive_place=x.Place,condition_value=x.Condition,is_over_qty_limit=x.Over,expire_timestamp=x.Expiry}:new{user_present_equip_id=x.Id,user_id=account,equip_id=x.Reward.ItemId,equip_type=x.Definition.EquipType,rarity=x.Definition.Rarity,effect_type=x.Definition.EffectType,receive_place=x.Place,condition_value=x.Condition,is_over_qty_limit=x.Over,expire_timestamp=x.Expiry,qty=x.Reward.ItemQty}).ToArray();
     extras["unread_present_qty"]=values.Length;extras["has_warning_receive_present"]=values.Any(x=>x.Expiry>0&&x.Expiry-now<=86400);
    }
    return PresentResult(c,t,account,player,now,extras);
   }
   using(var c=Open())using(var t=c.BeginTransaction(deferred:false))
   {
    var grantContext = new EquipmentGrantContext(request.EquipmentOptions); var now=Now;if(!TryReadAuthenticatedAccount(c,t,request.Auth,now,out var account,out var player))return new(401);var hash=Hash(request.Retry);var old=FindEconomyReceipt(c,t,account,hash);
    if(old is not null)
    {
     if(old.Route!=request.Route||old.Semantic!=request.Semantic)return new(409);using var json=JsonDocument.Parse(old.Result);
     if(json.RootElement.GetProperty("source").GetString()=="vip-present-receive")return VipReceiveAction(c,t,account,player,old.Result,now);
     var action=json.RootElement.GetProperty("action").EnumerateObject().ToDictionary(x=>x.Name,x=>(object?)x.Value.Clone());var rewards=json.RootElement.GetProperty("notification_items").EnumerateArray().Select(x=>(object)x.Clone()).ToArray();return PresentResult(c,t,account,player,now,action,rewards);
    }
    if(!HasFinishedTutorial(player))return new(409);
    try
    {
     var entries=PresentEntries(c,t,account,request.Category).ToDictionary(x=>x.Id);if(request.Ids.Any(id=>!entries.ContainsKey(id)))throw new PresentRejectedException();var budget=new PresentBudget(this,c,t,account,player);var accepted=new List<PresentEntry>();var expired=new List<PresentEntry>();var failed=new List<int>();var credits=new List<CommerceReward>();var converted=new List<object>();var duplicates=new List<int>();var selling=request.Route=="present/equip/sell";
     foreach(var id in request.Ids.Order())
     {
      var row=entries[id];if(row.EffectiveState(now)!=0){failed.Add(id);if(!row.Vip&&row.State==0)expired.Add(row);continue;}
      // First version deliberately rejects a changed catalog definition rather
      // than substituting new properties/prices for a frozen pending gift.
      if(row.Definition!=presentCatalog.Definition(row.Reward.ItemType,row.Reward.ItemId))throw new InvalidDataException("Pending present definition differs from frozen original.");
      var next=budget.TryAccept(row.Reward,row.Definition,selling,out var grants,out var conversions,out var titleDuplicates);if(next is null){failed.Add(id);continue;}budget=next;accepted.Add(row);credits.AddRange(grants);converted.AddRange(conversions);duplicates.AddRange(titleDuplicates);
     }
     var updated=credits.Count==0?player:ApplyPresentCredits(c,t,account,player,now,budget,credits.ToArray(), grantContext);
     foreach(var row in accepted.Concat(expired))
     {
      var state=expired.Contains(row)?2:selling?3:1;var timestamp=state==2?row.Expiry:now;
      using var q=Command(c,t,row.Vip?"UPDATE vip_present_items SET received_at=$time WHERE account_id=$a AND user_present_item_id=$id AND received_at=0":"UPDATE "+(row.Category==0?"present_items":"present_equips")+" SET state=$state,terminal_at=$time,terminal_retry_hash=$hash WHERE account_id=$a AND "+(row.Category==0?"user_present_item_id":"user_present_equip_id")+"=$id AND state=0");
      if(!row.Vip){q.Parameters.AddWithValue("$state",state);q.Parameters.AddWithValue("$hash",hash);}q.Parameters.AddWithValue("$time",timestamp);q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$id",row.Id);if(q.ExecuteNonQuery()!=1)throw new InvalidDataException("Present terminal transition race.");
     }
     var failureKey=selling?"not_sell_user_present_equip_ids":request.Category==0?"not_receive_user_present_item_ids":"not_receive_user_present_equip_ids";var extras=new Dictionary<string,object?>{{failureKey,failed.ToArray()}};if(!selling)extras["convert_items"]=converted.ToArray();var notifications=credits.Select(x=>(object)new{item_type=x.ItemType,item_id=x.ItemId,item_qty=x.ItemQty}).ToArray();
     var frozen=JsonSerializer.Serialize(new{source="local-general-present-v1",action=extras,notification_items=notifications,audit=new{received_ids=accepted.Select(x=>x.Id).ToArray(),expired_ids=expired.Select(x=>x.Id).ToArray(),already_owned_unique_titles=duplicates,typed_grants=credits}});
     InsertEconomyReceipt(c,t,account,hash,request.Route,request.Semantic,PresentCatalog.ExpectedFingerprint,frozen,"{\"external_payment\":false,\"amount\":0}",now);ObserveActivityDailyClear(c,t,account,updated,now);var result=PresentResult(c,t,account,updated,now,extras,notifications);t.Commit();return result;
    }
    catch(PresentRejectedException){return new(409);}catch(GachaCapacityException){return new(503);}catch(OverflowException){return new(503);}catch(InvalidDataException){return new(503);}catch(SqliteException){return new(503);}
   }
  }
 }
}
