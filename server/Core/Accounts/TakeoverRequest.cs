using System.Text.Json;
using System.Text.RegularExpressions;
namespace Brs.Local.Accounts;

// Deliberately not a record: generated ToString must not expose credentials.
public sealed class TakeoverRequest
{
 public static readonly string[] Routes=["user/takeover/set_password","user/takeover/pass_authentication","user/takeover/exec"];
 public string Route{get;} public string RetryToken{get;} public string? AuthToken{get;}
 public string Uuid{get;} public string Password{get;} public string Id{get;} public string Token{get;}
 public int OsType{get;} public int TakeoverType{get;}
 private TakeoverRequest(string route,string retry,string?auth,string uuid,string password,string id,string token,int os,int type)
 {Route=route;RetryToken=retry;AuthToken=auth;Uuid=uuid;Password=password;Id=id;Token=token;OsType=os;TakeoverType=type;}
 public static TakeoverRequest Read(string route,JsonElement root)
 {
  var fields=route switch{ "user/takeover/set_password"=>new[]{"uuid","takeover_password"},"user/takeover/pass_authentication"=>["takeover_id","takeover_password"],"user/takeover/exec"=>["os_type","takeover_type","takeover_token"],_=>throw new InvalidDataException("Unsupported takeover route.")};
  var common=EconomyRequestFields.Read(root,fields);var retry=EconomyRequestFields.Retry(common);var auth=EconomyRequestFields.Auth(common);
  string S(string key,int max){var value=root.GetProperty(key);if(value.ValueKind!=JsonValueKind.String||value.GetString()is not{}s||s.Length==0||s.Length>max)throw new InvalidDataException("Invalid takeover field.");return s;}
  var uuid="";var pass="";var id="";var token="";var os=0;var type=0;
  if(route.EndsWith("set_password",StringComparison.Ordinal)){uuid=S("uuid",36);if(!Guid.TryParseExact(uuid,"D",out _))throw new InvalidDataException("Invalid takeover identity.");}
  if(route!="user/takeover/exec")
  {pass=S("takeover_password",12);if(pass.Length<8||!pass.All(char.IsAsciiLetterOrDigit))throw new InvalidDataException("Invalid takeover password format.");}
  if(route.EndsWith("pass_authentication",StringComparison.Ordinal)){id=S("takeover_id",128);if(!id.All(char.IsAsciiLetterOrDigit))throw new InvalidDataException("Invalid takeover identifier.");}
  if(route.EndsWith("exec",StringComparison.Ordinal))
  {token=S("takeover_token",43);if(!Regex.IsMatch(token,"\\A[A-Za-z0-9_-]{43}\\z",RegexOptions.CultureInvariant))throw new InvalidDataException("Invalid takeover grant.");os=VipRequestFields.Int(root,"os_type");type=VipRequestFields.Int(root,"takeover_type");}
  return new(route,retry,auth,uuid,pass,id,token,os,type);
 }
}
