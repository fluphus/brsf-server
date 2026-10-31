using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;
public sealed partial class AccountStore
{
 private sealed record TaState(bool Entry,float Score,bool Awarded);
 private static Dictionary<int,TaState> ReadTaStates(SqliteConnection c,SqliteTransaction t,long account)
 {
  var states=new Dictionary<int,TaState>();
  using var q=Command(c,t,"SELECT route,result_json FROM economy_transactions WHERE account_id=$a AND config_hash=$h AND route IN('quest/ta_entry','quest/result') ORDER BY created_at,rowid");q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$h",QuestTaCatalog.Fingerprint);using var r=q.ExecuteReader();
  while(r.Read())
  {
   using var d=JsonDocument.Parse(r.GetString(1));if(d.RootElement.GetProperty("source").GetString()!="local-training-ta")throw new InvalidDataException("Invalid TA receipt.");var a=d.RootElement.GetProperty("audit");var id=a.GetProperty("quest_id").GetInt32();if(!QuestTaCatalog.Contains(id))throw new InvalidDataException("Unknown TA receipt stage.");
   if(r.GetString(0)==QuestRankingEntryRequest.Route)
   {var entry=a.GetProperty("is_entry").GetBoolean();if(states.TryGetValue(id,out var prior)&&prior.Entry!=entry)throw new InvalidDataException("Conflicting frozen TA decision.");states.TryAdd(id,new(entry,0,false));}
   else if(states.TryGetValue(id,out var old))
   {var score=a.GetProperty("ta_score").GetSingle();var awarded=a.GetProperty("ta_participation_award").GetBoolean();if(!float.IsFinite(score)||score<0||!old.Entry&&(score>0||awarded))throw new InvalidDataException("Invalid TA score.");states[id]=old with{Score=score>0&&(old.Score==0||score<old.Score)?score:old.Score,Awarded=old.Awarded||awarded};}
  }
  return states;
 }
 private static object? FreezeTaEntry(SqliteConnection c,SqliteTransaction t,long account,int id)=>QuestTaCatalog.Contains(id)?ReadTaStates(c,t,account).GetValueOrDefault(id)?.Entry:null;
 private AccountResult ChangeTrainingTaEntry(QuestRankingEntryRequest request)=>MutateMainline(request.AuthToken,request.RetryToken,QuestRankingEntryRequest.Route,request.Semantic,(c,t,a,p,n)=>
 {
  _=RequireMainlineQuest(c,t,a,request.QuestId,n);var old=ReadTaStates(c,t,a).GetValueOrDefault(request.QuestId);if(old is not null&&old.Entry!=request.IsEntry)throw new MainlineRejectedException();
  return new(p,[],new{quest_id=request.QuestId,is_entry=request.IsEntry},new{},QuestTaCatalog.Fingerprint);
 });
 private static (float Score,bool Awarded) ApplyTrainingTaResult(SqliteConnection c,SqliteTransaction t,long account,MainlineRun run,QuestResultRequest request,List<MainlineReward> drops)
 {
  if(!QuestTaCatalog.Contains(run.QuestId)||request.ClearRate==0)return(0,false);
  using var frozen=JsonDocument.Parse(run.Payload);var entry=frozen.RootElement.GetProperty("ta_entry");if(entry.ValueKind!=JsonValueKind.True)return(0,false);
  if(request.BattleTimeTa<=0||request.BattleTimeTa>600)throw new MainlineRejectedException();
  var state=ReadTaStates(c,t,account).GetValueOrDefault(run.QuestId);if(state is null||!state.Entry)throw new InvalidDataException("Missing frozen TA entry.");
  var award=!state.Awarded;if(award){var reward=QuestTaCatalog.Participation(run.QuestId);drops.Add(new(reward.ItemType,reward.ItemId,reward.ItemQty,0));}return(request.BattleTimeTa,award);
 }
 private static void ProjectTrainingTa(SqliteConnection c,SqliteTransaction t,long account,Dictionary<string,object?> action)=>action["u_quest_ta"]=ReadTaStates(c,t,account).OrderBy(x=>x.Key).Select(x=>new{quest_id=x.Key,is_entry=x.Value.Entry?1:0,clear_time=x.Value.Score}).ToArray();
}
