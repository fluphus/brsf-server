using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;
public sealed partial class AccountStore
{
 private static void ManagementFields(JsonElement value,params string[] allowed)
 {
  if(value.ValueKind!=JsonValueKind.Object)throw Reject();var seen=new HashSet<string>(StringComparer.Ordinal);foreach(var p in value.EnumerateObject())if(!seen.Add(p.Name)||!allowed.Contains(p.Name,StringComparer.Ordinal))throw Reject();
 }
 private static JsonElement[] ManagementArray(JsonElement value,bool allowEmpty=false)
 {if(value.ValueKind!=JsonValueKind.Array||value.GetArrayLength()>200||!allowEmpty&&value.GetArrayLength()==0)throw Reject();return value.EnumerateArray().Select(x=>x.Clone()).ToArray();}
 private static int ManagementInt(JsonElement e,string field,int min,int max)
 {if(!e.TryGetProperty(field,out var v)||v.ValueKind!=JsonValueKind.Number||!v.TryGetInt32(out var i)||i<min||i>max)throw Reject();return i;}
 private static bool ManagementBool(JsonElement e,string field){if(!e.TryGetProperty(field,out var v)||v.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw Reject();return v.GetBoolean();}
 private static int[] ManagementIds(JsonElement e)
 {var a=ManagementArray(e).Select(v=>v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out var id)&&id>0?id:throw Reject()).ToArray();if(a.Distinct().Count()!=a.Length)throw Reject();return a;}
 private static int ManagementInstance(JsonElement e)
 {if(e.ValueKind!=JsonValueKind.String||!int.TryParse(e.GetString(),NumberStyles.None,CultureInfo.InvariantCulture,out var n)||n<=0||n.ToString(CultureInfo.InvariantCulture)!=e.GetString())throw Reject();return n;}
 private static string ManagementOperation(string operation)
 {if(!Guid.TryParseExact(operation,"D",out var id)||id==Guid.Empty||operation!=id.ToString("D"))throw Reject();return operation;}
 private static byte[] ManagementDigest(string kind,JsonElement payload)=>SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(kind+"\0"+Canonical(payload)));
 private static void ManagementExec(SqliteConnection c,SqliteTransaction t,long account,string sql,params (string,object)[] args)
 {using var q=Command(c,t,sql);q.Parameters.AddWithValue("$a",account);foreach(var (key,value)in args)q.Parameters.AddWithValue(key,value);q.ExecuteNonQuery();}
 private sealed record ManagementOld(long Account,string Kind,byte[] Hash,string Base,string Result);
 private static ManagementOld? ManagementReceipt(SqliteConnection c,SqliteTransaction t,string operation)
 {
  using var q=Command(c,t,"SELECT o.account_id,o.kind,o.request_hash,o.base_revision,o.result_json FROM management_receipts r LEFT JOIN management_operations o ON o.operation_id=r.operation_id WHERE r.operation_id=$o");q.Parameters.AddWithValue("$o",operation);using var r=q.ExecuteReader();if(!r.Read())return null;if(r.IsDBNull(0))throw Reject(410,"operation_gone");return new(r.GetInt64(0),r.GetString(1),(byte[])r[2],r.GetString(3),r.GetString(4));
 }
 private static void ManagementSaveReceipt(SqliteConnection c,SqliteTransaction t,string operation,long account,string kind,byte[] digest,string revision,object result,long now)
 {
  using var q=Command(c,t,"INSERT INTO management_receipts(operation_id,created_at)VALUES($o,$n);INSERT INTO management_operations(operation_id,account_id,kind,request_hash,base_revision,result_json,created_at)VALUES($o,$a,$k,$h,$r,$j,$n)");q.Parameters.AddWithValue("$o",operation);q.Parameters.AddWithValue("$n",now);q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$k",kind);q.Parameters.AddWithValue("$h",digest);q.Parameters.AddWithValue("$r",revision);q.Parameters.AddWithValue("$j",JsonSerializer.Serialize(result,ManagementJson));q.ExecuteNonQuery();
 }
 public ManagementResult ManagementPreview(string uid,string ownerSession,string leaseId,ManagementCommand request)=>ManagementCommandCore(uid,ownerSession,leaseId,request,true);
 public ManagementResult ManagementExecute(string uid,string ownerSession,string leaseId,ManagementCommand request)=>ManagementCommandCore(uid,ownerSession,leaseId,request,false);
 private ManagementResult ManagementCommandCore(string uid,string owner,string lease,ManagementCommand request,bool preview)=>ManagementRead((c,t)=>
 {
  _=RequireManagementGrowth();var operation=ManagementOperation(request.OperationId);if(request.Kind.Length is <1 or >64||request.ExpectedRevision.Length!=64||request.Payload.ValueKind!=JsonValueKind.Object)throw Reject();var payload=request.Payload.Clone();var digest=ManagementDigest(request.Kind,payload);
  var old=ManagementReceipt(c,t,operation);var a=ManagementActor(c,t,uid);
  if(old is not null){if(old.Account!=a.Id||old.Kind!=request.Kind||old.Base!=request.ExpectedRevision||!CryptographicOperations.FixedTimeEquals(old.Hash,digest))throw Reject(409,"operation_conflict");if(preview)throw Reject(409,"operation_conflict");var replay=JsonNode.Parse(old.Result)!.AsObject();replay["replayed"]=true;replay["applied"]=false;return replay;}
  RequireManagementLease(a.Id,owner,lease);var revision=ManagementRevision(c,t,a.Id,a.Player);if(request.ExpectedRevision!=revision)throw Reject(409,"stale_revision");
  var before=JsonSerializer.SerializeToElement(ManagementState(c,t,a.Id,a.Player,a.Created),ManagementJson);
  ApplyManagementCommand(c,t,a.Id,a.Player,request.Kind,payload,Now);
  var changed=ManagementActor(c,t,uid);var after=JsonSerializer.SerializeToElement(ManagementState(c,t,changed.Id,changed.Player,changed.Created),ManagementJson);var next=after.GetProperty("revision").GetString()!;
  var changes=new List<object>();foreach(var (field,label)in new[]{("resources","资源"),("items","物品"),("cards","兵装"),("equipment","装备"),("collections","收藏")})if(Canonical(before.GetProperty(field))!=Canonical(after.GetProperty(field)))changes.Add(new{label,before=before.GetProperty(field),after=after.GetProperty(field)});
  var summary=changes.Count==0?"没有变化":$"更新 {changes.Count} 类数据";
  if(preview)return new{operationId=operation,uid,revision,changes,summary};
  var result=new{operationId=operation,uid,applied=true,replayed=false,revision=next,summary};ManagementSaveReceipt(c,t,operation,a.Id,request.Kind,digest,revision,result,Now);t.Commit();return result;
 });
 private void ApplyManagementCommand(SqliteConnection c,SqliteTransaction t,long account,string player,string kind,JsonElement p,long now)
 {
  var g=RequireManagementGrowth();void Exec(string sql,params(string,object)[] args)=>ManagementExec(c,t,account,sql,args);
  JsonElement Card(int id){var row=g.Card(id);if(ManagementCount(c,t,"SELECT COUNT(*) FROM owned_cards WHERE account_id=$a AND card_id="+id,account)!=1)throw Reject(409,"ownership_required");return row;}
  JsonElement Equip(int id){using var q=Command(c,t,"SELECT equip_id FROM owned_equips WHERE account_id=$a AND user_equip_id=$id AND is_deleted=0");q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$id",id);if(q.ExecuteScalar()is not long eid)throw Reject(409,"ownership_required");var row=g.Equip(checked((int)eid));if(GrowthCatalog.I(row,"EquipType")>=301||inventoryMaterialConfig.Find((int)eid)is not null)throw Reject();return row;}
  void SetCard(JsonElement edit)
  {
   ManagementFields(edit,"cardId","level","awakening","mainSkillLevel","killAbilityLevel","killZoneSkillLevel","masteryLevel");var id=ManagementInt(edit,"cardId",1,int.MaxValue);var row=Card(id);var awaken=ManagementInt(edit,"awakening",0,5);var level=ManagementInt(edit,"level",1,g.CardCap(id,awaken));var caps=g.One("CardAwakeningMaster",r=>GrowthCatalog.I(r,"Rarity")==GrowthCatalog.I(row,"Rarity")&&GrowthCatalog.I(r,"AwakeningCount")==awaken);
   var main=ManagementInt(edit,"mainSkillLevel",1,GrowthCatalog.I(caps,"MaxMainSkillLv"));var kill=ManagementInt(edit,"killAbilityLevel",1,GrowthCatalog.I(caps,"MaxKillAbilityLv"));var zone=ManagementInt(edit,"killZoneSkillLevel",1,GrowthCatalog.I(caps,"MaxKillZoneSkillLv"));var mastery=ManagementInt(edit,"masteryLevel",1,mainlineConfig?.MasteryLevels.Max(x=>x.MasteryLv)??throw Reject(503,"catalog_unavailable"));
   var exp=g.Threshold("CardLvMaster",GrowthCatalog.I(row,"LvGroupId"),level);var mx=mainlineConfig!.MasteryLevels.Single(x=>x.MasteryLv==mastery).MasteryExp;
   // Preserve the historical initial mastery1/0 if a different growth field alone changes.
   if(mastery==1){using var q=Command(c,t,"SELECT mastery_lv,mastery_exp FROM owned_cards WHERE account_id=$a AND card_id=$id");q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$id",id);using var r=q.ExecuteReader();r.Read();if(r.GetInt32(0)==1&&r.GetInt32(1)==0)mx=0;}
   Exec("UPDATE owned_cards SET lv=$l,exp=$e,awakening_count=$w,main_skill_lv=$m,kill_ability_lv=$k,kill_zone_skill_lv=$z,mastery_lv=$ml,mastery_exp=$me WHERE account_id=$a AND card_id=$id",("$l",level),("$e",exp),("$w",awaken),("$m",main),("$k",kill),("$z",zone),("$ml",mastery),("$me",mx),("$id",id));
   // State-qualified entitlements only: no historical rewards, missions or play claims.
   var skin=GrowthCatalog.I(row,"AwakeningCardSkinId");if(awaken>0&&skin>0&&g.Rows("CardAwakeningMaster").Any(r=>GrowthCatalog.I(r,"Rarity")==GrowthCatalog.I(row,"Rarity")&&GrowthCatalog.I(r,"AwakeningCount")<=awaken&&r.GetProperty("IsAwakeningSkin").GetBoolean()))Exec("INSERT OR IGNORE INTO owned_card_skins(account_id,card_id,card_skin_id)VALUES($a,$id,$skin)",("$id",id),("$skin",skin));
   if(miscCatalog is not null)SaveVoiceEntitlements(c,t,account,AwakeningVoiceCandidates(GrowthCatalog.I(row,"CharaId"),id,0,awaken),now);
  }
  void SetEquip(JsonElement edit)
  {
   ManagementFields(edit,"id","level","breakthrough","locked");var id=ManagementInstance(edit.GetProperty("id"));var row=Equip(id);var b=ManagementInt(edit,"breakthrough",0,4);var level=ManagementInt(edit,"level",1,g.EquipCap(GrowthCatalog.I(row,"Rarity"),b));var locked=ManagementBool(edit,"locked");var xp=g.Threshold("EquipLvMaster",GrowthCatalog.I(row,"LvGroupId"),level);
   Exec("UPDATE owned_equips SET lv=$l,exp=$e,over_limit_count=$b,is_lock=$k WHERE account_id=$a AND user_equip_id=$id AND is_deleted=0",("$l",level),("$e",xp),("$b",b),("$k",locked?1:0),("$id",id));
  }
  switch(kind)
  {
   case "wallet.set":
   {
    ManagementFields(p,"freeGems","paidGems","gf","friendPoints","stamina");if(!p.EnumerateObject().Any())throw Reject();var wallet=JsonNode.Parse(player)!.AsObject();foreach(var (field,key,max)in new[]{("freeGems","free_paid_coin",99999999),("paidGems","paid_coin",99999999),("gf","game_coin",999999999),("friendPoints","friend_pt",99999999)})if(p.TryGetProperty(field,out _))wallet[key]=checked((ulong)ManagementInt(p,field,0,max));
    if(wallet["free_paid_coin"]!.GetValue<ulong>()+wallet["paid_coin"]!.GetValue<ulong>()>MaximumLocalGemBalance)throw Reject();if(p.TryGetProperty("stamina",out _)){var value=ManagementInt(p,"stamina",0,999);new MainlineStamina(value,wallet["stamina_max"]!.GetValue<int>(),now).Save(wallet);}SavePlayer(c,t,account,wallet.ToJsonString());break;
   }
   case "items.set":
   {
    ManagementFields(p,"items");var seen=new HashSet<(int,int,int)>();foreach(var item in ManagementArray(p.GetProperty("items"))){ManagementFields(item,"type","id","qty","eventId");var type=ManagementInt(item,"type",1,14);var id=ManagementInt(item,"id",1,int.MaxValue);var qty=ManagementInt(item,"qty",0,99999999);var owner=0;
     if(type==14){owner=eventQuestConfig?.EventItems.SingleOrDefault(x=>x.ItemId==id)?.EventId??throw Reject();if(ManagementInt(item,"eventId",1,int.MaxValue)!=owner)throw Reject();}else if(item.TryGetProperty("eventId",out _))throw Reject();
     if(!seen.Add((type,id,owner)))throw Reject();if(type==7){var material=inventoryMaterialConfig.Find(id)??throw Reject();if(qty>0)Exec("INSERT OR IGNORE INTO acquired_equip_groups(account_id,equip_group_id)VALUES($a,$g)",("$g",material.EquipGroupId));
      using var update=Command(c,t,"UPDATE owned_equip_materials SET qty=$q WHERE account_id=$a AND equip_id=$id");update.Parameters.AddWithValue("$a",account);update.Parameters.AddWithValue("$id",id);update.Parameters.AddWithValue("$q",qty);if(update.ExecuteNonQuery()==0){EnsureInstanceCapacity(c,t,"owned_equip_materials",1);Exec("INSERT INTO owned_equip_materials(account_id,equip_id,qty)VALUES($a,$id,$q)",("$id",id),("$q",qty));}}
     else if(type is 6 or 11 or 12){_ =presentCatalog?.Definition(type,id)??throw Reject(503,"catalog_unavailable");var table=CommerceQuantityTable(type);Exec($"INSERT INTO {table}(account_id,item_id,item_qty)VALUES($a,$id,$q)ON CONFLICT(account_id,item_id)DO UPDATE SET item_qty=excluded.item_qty",("$id",id),("$q",qty));}
     else if(type==14)Exec("INSERT INTO owned_event_items(account_id,event_id,item_id,item_qty)VALUES($a,$event,$id,$q)ON CONFLICT(account_id,event_id,item_id)DO UPDATE SET item_qty=excluded.item_qty",("$event",owner),("$id",id),("$q",qty));else throw Reject();
    }break;
   }
   case "cards.ensure":
    ManagementFields(p,"cardIds");foreach(var id in ManagementIds(p.GetProperty("cardIds"))){var card=presentCatalog?.Definition(5,id).Card??throw Reject(503,"catalog_unavailable");if(ManagementCount(c,t,"SELECT COUNT(*) FROM owned_cards WHERE account_id=$a AND card_id="+id,account)==0){EnsureInstanceCapacity(c,t,"owned_cards",1);GrantCard(c,t,account,card);}}break;
   case "cards.growth":
    ManagementFields(p,"cards");var edits=ManagementArray(p.GetProperty("cards"));if(edits.Select(e=>ManagementInt(e,"cardId",1,int.MaxValue)).Distinct().Count()!=edits.Length)throw Reject();foreach(var edit in edits)SetCard(edit);break;
   case "cards.max":
   {
    ManagementFields(p,"cardIds");var ids=p.GetProperty("cardIds").ValueKind==JsonValueKind.Null?ManagementRows(c,t,account,"SELECT json_object('id',card_id)FROM owned_cards WHERE account_id=$a ORDER BY card_id").Select(x=>x.GetProperty("id").GetInt32()).ToArray():ManagementIds(p.GetProperty("cardIds"));foreach(var id in ids){var row=Card(id);var caps=g.One("CardAwakeningMaster",r=>GrowthCatalog.I(r,"Rarity")==GrowthCatalog.I(row,"Rarity")&&GrowthCatalog.I(r,"AwakeningCount")==5);SetCard(JsonSerializer.SerializeToElement(new{cardId=id,level=g.CardCap(id,5),awakening=5,mainSkillLevel=GrowthCatalog.I(caps,"MaxMainSkillLv"),killAbilityLevel=GrowthCatalog.I(caps,"MaxKillAbilityLv"),killZoneSkillLevel=GrowthCatalog.I(caps,"MaxKillZoneSkillLv"),masteryLevel=mainlineConfig!.MasteryLevels.Max(x=>x.MasteryLv)}));}break;
   }
   case "equipment.grant":
   {
    ManagementFields(p,"equipId","quantity","locked");var id=ManagementInt(p,"equipId",1,int.MaxValue);var qty=ManagementInt(p,"quantity",1,200);var locked=ManagementBool(p,"locked");var row=g.Equip(id);if(GrowthCatalog.I(row,"EquipType")>=301||inventoryMaterialConfig.Find(id)is not null)throw Reject();var definition=presentCatalog?.Definition(7,id).Equip??throw Reject(503,"catalog_unavailable");if(ActiveOrdinaryEquipmentCount(c,t,account)+qty>999)throw Reject(409,"capacity_exceeded");EnsureInstanceCapacity(c,t,"owned_equips",qty);
    for(var i=0;i<qty;i++){GrantEquip(c,t,account,now,definition,new EquipmentGrantContext(default));if(locked)Exec("UPDATE owned_equips SET is_lock=1 WHERE account_id=$a AND user_equip_id=last_insert_rowid()");}break;
   }
   case "equipment.growth":
    ManagementFields(p,"equipment");var equips=ManagementArray(p.GetProperty("equipment"));if(equips.Select(e=>ManagementInstance(e.GetProperty("id"))).Distinct().Count()!=equips.Length)throw Reject();foreach(var edit in equips)SetEquip(edit);break;
   case "equipment.max":
   case "equipment.lock":
   {
    ManagementFields(p,kind=="equipment.max"?["equipmentIds"]:["equipmentIds","locked"]);var array=p.GetProperty("equipmentIds");var ids=array.ValueKind==JsonValueKind.Null&&kind=="equipment.max"?ManagementRows(c,t,account,"SELECT json_object('id',CAST(user_equip_id AS TEXT))FROM owned_equips WHERE account_id=$a AND is_deleted=0 ORDER BY user_equip_id").Select(x=>ManagementInstance(x.GetProperty("id"))).ToArray():ManagementArray(array).Select(ManagementInstance).ToArray();if(ids.Distinct().Count()!=ids.Length)throw Reject();
    foreach(var id in ids){var row=Equip(id);if(kind=="equipment.lock")Exec("UPDATE owned_equips SET is_lock=$k WHERE account_id=$a AND user_equip_id=$id AND is_deleted=0",("$k",ManagementBool(p,"locked")?1:0),("$id",id));else{var locked=ManagementCount(c,t,"SELECT is_lock FROM owned_equips WHERE account_id=$a AND user_equip_id="+id,account)!=0;SetEquip(JsonSerializer.SerializeToElement(new{id=id.ToString(CultureInfo.InvariantCulture),level=g.EquipCap(GrowthCatalog.I(row,"Rarity"),4),breakthrough=4,locked}));}}break;
   }
   case "collection.grant":
   {
    ManagementFields(p,"skins","backgroundIds","titleIds");if(!p.EnumerateObject().Any())throw Reject();if(p.TryGetProperty("skins",out var skins)){var seen=new HashSet<(int,int)>();foreach(var edit in ManagementArray(skins,true)){ManagementFields(edit,"cardId","skinId");var id=ManagementInt(edit,"cardId",1,int.MaxValue);var skin=ManagementInt(edit,"skinId",1,int.MaxValue);Card(id);if(!seen.Add((id,skin))||cardSkinConfig?.CanUseSkin(id,skin,true)!=true)throw Reject();Exec("INSERT OR IGNORE INTO owned_card_skins(account_id,card_id,card_skin_id)VALUES($a,$id,$s)",("$id",id),("$s",skin));}}
    if(p.TryGetProperty("backgroundIds",out var backgrounds))foreach(var id in ManagementArray(backgrounds,true).Length==0?[]:ManagementIds(backgrounds)){if(HomeBackgroundCatalog.Find(id)is not{IsDeleted:false})throw Reject();Exec("INSERT OR IGNORE INTO owned_backgrounds(account_id,background_id)VALUES($a,$id)",("$id",id));}
    if(p.TryGetProperty("titleIds",out var titles))foreach(var id in ManagementArray(titles,true).Length==0?[]:ManagementIds(titles)){if(activityConfig?.TitleIds.Contains(id)!=true)throw Reject();if(ManagementCount(c,t,"SELECT COUNT(*)FROM owned_titles WHERE account_id=$a AND title_id="+id,account)==0)Exec("INSERT INTO owned_titles(account_id,title_id)VALUES($a,$id)",("$id",id));}break;
   }
   default:throw Reject();
  }
 }
}
