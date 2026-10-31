using System.Text.Json;
namespace Brs.Local.Accounts;

// The saved UUID and optional session token are credentials; no generated ToString.
public sealed class UserDeleteRequest
{
 public const string Route="user/delete";
 public string Uuid{get;} public string RetryToken{get;} public string? AuthToken{get;}
 private UserDeleteRequest(string uuid,string retry,string? auth){Uuid=uuid;RetryToken=retry;AuthToken=auth;}
 public static UserDeleteRequest Read(JsonElement root)
 {
  var common=EconomyRequestFields.Read(root,"uuid");var value=root.GetProperty("uuid");
  if(value.ValueKind!=JsonValueKind.String||value.GetString()is not{Length:36}uuid||!Guid.TryParseExact(uuid,"D",out _))throw new InvalidDataException("Invalid account deletion identity.");
  return new(uuid,EconomyRequestFields.Retry(common),EconomyRequestFields.Auth(common));
 }
}
