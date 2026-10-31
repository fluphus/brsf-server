using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;
public sealed partial class AccountStore
{
 private static void ManagementPassword(string password){if(password.Length is <8 or >12||!password.All(char.IsAsciiLetterOrDigit))throw Reject();}
 private static object ManagementCredentialResponse(JsonObject safe,string password,TakeoverCredential credential,bool replayed)
 {
  safe.Remove("credentialVersion");safe.Remove("credentialTransferred");safe["applied"]=!replayed;safe["replayed"]=replayed;safe["credentials"]=new JsonObject{["takeoverId"]=credential.Id,["password"]=password};return safe;
 }
 private static TakeoverCredential ManagementReplayCredential(SqliteConnection c,SqliteTransaction t,long account,JsonObject safe,string password)
 {
  var credential=ReadTakeoverCredential(c,t,"account_id",account);
  if(credential is null||credential.Revoked!=0||safe["credentialVersion"]?.GetValue<int>()!=credential.Version||safe["credentialTransferred"]?.GetValue<long>()!=credential.Transferred||!PasswordMatches(password,credential))throw Reject(409,"operation_conflict");return credential;
 }
 public ManagementResult ManagementCreate(string operationId,string name,string password)=>ManagementRead((c,t)=>
 {
  _=RequireManagementGrowth();var operation=ManagementOperation(operationId);ManagementPassword(password);if(profileConfig?.ValidText(name,true)!=true||string.IsNullOrWhiteSpace(name))throw Reject();
  var digest=ManagementDigest("account.create",JsonSerializer.SerializeToElement(new{name}));var old=ManagementReceipt(c,t,operation);
  if(old is not null){if(old.Kind!="account.create"||!CryptographicOperations.FixedTimeEquals(old.Hash,digest))throw Reject(409,"operation_conflict");var safe=JsonNode.Parse(old.Result)!.AsObject();var credential=ManagementReplayCredential(c,t,old.Account,safe,password);return ManagementCredentialResponse(safe,password,credential,true);}
  var created=CreateAccountInTransaction(c,t,Hash("local-management-create-v1\0"+operation),digest,Now,consumeNonce:false)??throw Reject(503,"storage_unavailable");
  var wallet=JsonNode.Parse(created.Player)!.AsObject();wallet["name"]=name;var player=wallet.ToJsonString();SavePlayer(c,t,created.Account,player);var changed=WriteTakeoverCredential(c,t,created.Account,password,Now);var uid=ManagementUid(player);var revision=ManagementRevision(c,t,created.Account,player);
  var result=JsonSerializer.SerializeToNode(new{operationId=operation,uid,applied=true,replayed=false,revision,summary="已创建账号与 IDPASS",account=new{uid,name,rank=1},credentialVersion=changed.Version,credentialTransferred=changed.Transferred},ManagementJson)!.AsObject();
  ManagementSaveReceipt(c,t,operation,created.Account,"account.create",digest,"",result,Now);t.Commit();return ManagementCredentialResponse(result,password,changed,false);
 });
 public ManagementResult ManagementCredentials(string uid,string ownerSession,string leaseId,string operationId,string expectedRevision,string password)=>ManagementRead((c,t)=>
 {
  var operation=ManagementOperation(operationId);ManagementPassword(password);if(expectedRevision.Length!=64)throw Reject();var digest=ManagementDigest("credentials.reset",JsonSerializer.SerializeToElement(new{}));var old=ManagementReceipt(c,t,operation);var a=ManagementActor(c,t,uid);
  if(old is not null){if(old.Account!=a.Id||old.Kind!="credentials.reset"||old.Base!=expectedRevision||!CryptographicOperations.FixedTimeEquals(old.Hash,digest))throw Reject(409,"operation_conflict");var safe=JsonNode.Parse(old.Result)!.AsObject();var credential=ManagementReplayCredential(c,t,a.Id,safe,password);return ManagementCredentialResponse(safe,password,credential,true);}
  RequireManagementLease(a.Id,ownerSession,leaseId);var revision=ManagementRevision(c,t,a.Id,a.Player);if(revision!=expectedRevision)throw Reject(409,"stale_revision");var changed=WriteTakeoverCredential(c,t,a.Id,password,Now);
  var result=JsonSerializer.SerializeToNode(new{operationId=operation,uid,applied=true,replayed=false,revision,summary="已重置 IDPASS",credentialVersion=changed.Version,credentialTransferred=changed.Transferred},ManagementJson)!.AsObject();ManagementSaveReceipt(c,t,operation,a.Id,"credentials.reset",digest,revision,result,Now);t.Commit();return ManagementCredentialResponse(result,password,changed,false);
 });
}
