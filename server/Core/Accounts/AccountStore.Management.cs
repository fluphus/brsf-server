using System.Globalization;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;
public sealed partial class AccountStore
{
 private GrowthCatalog? managementGrowth;
 private IReadOnlyDictionary<string,string> managementLabels=new Dictionary<string,string>();
 private sealed record ManagementLease(string Id,string Owner,long Expires);
 private readonly Dictionary<long,ManagementLease> managementLeases=new();
 private sealed class ManagementRejected(int status,string code):Exception {public int Status{get;}=status;public string Code{get;}=code;}
 private static readonly JsonSerializerOptions ManagementJson=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
 private static ManagementRejected Reject(int status=400,string code="invalid_request")=>new(status,code);
 public void ConfigureManagement(GrowthCatalog growth,IReadOnlyDictionary<string,string>? labels=null)
 {
  lock(gate){if(managementGrowth is not null||growth.Fingerprint!=GrowthCatalog.ExpectedFingerprint)throw new InvalidDataException("Invalid management configuration.");managementGrowth=growth;managementLabels=labels is null?new Dictionary<string,string>():new Dictionary<string,string>(labels,StringComparer.Ordinal);}
 }
 private bool IsManagementLeased(long account)
 {
  if(!managementLeases.TryGetValue(account,out var lease))return false;
  if(lease.Expires>Now)return true;managementLeases.Remove(account);return false;
 }
 private void RequireManagementLease(long account,string owner,string lease)
 {
  if(!IsManagementLeased(account)||!managementLeases.TryGetValue(account,out var current)||current.Owner!=owner||current.Id!=lease)throw Reject(409,"maintenance_required");
 }
 private ManagementResult ManagementRead(Func<SqliteConnection,SqliteTransaction,object> read)
 {
  lock(gate){try{using var c=Open();using var t=c.BeginTransaction(deferred:false);return new(200,read(c,t));}catch(ManagementRejected e){return new(e.Status,Code:e.Code);}catch(Exception e)when(e is GrowthRejectedException or KeyNotFoundException or FormatException){return new(400,Code:"invalid_request");}catch(GachaCapacityException){return new(409,Code:"capacity_exceeded");}catch(Exception e)when(e is SqliteException or InvalidDataException or OverflowException or JsonException or InvalidOperationException){return new(503,Code:"storage_unavailable");}}
 }
 private static string ManagementUid(string player)=>JsonNode.Parse(player)!["my_game_id"]!.GetValue<ulong>().ToString(CultureInfo.InvariantCulture);
 private static (long Id,string Player,long Created) ManagementActor(SqliteConnection c,SqliteTransaction t,string uid)
 {
  if(!ulong.TryParse(uid,NumberStyles.None,CultureInfo.InvariantCulture,out var value)||value==0||value.ToString(CultureInfo.InvariantCulture)!=uid)throw Reject();
  using var q=Command(c,t,"SELECT account_id,player_json,created_at FROM accounts WHERE CAST(json_extract(player_json,'$.my_game_id') AS TEXT)=$u");q.Parameters.AddWithValue("$u",uid);using var r=q.ExecuteReader();if(!r.Read())throw Reject(404,"account_not_found");return(r.GetInt64(0),r.GetString(1),r.GetInt64(2));
 }
 private static long ManagementCount(SqliteConnection c,SqliteTransaction t,string sql,long account)
 {using var q=Command(c,t,sql);q.Parameters.AddWithValue("$a",account);return Convert.ToInt64(q.ExecuteScalar(),CultureInfo.InvariantCulture);}
 private static Dictionary<string,bool> ManagementBusy(SqliteConnection c,SqliteTransaction t,long account)=>new()
 {
  ["quest"]=ManagementCount(c,t,"SELECT EXISTS(SELECT 1 FROM quest_runs WHERE account_id=$a AND state IN(0,1))",account)!=0,
  ["loop"]=ManagementCount(c,t,"SELECT EXISTS(SELECT 1 FROM quest_loop_sessions WHERE account_id=$a AND status IN(0,1))",account)!=0,
  ["expedition"]=ManagementCount(c,t,"SELECT EXISTS(SELECT 1 FROM expedition_runs WHERE account_id=$a AND state=0)",account)!=0
 };
 private static JsonElement[] ManagementRows(SqliteConnection c,SqliteTransaction t,long account,string sql)
 {
  using var q=Command(c,t,sql);q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();var list=new List<JsonElement>();while(r.Read()){using var j=JsonDocument.Parse(r.GetString(0));list.Add(j.RootElement.Clone());}return list.ToArray();
 }
 private static object ManagementAccountHeader(SqliteConnection c,SqliteTransaction t,long id,string player,long created)
 {var p=JsonNode.Parse(player)!;return new{uid=ManagementUid(player),name=p["name"]!.GetValue<string>(),rank=p["rank"]!.GetValue<int>(),createdAt=created,cardCount=ManagementCount(c,t,"SELECT COUNT(*) FROM owned_cards WHERE account_id=$a",id),equipmentCount=ManagementCount(c,t,"SELECT COUNT(*) FROM owned_equips WHERE account_id=$a AND is_deleted=0",id)};}
 public ManagementResult ManagementAccounts()=>ManagementRead((c,t)=>
 {
  var actors=new List<(long,string,long)>();using(var q=Command(c,t,"SELECT account_id,player_json,created_at FROM accounts ORDER BY account_id"))using(var r=q.ExecuteReader())while(r.Read())actors.Add((r.GetInt64(0),r.GetString(1),r.GetInt64(2)));
  return new{items=actors.Select(a=>new{uid=ManagementUid(a.Item2),name=JsonNode.Parse(a.Item2)!["name"]!.GetValue<string>(),rank=JsonNode.Parse(a.Item2)!["rank"]!.GetValue<int>(),createdAt=a.Item3,cardCount=ManagementCount(c,t,"SELECT COUNT(*) FROM owned_cards WHERE account_id=$a",a.Item1),equipmentCount=ManagementCount(c,t,"SELECT COUNT(*) FROM owned_equips WHERE account_id=$a AND is_deleted=0",a.Item1),busy=ManagementBusy(c,t,a.Item1)}).ToArray()};
 });
 private static readonly string[] ManagementRevisionTables=["owned_cards","owned_card_skins","owned_equips","owned_equip_materials","owned_awakenings","owned_consumes","owned_enhances","owned_event_items","owned_backgrounds","owned_titles"];
 private static string Canonical(JsonElement e)=>e.ValueKind switch
 {
  JsonValueKind.Object=>"{"+string.Join(',',e.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal).Select(p=>JsonSerializer.Serialize(p.Name)+":"+Canonical(p.Value)))+"}",
  JsonValueKind.Array=>"["+string.Join(',',e.EnumerateArray().Select(Canonical))+"]",_=>e.GetRawText()
 };
 private static string ManagementRevision(SqliteConnection c,SqliteTransaction t,long account,string player)
 {
  var data=new List<object?>();using(var j=JsonDocument.Parse(player))data.Add(Canonical(j.RootElement));
  foreach(var table in ManagementRevisionTables){using var q=Command(c,t,"SELECT * FROM "+table+" WHERE account_id=$a ORDER BY "+(table=="owned_card_skins"?"card_id,card_skin_id":table=="owned_event_items"?"event_id,item_id":"1,2"));q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();while(r.Read()){data.Add(table);for(var i=0;i<r.FieldCount;i++)data.Add(r.IsDBNull(i)?null:r.GetValue(i));}}
  return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data)));
 }
 private object ManagementState(SqliteConnection c,SqliteTransaction t,long id,string player,long created)
 {
  var p=JsonNode.Parse(player)!.AsObject();var stamina=MainlineStamina.Read(p,Now);
  var items=new List<JsonElement>();foreach(var (type,table,quantity)in new[]{(6,"owned_consumes","item_qty"),(7,"owned_equip_materials","qty"),(11,"owned_awakenings","item_qty"),(12,"owned_enhances","item_qty"),(14,"owned_event_items","item_qty")})
   items.AddRange(ManagementRows(c,t,id,$"SELECT json_object('type',{type},'id',{(type==7?"equip_id":"item_id")},'qty',{quantity}"+(type==14?",'eventId',event_id":"")+$") FROM {table} WHERE account_id=$a ORDER BY {(type==14?"event_id,":"")}{(type==7?"equip_id":"item_id")}"));
  return new{account=ManagementAccountHeader(c,t,id,player,created),revision=ManagementRevision(c,t,id,player),resources=new{freeGems=p["free_paid_coin"]!.GetValue<ulong>(),paidGems=p["paid_coin"]!.GetValue<ulong>(),gf=p["game_coin"]!.GetValue<ulong>(),friendPoints=p["friend_pt"]!.GetValue<ulong>(),stamina=stamina.Current,staminaMax=stamina.Maximum},items,
   cards=ManagementRows(c,t,id,"SELECT json_object('cardId',card_id,'level',lv,'exp',exp,'awakening',awakening_count,'mainSkillLevel',main_skill_lv,'killAbilityLevel',kill_ability_lv,'killZoneSkillLevel',kill_zone_skill_lv,'masteryLevel',mastery_lv,'masteryExp',mastery_exp,'skinId',card_skin_id,'homeSkinId',home_skin_id,'profileSkinId',profile_skin_id) FROM owned_cards WHERE account_id=$a ORDER BY card_id"),
   equipment=ManagementRows(c,t,id,"SELECT json_object('id',CAST(user_equip_id AS TEXT),'equipId',equip_id,'level',lv,'exp',exp,'breakthrough',over_limit_count,'locked',json(CASE WHEN is_lock=1 THEN 'true' ELSE 'false' END),'properties',free_property_ids) FROM owned_equips WHERE account_id=$a AND is_deleted=0 ORDER BY user_equip_id"),
   collections=new{skins=ManagementRows(c,t,id,"SELECT json_object('cardId',card_id,'skinId',card_skin_id) FROM owned_card_skins WHERE account_id=$a ORDER BY card_id,card_skin_id"),backgroundIds=ManagementRows(c,t,id,"SELECT json_object('id',background_id) FROM owned_backgrounds WHERE account_id=$a ORDER BY background_id").Select(x=>x.GetProperty("id").GetInt32()).ToArray(),titleIds=ManagementRows(c,t,id,"SELECT json_object('id',title_id) FROM owned_titles WHERE account_id=$a ORDER BY title_id").Select(x=>x.GetProperty("id").GetInt32()).ToArray()},busy=ManagementBusy(c,t,id)};
 }
 public ManagementResult ManagementAccount(string uid)=>ManagementRead((c,t)=>{var a=ManagementActor(c,t,uid);return ManagementState(c,t,a.Id,a.Player,a.Created);});
 public ManagementResult ManagementBeginMaintenance(string uid,string ownerSession)=>ManagementRead((c,t)=>
 {
  if(string.IsNullOrEmpty(ownerSession)||ownerSession.Length>256)throw Reject();var a=ManagementActor(c,t,uid);
  foreach(var expired in managementLeases.Where(x=>x.Value.Expires<=Now).Select(x=>x.Key).ToArray())managementLeases.Remove(expired);
  if(IsManagementLeased(a.Id)){var old=managementLeases[a.Id];if(old.Owner!=ownerSession)throw Reject(409,"maintenance_owned");return new{leaseId=old.Id,expiresAt=old.Expires};}
  if(managementLeases.Count>=1024)throw Reject(503,"maintenance_capacity");
  if(ManagementBusy(c,t,a.Id).Values.Any(x=>x))throw Reject(409,"maintenance_busy");
  using(var q=Command(c,t,"DELETE FROM sessions WHERE account_id=$a")){q.Parameters.AddWithValue("$a",a.Id);q.ExecuteNonQuery();}t.Commit();
  var lease=new ManagementLease(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),ownerSession,checked(Now+120));managementLeases[a.Id]=lease;return new{leaseId=lease.Id,expiresAt=lease.Expires};
 });
 public ManagementResult ManagementRenewMaintenance(string uid,string ownerSession,string leaseId)=>ManagementRead((c,t)=>{var a=ManagementActor(c,t,uid);RequireManagementLease(a.Id,ownerSession,leaseId);var lease=managementLeases[a.Id] with{Expires=checked(Now+120)};managementLeases[a.Id]=lease;return new{leaseId=lease.Id,expiresAt=lease.Expires};});
 public ManagementResult ManagementEndMaintenance(string uid,string ownerSession,string leaseId)=>ManagementRead((c,t)=>{var a=ManagementActor(c,t,uid);RequireManagementLease(a.Id,ownerSession,leaseId);managementLeases.Remove(a.Id);return new{released=true};});
 public void ManagementReleaseOwner(string ownerSession){lock(gate){foreach(var id in managementLeases.Where(x=>x.Value.Owner==ownerSession).Select(x=>x.Key).ToArray())managementLeases.Remove(id);}}
 public ManagementResult ManagementExport(string uid)=>ManagementRead((c,t)=>{var a=ManagementActor(c,t,uid);var node=JsonSerializer.SerializeToNode(ManagementState(c,t,a.Id,a.Player,a.Created),ManagementJson)!.AsObject();node["formatVersion"]=1;node["exportedAt"]=Now;return node;});
 public ManagementResult ManagementBackup()
 {
  lock(gate){string? path=null;try{path=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"brsf-management-"+Guid.NewGuid().ToString("N")+".sqlite3");
   using(new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,4096,FileOptions.None)){}
   if(OperatingSystem.IsWindows())
   {
    var sid=WindowsIdentity.GetCurrent().User??throw new UnauthorizedAccessException();var acl=new FileSecurity();acl.SetOwner(sid);acl.SetAccessRuleProtection(true,false);acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));new FileInfo(path).SetAccessControl(acl);
   }
   else File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
   using var c=Open();using(var target=new SqliteConnection(ConnectionString(path,SqliteOpenMode.ReadWrite))){target.Open();c.BackupDatabase(target);}return new(200,new ManagementBackupFile(path));
  }catch(Exception e)when(e is SqliteException or IOException or UnauthorizedAccessException){if(path is not null)using(var file=new ManagementBackupFile(path)){}return new(503,Code:"storage_unavailable");}}
 }
}
