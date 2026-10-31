using System.Text.Json;
namespace Brs.Local.Accounts;

public sealed record LocalBotRequest(string Route,string? AuthToken,string RetryToken,int TargetId,ulong GameId)
{
 public static readonly string[] Routes=["friend/follow_list","friend/search","friend/follow","friend/un_follow","friend/cancel_follower","friend/del_follow_badge"];
 public static LocalBotRequest Read(string route,JsonElement root)
 {
  if(!Routes.Contains(route,StringComparer.Ordinal))throw new InvalidDataException("Unknown Bot route.");
  var field=route switch{"friend/search"=>"my_game_id","friend/follow" or "friend/un_follow"=>"follow_user_id","friend/cancel_follower"=>"follower_user_id",_=>null};
  var common=field is null?MainlineFields.Read(root):MainlineFields.Read(root,field);
  var read=route is "friend/follow_list" or "friend/search";
  var target=field is "follow_user_id" or "follower_user_id"?MainlineFields.Int(root,field):0;
  ulong game=0;
  if(route=="friend/search"&&(root.GetProperty("my_game_id").ValueKind!=JsonValueKind.Number||!root.GetProperty("my_game_id").TryGetUInt64(out game)||game==0))throw new InvalidDataException("Invalid public Bot game ID.");
  if(field is "follow_user_id" or "follower_user_id"&&target<=0)throw new InvalidDataException("Invalid Bot target.");
  return new(route,MainlineFields.Auth(common),read?"":MainlineFields.Retry(common),target,game);
 }
 public string Semantic=>MainlineFields.Semantic(Route,new{TargetId=LocalBotCatalog.Known(TargetId)?LocalBotCatalog.Canonical(TargetId):TargetId});
}
