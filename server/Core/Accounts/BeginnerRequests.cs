using System.Text.Json;

namespace Brs.Local.Accounts;

public sealed record BeginnerTopRequest(string? AuthToken)
{
 public const string Route="campaign/beginner/top";
 public static BeginnerTopRequest Read(JsonElement root)=>new(MainlineFields.Auth(MainlineFields.Read(root)));
}
public sealed record BeginnerReceiveRequest(string RetryToken,string? AuthToken,int[] MissionIds)
{
 public const string Route="campaign/beginner/receive";
 public string Semantic=>FormationRequestFields.Semantic(Route,new{receive_mission_ids=MissionIds.Order().ToArray()});
 public static BeginnerReceiveRequest Read(JsonElement root)
 {
  var common=MainlineFields.Read(root,"receive_mission_ids");var ids=VipRequestFields.IntArray(root,"receive_mission_ids",50,nonempty:true);if(ids.Any(i=>i<=0))throw new InvalidDataException("Invalid beginner mission identity.");
  return new(MainlineFields.Retry(common),MainlineFields.Auth(common),ids);
 }
}
