using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
 private sealed record BeginnerState(BeginnerMissionRule Rule,int Score,bool IsReceived)
 {
  internal object Detail=>new{mission_id=Rule.MissionId,score=Score,is_received=IsReceived};
  internal JsonElement Wire=>JsonSerializer.SerializeToElement(new{mission_id=Rule.MissionId,score=Score,is_received=IsReceived?1:0});
 }
 private BeginnerState[] ReadBeginnerStates(SqliteConnection c,SqliteTransaction t,long account,string player,long now,ActivityReadSnapshot? shared=null)
 {
  var cfg=beginnerConfig??throw new InvalidDataException("Beginner catalog unavailable.");
  var activity=activityConfig??throw new InvalidDataException("Beginner mission sources unavailable.");
  var sources=shared??ActivityReadSnapshot.Load(activity,c,t,account,player,now,mainlineConfig,eventQuestConfig,questModesConfig);
  var claimed=new HashSet<int>();using(var q=Command(c,t,"SELECT entry_id FROM beginner_claims WHERE account_id=$a AND campaign_id=$c AND entry_kind=0"))
  {q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$c",cfg.CampaignId);using var r=q.ExecuteReader();while(r.Read())claimed.Add(r.GetInt32(0));}
  long mastery,skill,ka;using(var q=Command(c,t,"SELECT COALESCE(MAX(mastery_lv),0),COALESCE(MAX(MAX(main_skill_lv,kill_ability_lv,kill_zone_skill_lv)),0),COALESCE(MAX(kill_ability_lv),0) FROM owned_cards WHERE account_id=$a"))
  {q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();r.Read();mastery=r.GetInt64(0);skill=r.GetInt64(1);ka=r.GetInt64(2);}
  long homeChanged=0,titleChanged=0,equipsFromQuest=0;
  using(var q=Command(c,t,"SELECT e.route,e.config_hash,e.result_json,q.kind FROM economy_transactions e LEFT JOIN quest_runs q ON q.account_id=e.account_id AND q.id=json_extract(e.result_json,'$.audit.run_id') WHERE e.account_id=$a AND e.created_at<=$n AND (e.created_at>$epoch OR(e.created_at=$epoch AND NOT EXISTS(SELECT 1 FROM mission_epoch_excluded_receipts x WHERE x.account_id=e.account_id AND x.retry_hash=e.retry_hash))) AND e.route IN('home/customize','profile/title_set','quest/result')"))
  {
   q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$n",now);q.Parameters.AddWithValue("$epoch",sources.Epoch);using var r=q.ExecuteReader();
   while(r.Read())
   {
    var route=r.GetString(0);var fp=r.GetString(1);using var d=JsonDocument.Parse(r.GetString(2));var root=d.RootElement;if(!root.TryGetProperty("audit",out var audit))continue;
    if(route==HomeCustomizeRequest.Route&&fp==FormationConfig.ExpectedFingerprint&&Changed(audit))homeChanged++;
    else if(route==ProfileTitlesRequest.Route&&fp==ProfileConfig.ExpectedFingerprint&&Changed(audit))titleChanged++;
    else if(route==QuestResultRequest.Route&&!r.IsDBNull(3)&&r.GetInt32(3)==1&&fp is MainlineConfig.ExpectedFingerprint or EventQuestConfig.ExpectedFingerprint or QuestModesConfig.ExpectedFingerprint&&audit.TryGetProperty("outcome",out var outcome)&&outcome.GetString()=="clear")
    {
     // ActivityReadSnapshot has already checked the original config/frozen-run link.
     var grants=JsonSerializer.Deserialize<CommerceReward[]>(audit.GetProperty("typed_grants"),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException("Missing actual quest equipment source.");
     equipsFromQuest=checked(equipsFromQuest+grants.Where(g=>g.ItemType==7).Sum(g=>(long)g.ItemQty));
    }
   }
  }
  return cfg.Missions.OrderBy(m=>m.MissionId).Select(m=>
  {
   long score=m.ContentType switch
   {
    203=>m.ContentValue=="0"?skill:m.ContentValue=="1"?ka:0,
    210=>m.ContentValue=="0"?mastery:0,
    403=>homeChanged,404=>titleChanged,
    405=>ReadLocalBotFollowCount(c,t,account),
    111=>equipsFromQuest,
    402=>0,
    _=>sources.Score(activity,new ActivityMission(1,m.MissionId,0,m.ContentType,m.ContentValue,m.ClearScore,[],m.TakeoverMissionId,m.ItemGroupId,0,"",""),now)
   };
   if(score<0)throw new InvalidDataException("Negative beginner mission score.");return new BeginnerState(m,checked((int)Math.Min(int.MaxValue,score)),claimed.Contains(m.MissionId));
  }).ToArray();
  static bool Changed(JsonElement audit)=>audit.TryGetProperty("changed",out var value)&&value.ValueKind==JsonValueKind.True;
 }
 private static int BeginnerStep(BeginnerState[] states)=>Enumerable.Range(1,5).FirstOrDefault(step=>states.Any(s=>s.Rule.StepId==step&&!s.IsReceived),5);
 private void ProjectBeginner(SqliteConnection c,SqliteTransaction t,long account,string player,long now,Dictionary<string,object?> action,ActivityReadSnapshot? shared=null)
 {
  if(beginnerConfig is not null)action["u_beginner_mission"]=ReadBeginnerStates(c,t,account,player,now,shared).Select(s=>s.Wire).ToArray();
 }
 public AccountResult ProjectBeginnerBadge(AccountResult result)
 {
  if(beginnerConfig is null||result.Status!=200||result.Action is null||!result.Action.TryGetValue("u_user",out var users))return result;
  if(!result.Action.TryGetValue("u_beginner_mission",out var wire))return result;
  var rows=JsonSerializer.SerializeToElement(wire);if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()!=50)return result;
  var states=rows.EnumerateArray().Select(r=>new BeginnerState(beginnerConfig.Missions.Single(m=>m.MissionId==r.GetProperty("mission_id").GetInt32()),r.GetProperty("score").GetInt32(),r.GetProperty("is_received").GetInt32()==1)).ToArray();
  var step=BeginnerStep(states);return result with{BeginnerBadge=states.Count(s=>s.Rule.StepId==step&&!s.IsReceived&&s.Score>=s.Rule.ClearScore),BeginnerClearBadge=states.All(s=>s.IsReceived)};
 }

}
