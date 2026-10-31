using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
 private sealed class TakeoverCredential
 {
  public long Account;public string Id="";public int Version;public byte[] Salt=[];public byte[] Verifier=[];
  public int Iterations;public long Revoked;public int Failures;public long Locked;public long Transferred;
 }
 private readonly Queue<long> takeoverAttempts=new();
 private static readonly byte[] TakeoverDummySalt=new byte[16];
 private static readonly byte[] TakeoverDummyHash=new byte[32];
 private static bool PasswordMatches(string password,TakeoverCredential? credential)
 {
  var derived=Rfc2898DeriveBytes.Pbkdf2(password,credential?.Salt??TakeoverDummySalt,600000,HashAlgorithmName.SHA256,32);
  try{return CryptographicOperations.FixedTimeEquals(derived,credential?.Verifier??TakeoverDummyHash)&&credential is not null;}
  finally{CryptographicOperations.ZeroMemory(derived);}
 }
 private static TakeoverCredential? ReadTakeoverCredential(SqliteConnection c,SqliteTransaction t,string where,object value)
 {
  using var q=Command(c,t,"SELECT account_id,takeover_id,credential_version,password_salt,password_hash,password_iterations,revoked_at,failed_attempts,locked_until,last_transferred_at FROM takeover_credentials WHERE "+where+"=$v");q.Parameters.AddWithValue("$v",value);
  using var r=q.ExecuteReader();return r.Read()?new(){Account=r.GetInt64(0),Id=r.GetString(1),Version=r.GetInt32(2),Salt=(byte[])r[3],Verifier=(byte[])r[4],Iterations=r.GetInt32(5),Revoked=r.GetInt64(6),Failures=r.GetInt32(7),Locked=r.GetInt64(8),Transferred=r.GetInt64(9)}:null;
 }
 private static bool WasTakenOver(SqliteConnection c,SqliteTransaction t,long account)
 {using var q=Command(c,t,"SELECT EXISTS(SELECT 1 FROM takeover_credentials WHERE account_id=$a AND last_transferred_at>0)");q.Parameters.AddWithValue("$a",account);return(long)q.ExecuteScalar()!!=0;}
 private static bool TakeoverBusy(SqliteConnection c,SqliteTransaction t,long account)
 {
  using var q=Command(c,t,"SELECT EXISTS(SELECT 1 FROM quest_runs WHERE account_id=$a AND state IN(0,1)) OR EXISTS(SELECT 1 FROM quest_loop_sessions WHERE account_id=$a AND status IN(0,1))");q.Parameters.AddWithValue("$a",account);return(long)q.ExecuteScalar()!!=0;
 }
 private static (string Uuid,string Player) TakeoverActor(SqliteConnection c,SqliteTransaction t,long account)
 {using var q=Command(c,t,"SELECT uuid,player_json FROM accounts WHERE account_id=$a");q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();if(!r.Read())throw new InvalidDataException("Missing takeover actor.");return(r.GetString(0),r.GetString(1));}
 private static JsonElement TakeoverUser(string player)
 {
  using var d=JsonDocument.Parse(player);var raw=d.RootElement;var projection=new Dictionary<string,JsonElement>();
  void Field(string name,int kind)
  {
   if(!raw.TryGetProperty(name,out var value)||(kind==0?!value.TryGetInt32(out _):kind==1?!value.TryGetUInt64(out _):value.ValueKind!=JsonValueKind.String))throw new InvalidDataException("Invalid takeover player projection.");
   projection[name]=value.Clone();
  }
  foreach(var name in new[]{"user_id","status","tutorial_step","birthday","rank","exp","stamina","stamina_max","main_quest_part_no","os_type","server_offset_time"})Field(name,0);
  foreach(var name in new[]{"my_game_id","game_coin","free_paid_coin","paid_coin","friend_pt"})Field(name,1);
  foreach(var name in new[]{"tutorial_ended_at","name","message","stamina_cure_started_at","vip_login_bonus_end_date","vip_loop_end_date","vip_daily_quest_end_date"})Field(name,2);
  return JsonSerializer.SerializeToElement(projection);
 }
 private static Dictionary<string,object?> TakeoverExecAction(long account,string uuid,string player)=>new()
 { ["uuid"]=uuid,["user_info"]=TakeoverUser(player),["user_version_info"]=new{user_id=checked((int)account),terms_version=1,policy_version=1} };
 private static AccountResult TakeoverWrongPassword()=>new(200,new(),ProtocolErrorId:9103,ProtocolErrorMessage:"IDまたはパスワードが間違っています");
 public AccountResult Takeover(TakeoverRequest request)
 {
  lock(gate)
  {
   var now=Now;while(takeoverAttempts.TryPeek(out var attempt)&&attempt<=now-60)takeoverAttempts.Dequeue();
   if(takeoverAttempts.Count>=120)return new(429);takeoverAttempts.Enqueue(now);
   using var c=Open();using var t=c.BeginTransaction(deferred:false);
   try
   {
    return request.Route switch
    {
     "user/takeover/set_password"=>SetTakeoverPassword(c,t,request,now),
     "user/takeover/pass_authentication"=>AuthenticateTakeover(c,t,request,now),
     "user/takeover/exec"=>ExecuteTakeover(c,t,request,now),
     _=>new(501)
    };
   }
   catch(OverflowException){return new(503);}
   catch(SqliteException){return new(503);}
  }
 }
 private AccountResult SetTakeoverPassword(SqliteConnection c,SqliteTransaction t,TakeoverRequest r,long now)
 {
  long account;using(var q=Command(c,t,"SELECT account_id FROM accounts WHERE uuid=$u")){q.Parameters.AddWithValue("$u",r.Uuid);if(q.ExecuteScalar()is not long n)return new(401);account=n;}
  if(IsManagementLeased(account))return new(409);
  // UUID is the original saved bearer used by title Login; a public UID is never accepted.
  if(!string.IsNullOrEmpty(r.AuthToken)&&TryReadAuthenticatedAccount(c,t,r.AuthToken,now,out var authAccount,out _)&&authAccount!=account)return new(409);
  var credential=ReadTakeoverCredential(c,t,"account_id",account);var retry=Hash(r.RetryToken);var old=FindEconomyReceipt(c,t,account,retry);
  if(old is not null)
  {
   if(old.Route!=r.Route||credential is null||credential.Revoked!=0)return new(409);
   using var receipt=JsonDocument.Parse(old.Result);
   if(!receipt.RootElement.TryGetProperty("source",out var source)||source.GetString()!="local-takeover-set-v1"||!receipt.RootElement.TryGetProperty("credential_version",out var version)||version.GetInt32()!=credential.Version||!PasswordMatches(r.Password,credential))return new(409);
   return new(200,new(){["takeover_id"]=credential.Id,["takeover_password"]=r.Password});
  }
  var changed=WriteTakeoverCredential(c,t,account,r.Password,now);var nextVersion=changed.Version;var id=changed.Id;
  var result=JsonSerializer.Serialize(new{source="local-takeover-set-v1",credential_version=nextVersion});
  InsertEconomyReceipt(c,t,account,retry,r.Route,"credential-version:"+nextVersion,TakeoverSchema.Fingerprint,result,"{}",now);
  t.Commit();return new(200,new(){["takeover_id"]=id,["takeover_password"]=r.Password});
 }
 // Shared PBKDF credential transaction helper; never commits or writes request secrets.
 private static TakeoverCredential WriteTakeoverCredential(SqliteConnection c,SqliteTransaction t,long account,string password,long now)
 {
  var credential=ReadTakeoverCredential(c,t,"account_id",account);
  var nextVersion=checked((credential?.Version??0)+1);var salt=RandomNumberGenerator.GetBytes(16);
  var passwordHash=Rfc2898DeriveBytes.Pbkdf2(password,salt,600000,HashAlgorithmName.SHA256,32);
  const string alphabet="0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
  string id;do{id=new string(Enumerable.Range(0,16).Select(_=>alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());using var collision=Command(c,t,"SELECT EXISTS(SELECT 1 FROM takeover_credentials WHERE takeover_id=$v)");collision.Parameters.AddWithValue("$v",id);if(Convert.ToInt64(collision.ExecuteScalar())==0)break;}while(true);
  using(var q=Command(c,t,"INSERT INTO takeover_credentials(account_id,takeover_id,credential_version,password_salt,password_hash,password_iterations,created_at,updated_at) VALUES($a,$id,$v,$salt,$hash,600000,$now,$now) ON CONFLICT(account_id)DO UPDATE SET takeover_id=excluded.takeover_id,credential_version=excluded.credential_version,password_salt=excluded.password_salt,password_hash=excluded.password_hash,updated_at=excluded.updated_at,revoked_at=0,failed_attempts=0,locked_until=0"))
  {q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$v",nextVersion);q.Parameters.AddWithValue("$salt",salt);q.Parameters.AddWithValue("$hash",passwordHash);q.Parameters.AddWithValue("$now",now);q.ExecuteNonQuery();}
  using(var q=Command(c,t,"UPDATE takeover_grants SET state=2 WHERE account_id=$a AND state=0")){q.Parameters.AddWithValue("$a",account);q.ExecuteNonQuery();}
  CryptographicOperations.ZeroMemory(passwordHash);
  return ReadTakeoverCredential(c,t,"account_id",account)!;
 }
 private AccountResult AuthenticateTakeover(SqliteConnection c,SqliteTransaction t,TakeoverRequest r,long now)
 {
  var credential=ReadTakeoverCredential(c,t,"takeover_id",r.Id);var match=PasswordMatches(r.Password,credential);
  if(credential is null)return TakeoverWrongPassword();
  if(IsManagementLeased(credential.Account))return new(409);
  if(credential.Revoked!=0||credential.Locked>now)return TakeoverWrongPassword();
  if(!match)
  {
   var failures=Math.Min((long)int.MaxValue,credential.Failures+1L);var locked=failures>=5?checked(now+60):0;
   using var q=Command(c,t,"UPDATE takeover_credentials SET failed_attempts=$f,locked_until=$l WHERE account_id=$a");q.Parameters.AddWithValue("$f",failures);q.Parameters.AddWithValue("$l",locked);q.Parameters.AddWithValue("$a",credential.Account);q.ExecuteNonQuery();t.Commit();return TakeoverWrongPassword();
  }
  using(var q=Command(c,t,"DELETE FROM takeover_grants WHERE account_id=$a AND state=0 AND expires_at<=$now")){q.Parameters.AddWithValue("$a",credential.Account);q.Parameters.AddWithValue("$now",now);q.ExecuteNonQuery();}
  using(var q=Command(c,t,"SELECT COUNT(*) FROM takeover_grants WHERE account_id=$a AND state=0")){q.Parameters.AddWithValue("$a",credential.Account);if((long)q.ExecuteScalar()!>=8)return new(503);}
  var token=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
  using(var q=Command(c,t,"INSERT INTO takeover_grants(token_hash,account_id,credential_version,created_at,expires_at,state) VALUES($h,$a,$v,$n,$e,0)"))
  {q.Parameters.AddWithValue("$h",Hash(token));q.Parameters.AddWithValue("$a",credential.Account);q.Parameters.AddWithValue("$v",credential.Version);q.Parameters.AddWithValue("$n",now);q.Parameters.AddWithValue("$e",checked(now+600));q.ExecuteNonQuery();}
  using(var q=Command(c,t,"UPDATE takeover_credentials SET failed_attempts=0,locked_until=0 WHERE account_id=$a")){q.Parameters.AddWithValue("$a",credential.Account);q.ExecuteNonQuery();}
  var actor=TakeoverActor(c,t,credential.Account);
  // Omit every ResponseActionBase inventory key. Preview must not mutate current cache.
  var action=new Dictionary<string,object?>{["takeover_token"]=token,["user_info"]=TakeoverUser(actor.Player)};
  t.Commit();return new(200,action);
 }
 private AccountResult ExecuteTakeover(SqliteConnection c,SqliteTransaction t,TakeoverRequest r,long now)
 {
  if(r.OsType!=2||r.TakeoverType!=3)return new(409);
  var hash=Hash(r.Token);long account,expires,replay;int version,state;byte[]? usedRetry,resultUuid;
  using(var q=Command(c,t,"SELECT account_id,credential_version,expires_at,state,replay_until,consumed_retry_hash,result_uuid_hash FROM takeover_grants WHERE token_hash=$h"))
  {q.Parameters.AddWithValue("$h",hash);using var rd=q.ExecuteReader();if(!rd.Read())return new(401);account=rd.GetInt64(0);version=rd.GetInt32(1);expires=rd.GetInt64(2);state=rd.GetInt32(3);replay=rd.GetInt64(4);usedRetry=rd.IsDBNull(5)?null:(byte[])rd[5];resultUuid=rd.IsDBNull(6)?null:(byte[])rd[6];}
  if(IsManagementLeased(account))return new(409);
  var credential=ReadTakeoverCredential(c,t,"account_id",account);if(credential is null||credential.Revoked!=0||credential.Version!=version)return new(401);
  var actor=TakeoverActor(c,t,account);var retry=Hash(r.RetryToken);
  if(state==1)
  {
   if(now>=replay||usedRetry is null||resultUuid is null||!CryptographicOperations.FixedTimeEquals(usedRetry,retry)||!CryptographicOperations.FixedTimeEquals(resultUuid,Hash(actor.Uuid)))return new(409);
   return new(200,TakeoverExecAction(account,actor.Uuid,actor.Player));
  }
  if(state!=0||now>=expires)return new(401);
  var source=0L;if(!string.IsNullOrEmpty(r.AuthToken)&&TryReadAuthenticatedAccount(c,t,r.AuthToken,now,out var sourceAccount,out _))source=sourceAccount;
  if(TakeoverBusy(c,t,account)||source>0&&source!=account&&TakeoverBusy(c,t,source))return new(409);
  var uuid=Guid.NewGuid().ToString("D");
  using(var q=Command(c,t,"UPDATE accounts SET uuid=$u WHERE account_id=$a")){q.Parameters.AddWithValue("$u",uuid);q.Parameters.AddWithValue("$a",account);if(q.ExecuteNonQuery()!=1)throw new InvalidDataException("Takeover actor changed.");}
  using(var q=Command(c,t,"DELETE FROM sessions WHERE account_id=$a OR token_hash=$h")){q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$h",string.IsNullOrEmpty(r.AuthToken)?Array.Empty<byte>():Hash(r.AuthToken));q.ExecuteNonQuery();}
  using(var q=Command(c,t,"UPDATE takeover_credentials SET last_transferred_at=$n WHERE account_id=$a")){q.Parameters.AddWithValue("$n",now);q.Parameters.AddWithValue("$a",account);q.ExecuteNonQuery();}
  using(var q=Command(c,t,"UPDATE takeover_grants SET state=2 WHERE account_id=$a AND state=0 AND token_hash<>$h")){q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$h",hash);q.ExecuteNonQuery();}
  using(var q=Command(c,t,"UPDATE takeover_grants SET state=1,consumed_at=$n,replay_until=$e,consumed_retry_hash=$r,result_uuid_hash=$u WHERE token_hash=$h AND state=0"))
  {q.Parameters.AddWithValue("$n",now);q.Parameters.AddWithValue("$e",checked(now+600));q.Parameters.AddWithValue("$r",retry);q.Parameters.AddWithValue("$u",Hash(uuid));q.Parameters.AddWithValue("$h",hash);if(q.ExecuteNonQuery()!=1)throw new InvalidDataException("Takeover grant changed.");}
  var action=TakeoverExecAction(account,uuid,actor.Player);t.Commit();return new(200,action);
 }
}
