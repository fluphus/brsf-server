using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
 private BeginnerCatalog? beginnerConfig;
 public void ConfigureBeginner(BeginnerCatalog config)
 {
  lock(gate){if(beginnerConfig is not null||presentCatalog is null||activityConfig is null||mainlineConfig is null||eventQuestConfig is null||questModesConfig is null)throw new InvalidDataException("Beginner requires complete proven account sources.");beginnerConfig=config;}
 }
 public AccountResult BeginnerTop(BeginnerTopRequest request)
 {
  if(beginnerConfig is null)return new(501);if(string.IsNullOrEmpty(request.AuthToken))return new(401);
  lock(gate)
  {
   using var c=new SqliteConnection(new SqliteConnectionStringBuilder(connectionString){Mode=SqliteOpenMode.ReadOnly}.ToString());c.Open();using var t=c.BeginTransaction(deferred:true);var now=Now;
   if(!TryReadAuthenticatedAccount(c,t,request.AuthToken,now,out var account,out var player))return new(401);if(!HasFinishedTutorial(player))return new(409);
   var action=Snapshot(c,t,account,player,nowOverride:now);var states=((JsonElement[])action["u_beginner_mission"]!).Select(r=>new BeginnerState(beginnerConfig.Missions.Single(m=>m.MissionId==r.GetProperty("mission_id").GetInt32()),r.GetProperty("score").GetInt32(),r.GetProperty("is_received").GetInt32()==1)).ToArray();var step=BeginnerStep(states);
   action["user_beginner_mission_details"]=states.Where(s=>s.Rule.StepId==step).Select(s=>s.Detail).ToArray();action["step_id"]=step;return new(200,action);
  }
 }
 public AccountResult ReceiveBeginner(BeginnerReceiveRequest request)
 {
  if(beginnerConfig is null)return new(501);if(string.IsNullOrEmpty(request.AuthToken))return new(401);
  if(string.IsNullOrWhiteSpace(request.RetryToken)||request.RetryToken.Length>256||request.MissionIds.Length is <1 or >50||request.MissionIds.Any(i=>i<=0)||request.MissionIds.Distinct().Count()!=request.MissionIds.Length)return new(400);
  lock(gate)
  {
   using var c=Open();using var t=c.BeginTransaction(deferred:false);var now=Now;
   try
   {
    if(!TryReadAuthenticatedAccount(c,t,request.AuthToken,now,out var account,out var player))return new(401);if(!HasFinishedTutorial(player))return new(409);
    var retry=Hash(request.RetryToken);var old=FindEconomyReceipt(c,t,account,retry);
    if(old is not null)
    {
     if(old.Route!=BeginnerReceiveRequest.Route||old.Semantic!=request.Semantic)return new(409);using var frozen=JsonDocument.Parse(old.Result);var replay=Snapshot(c,t,account,player,nowOverride:now);
     replay["is_complete_step"]=frozen.RootElement.GetProperty("is_complete_step").GetBoolean();var items=frozen.RootElement.GetProperty("notification_items").EnumerateArray().Select(x=>(object)x.Clone()).ToArray();return new(200,replay,items,IsSendPresentBox:frozen.RootElement.GetProperty("is_send_present_box").GetBoolean());
    }
    var states=ReadBeginnerStates(c,t,account,player,now);var step=BeginnerStep(states);var selected=request.MissionIds.Order().Select(id=>states.SingleOrDefault(s=>s.Rule.MissionId==id)).ToArray();
    if(selected.Any(s=>s is null))return new(400);if(selected.Any(s=>s!.Rule.StepId!=step||s.IsReceived||s.Score<s.Rule.ClearScore))return new(409);
    var notifications=new List<object>();var identities=new List<object>();
    foreach(var state in selected)
    {
     var s=state!;Claim(0,s.Rule.MissionId,s.Score);
     var queued=EnqueueGeneralPresents(c,t,account,now,$"beginner:{beginnerConfig.CampaignId}:mission:{s.Rule.MissionId}",retry,BeginnerCatalog.ExpectedFingerprint,2600,0,beginnerConfig.RewardsFor(s.Rule.ItemGroupId));
     notifications.AddRange(queued.NotificationItems);identities.AddRange(queued.Presents);
    }
    var selectedIds=request.MissionIds.ToHashSet();var complete=states.Where(s=>s.Rule.StepId==step).All(s=>s.IsReceived||selectedIds.Contains(s.Rule.MissionId));
    if(complete)
    {
     Claim(1,step,0);var group=beginnerConfig.Steps.Single(s=>s.StepId==step).ItemGroupId;
     var queued=EnqueueGeneralPresents(c,t,account,now,$"beginner:{beginnerConfig.CampaignId}:step:{step}",retry,BeginnerCatalog.ExpectedFingerprint,2600,0,beginnerConfig.RewardsFor(group));notifications.AddRange(queued.NotificationItems);identities.AddRange(queued.Presents);
    }
    var result=JsonSerializer.Serialize(new{source="local-beginner-campaign",campaign_id=beginnerConfig.CampaignId,mission_ids=request.MissionIds.Order().ToArray(),step_id=step,is_complete_step=complete,is_send_present_box=true,notification_items=notifications,presents=identities});
    InsertEconomyReceipt(c,t,account,retry,BeginnerReceiveRequest.Route,request.Semantic,BeginnerCatalog.ExpectedFingerprint,result,"{}",now);
    var action=Snapshot(c,t,account,player,nowOverride:now);action["is_complete_step"]=complete;t.Commit();return new(200,action,notifications.ToArray(),IsSendPresentBox:true);
    void Claim(int kind,int id,int score)
    {
     using var q=Command(c,t,"INSERT INTO beginner_claims(account_id,campaign_id,entry_kind,entry_id,score_at_claim,claimed_at,receipt_hash)VALUES($a,$c,$k,$i,$s,$n,$h)");
     q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$c",beginnerConfig.CampaignId);q.Parameters.AddWithValue("$k",kind);q.Parameters.AddWithValue("$i",id);q.Parameters.AddWithValue("$s",score);q.Parameters.AddWithValue("$n",now);q.Parameters.AddWithValue("$h",retry);q.ExecuteNonQuery();
    }
   }
   catch(Exception ex)when(ex is GachaCapacityException or OverflowException or InvalidDataException or SqliteException){return new(503);}
  }
 }
}
