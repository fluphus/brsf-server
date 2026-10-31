using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;
public sealed record QuestLoopResultRequest(string RetryToken,string? AuthToken)
{
 public const string Route="quest/loop_result";
 public string Semantic=>MainlineFields.Semantic(Route,new{});
 public static QuestLoopResultRequest Read(JsonElement root){var c=MainlineFields.Read(root);return new(MainlineFields.Retry(c),MainlineFields.Auth(c));}
}
public sealed partial class AccountStore
{
 private const string LoopFingerprint="c6459938989868cfaaf5f36285f8b7cfd883f32d314e9510ab9df0fc518bd82a";
 private GrowthCatalog? loopGrowth;
 public void ConfigureQuestLoops(GrowthCatalog growth){lock(gate){if(loopGrowth is not null)throw new InvalidOperationException("Loop catalog configured twice.");loopGrowth=growth;}}
 private sealed record LoopState(int Id,int Quest,int Max,int Next,int Status,int? Active,int? Last,string Config,JsonElement Frozen,JsonElement Summary);
 private static LoopState? ReadLoop(SqliteConnection c,SqliteTransaction t,long account,bool unfinished=true)
 {
  using var q=Command(c,t,"SELECT id,quest_id,max_loop_count,next_loop_count,status,active_run_id,last_run_id,config_hash,frozen_json,summary_json FROM quest_loop_sessions WHERE account_id=$a"+(unfinished?" AND status IN(0,1)":"")+" ORDER BY id DESC LIMIT 1");q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();if(!r.Read())return null;
  using var f=JsonDocument.Parse(r.GetString(8));using var s=JsonDocument.Parse(r.GetString(9));return new(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2),r.GetInt32(3),r.GetInt32(4),r.IsDBNull(5)?null:r.GetInt32(5),r.IsDBNull(6)?null:r.GetInt32(6),r.GetString(7),f.RootElement.Clone(),s.RootElement.Clone());
 }
 private static void ValidateLoopRunOwner(SqliteConnection c,SqliteTransaction t,long a,LoopState s,int run)
 {using var q=Command(c,t,"SELECT EXISTS(SELECT 1 FROM quest_runs WHERE id=$r AND account_id=$a AND quest_id=$q AND config_hash=$h)");q.Parameters.AddWithValue("$r",run);q.Parameters.AddWithValue("$a",a);q.Parameters.AddWithValue("$q",s.Quest);q.Parameters.AddWithValue("$h",s.Config);if((long)q.ExecuteScalar()! !=1)throw new InvalidDataException("Cross-account/quest loop run reference.");}
 private void CheckLoopContinuation(SqliteConnection c,SqliteTransaction t,long a,QuestStartRequest r,LoopState s)
 {
  if(s.Quest!=r.QuestId||s.Max!=r.SetLoopCount||s.Next!=r.LoopCount||s.Status!=0||s.Active is not null||s.Config!=QuestHash(r.QuestId)||s.Frozen.GetProperty("deck_no").GetInt32()!=r.DeckNo||s.Frozen.GetProperty("support_user_id").GetInt32()!=r.SupportUserId||s.Frozen.GetProperty("coin").GetBoolean()!=r.IsCureStaminaUseCoin)throw new MainlineRejectedException();
  if(s.Last is not{}last)throw new MainlineRejectedException();ValidateLoopRunOwner(c,t,a,s,last);
  using var q=Command(c,t,"SELECT state FROM quest_runs WHERE id=$r");q.Parameters.AddWithValue("$r",last);if((long)q.ExecuteScalar()! !=2)throw new MainlineRejectedException();
 }
 private LoopState? CheckLoopStart(SqliteConnection c,SqliteTransaction t,long a,QuestStartRequest r,MainlineQuest quest,JsonElement deck,long now)
 {
  var old=ReadLoop(c,t,a);if(r.SetLoopCount==0)
  {if(old is not null){if(old.Active is not null)throw new MainlineRejectedException();CloseLoop(c,t,a,old,now,true);}return null;}
  if(loopGrowth is null)throw new MainlineRejectedException();
  var missions=QuestData(quest.QuestId).Missions.Where(m=>m.QuestId==quest.QuestId).ToArray();if(missions.Length==0||!missions.All(m=>HasMainlineMission(c,t,a,quest.QuestId,m.MissionNo)))throw new MainlineRejectedException();
  if(ActiveMainlineRun(c,t,a)is not null)throw new MainlineRejectedException();
  if(RealQuestType(quest.QuestId)==4&&!QuestTaCatalog.Contains(quest.QuestId)){var daily=questModesConfig!.DailySchedules.Single(d=>d.QuestTypeId==quest.QuestTypeId);if(daily.ExecMaxCount>0&&r.SetLoopCount-r.LoopCount+1>daily.ExecMaxCount-DailyChallengeCount(c,t,a,quest.QuestTypeId,now))throw new MainlineRejectedException();}
  if(r.LoopCount==1)
  {if(old is not null){if(old.Active is not null)throw new MainlineRejectedException();CloseLoop(c,t,a,old,now,true);}return null;}
  if(old is null)throw new MainlineRejectedException();CheckLoopContinuation(c,t,a,r,old);
  if(old.Frozen.GetProperty("deck").GetRawText()!=deck.GetRawText())throw new MainlineRejectedException();return old;
 }
 private static void CloseLoop(SqliteConnection c,SqliteTransaction t,long a,LoopState s,long now,bool finalize)
 {
  if(s.Active is not null)throw new MainlineRejectedException();if(s.Last is{}last)ValidateLoopRunOwner(c,t,a,s,last);
  using var q=Command(c,t,"UPDATE quest_loop_sessions SET status=$s,ended_at=CASE WHEN ended_at=0 THEN $n ELSE ended_at END,finalized_at=$f WHERE account_id=$a AND id=$id AND active_run_id IS NULL");q.Parameters.AddWithValue("$s",finalize?2:1);q.Parameters.AddWithValue("$n",now);q.Parameters.AddWithValue("$f",finalize?now:0);q.Parameters.AddWithValue("$a",a);q.Parameters.AddWithValue("$id",s.Id);if(q.ExecuteNonQuery()!=1)throw new MainlineRejectedException();
 }
 private MainlineMutation? LoopKnownStop(SqliteConnection c,SqliteTransaction t,long a,string p,QuestStartRequest r,long now)
 {
  if(r.SetLoopCount==0||r.LoopCount<=1)return null;var s=ReadLoop(c,t,a);if(s is null)throw new MainlineRejectedException();CheckLoopContinuation(c,t,a,r,s);if(ActiveMainlineRun(c,t,a)is not null)throw new MainlineRejectedException();
  var quest=QuestData(r.QuestId).Find(r.QuestId)??throw new MainlineRejectedException();var open=QuestTaCatalog.Contains(r.QuestId)?QuestTaCatalog.Data.IsOpen(quest,now):IsModeQuest(r.QuestId)?IsModeQuestAvailable(quest,now):IsEventQuest(r.QuestId)?eventQuestConfig!.IsOpen(quest,now):mainlineConfig!.IsOpen(quest,now);
  return open?null:EndLoopError(c,t,a,p,s,now,3600);
 }
 private static MainlineMutation EndLoopError(SqliteConnection c,SqliteTransaction t,long a,string p,LoopState s,long now,int error)
 {CloseLoop(c,t,a,s,now,false);return new(p,new(){["select_reward_ids"]=Array.Empty<int>()},new{loop_session_id=s.Id,quest_id=s.Quest,terminated=true,error_id=error},new{stamina_spent=0,items=Array.Empty<object>(),free_spent=0,paid_spent=0},LoopFingerprint,error,error==2218?"Not enough AP for automatic recovery.":"Quest period is closed.");}
 private sealed record LoopCure(string Player,GrowthQuantity[] Foods,ulong Free,ulong Paid,int Added);
 private LoopCure? PlanLoopCure(SqliteConnection c,SqliteTransaction t,long a,string p,MainlineQuest quest,QuestStartRequest r,long now)
 {
  var wallet=JsonNode.Parse(p)!.AsObject();var ap=MainlineStamina.Read(wallet,now);if(ap.Current>=quest.Stamina)return new(p,[],0,0,0);
  if(r.LoopCount<=1||loopGrowth is null)return null;
  try
  {
   var foods=new List<GrowthQuantity>();var added=0;var rows=loopGrowth.Rows("ItemConsumeMaster").Where(x=>GrowthCatalog.I(x,"ItemCategory")is 3 or 4).Select(x=>new{Row=x,Id=GrowthCatalog.I(x,"ItemId"),End=x.GetProperty("EndDate").GetString()??""}).Select(x=>new{ x.Row,x.Id,x.End,Expiry=x.End.Length==0?long.MaxValue:QuestModesConfig.JstTimestamp(x.End)}).Where(x=>x.Expiry>=now).Select(x=>new{x.Row,x.Id,x.End,x.Expiry,Value=GrowthEngine.Cure(loopGrowth,0,ap.Maximum,0,[new(6,x.Id,1)],now).StaminaAdded}).OrderByDescending(x=>x.End.Length>0).ThenBy(x=>x.Expiry).ThenBy(x=>x.Value).ThenBy(x=>GrowthCatalog.I(x.Row,"ItemCategory")).ThenBy(x=>x.Id).ToArray();
   foreach(var row in rows)
   {var need=quest.Stamina-ap.Current-added;if(need<=0)break;var owned=CommerceQuantity(c,t,a,6,row.Id,0);var qty=checked((int)Math.Min(owned,(need+row.Value-1)/row.Value));if(qty<=0)continue;foods.Add(new(6,row.Id,qty));added=checked(added+qty*row.Value);}
   var gems=ap.Current+added>=quest.Stamina?0:r.IsCureStaminaUseCoin?(quest.Stamina-ap.Current-added+ap.Maximum-1)/ap.Maximum:0;
   if(ap.Current+added+gems*ap.Maximum<quest.Stamina)return null;
   var plan=GrowthEngine.Cure(loopGrowth,ap.Current,ap.Maximum,gems,foods.ToArray(),now);var debited=DebitCurrency(p,plan.GemCost,out var free,out var paid);if(debited is null)return null;
   var after=JsonNode.Parse(debited)!.AsObject();new MainlineStamina(ap.Current+plan.StaminaAdded,ap.Maximum,ap.Current+plan.StaminaAdded>=ap.Maximum?now:ap.Anchor).Save(after);return new(after.ToJsonString(),foods.ToArray(),free,paid,plan.StaminaAdded);
  }catch(GrowthRejectedException){return null;}catch(OverflowException){return null;}
 }
 private static void SpendLoopFoods(SqliteConnection c,SqliteTransaction t,long a,LoopCure cure)
 {foreach(var f in cure.Foods){using var q=Command(c,t,"UPDATE owned_consumes SET item_qty=item_qty-$n WHERE account_id=$a AND item_id=$i AND item_qty>=$n");q.Parameters.AddWithValue("$n",f.Quantity);q.Parameters.AddWithValue("$a",a);q.Parameters.AddWithValue("$i",f.ItemId);if(q.ExecuteNonQuery()!=1)throw new MainlineRejectedException();}}
 private int BeginLoopRun(SqliteConnection c,SqliteTransaction t,long a,QuestStartRequest r,LoopState? old,JsonElement deck,object? bot,long now)
 {
  if(r.SetLoopCount==0)return 0;if(old is not null)return old.Id;EnsureInstanceCapacity(c,t,"quest_loop_sessions",1);
  using var q=Command(c,t,"INSERT INTO quest_loop_sessions(account_id,quest_id,max_loop_count,next_loop_count,status,config_hash,frozen_json,summary_json,created_at,ended_at,finalized_at) VALUES($a,$q,$m,1,0,$h,$f,'{}',$n,0,0) RETURNING id");q.Parameters.AddWithValue("$a",a);q.Parameters.AddWithValue("$q",r.QuestId);q.Parameters.AddWithValue("$m",r.SetLoopCount);q.Parameters.AddWithValue("$h",QuestHash(r.QuestId));q.Parameters.AddWithValue("$f",JsonSerializer.Serialize(new{deck_no=r.DeckNo,support_user_id=r.SupportUserId,coin=r.IsCureStaminaUseCoin,deck,bot_support=bot}));q.Parameters.AddWithValue("$n",now);return checked((int)(long)q.ExecuteScalar()!);
 }
 private static void BindLoopRun(SqliteConnection c,SqliteTransaction t,long a,int session,int run)
 {if(session==0)return;var s=ReadLoop(c,t,a)??throw new InvalidDataException("Missing loop session.");if(s.Id!=session||s.Active is not null)throw new MainlineRejectedException();ValidateLoopRunOwner(c,t,a,s,run);using var q=Command(c,t,"UPDATE quest_loop_sessions SET active_run_id=$r WHERE id=$s AND account_id=$a AND status=0 AND active_run_id IS NULL");q.Parameters.AddWithValue("$r",run);q.Parameters.AddWithValue("$s",session);q.Parameters.AddWithValue("$a",a);if(q.ExecuteNonQuery()!=1)throw new MainlineRejectedException();}
 private static void ValidateLoopResult(SqliteConnection c,SqliteTransaction t,long a,MainlineRun run,QuestResultRequest r)
 {
  using var d=JsonDocument.Parse(run.Payload);var loop=d.RootElement.TryGetProperty("loop_session_id",out var id)&&id.GetInt32()>0;
  if(!loop){if(r.IsLoopMode||r.LoopCount!=0)throw new MainlineRejectedException();return;}
  var s=ReadLoop(c,t,a)??throw new MainlineRejectedException();if(s.Id!=id.GetInt32()||s.Active!=run.Id||!r.IsLoopMode||r.LoopCount!=s.Next||s.Quest!=run.QuestId)throw new MainlineRejectedException();ValidateLoopRunOwner(c,t,a,s,run.Id);
 }
 private static void CompleteLoopRun(SqliteConnection c,SqliteTransaction t,long a,MainlineRun run,bool won,long now)
 {
  using var d=JsonDocument.Parse(run.Payload);if(!d.RootElement.TryGetProperty("loop_session_id",out var id)||id.GetInt32()==0)return;
  var s=ReadLoop(c,t,a)??throw new MainlineRejectedException();if(s.Id!=id.GetInt32()||s.Active!=run.Id||s.Quest!=run.QuestId)throw new MainlineRejectedException();ValidateLoopRunOwner(c,t,a,s,run.Id);var end=!won||s.Next==s.Max;
  using var q=Command(c,t,"UPDATE quest_loop_sessions SET active_run_id=NULL,last_run_id=$r,next_loop_count=$next,status=$state,ended_at=$end WHERE account_id=$a AND id=$id AND active_run_id=$r");q.Parameters.AddWithValue("$r",run.Id);q.Parameters.AddWithValue("$next",won?s.Next+1:s.Next);q.Parameters.AddWithValue("$state",end?1:0);q.Parameters.AddWithValue("$end",end?now:0);q.Parameters.AddWithValue("$a",a);q.Parameters.AddWithValue("$id",s.Id);if(q.ExecuteNonQuery()!=1)throw new MainlineRejectedException();
 }
 public AccountResult FinishQuestLoop(QuestLoopResultRequest r)=>MutateMainline(r.AuthToken,r.RetryToken,QuestLoopResultRequest.Route,r.Semantic,(c,t,a,p,n)=>
 {
  if(ActiveMainlineRun(c,t,a)is not null)throw new MainlineRejectedException();var s=ReadLoop(c,t,a)??ReadLoop(c,t,a,false)??throw new MainlineRejectedException();
  if(s.Active is not null)throw new MainlineRejectedException();if(s.Last is{}last)ValidateLoopRunOwner(c,t,a,s,last);
  if(s.Status==2&&s.Summary.TryGetProperty("quest_loop_info",out _))return new(p,s.Summary.EnumerateObject().ToDictionary(x=>x.Name,x=>(object?)x.Value.Clone()),new{loop_session_id=s.Id,replayed=true},new{},LoopFingerprint);
  if(s.Status==2)throw new MainlineRejectedException();var ap=0;var items=new Dictionary<(int Type,int Id),int>();
  using(var q=Command(c,t,"SELECT e.cost_json,q.id,q.quest_id,q.account_id FROM economy_transactions e JOIN quest_runs q ON q.id=json_extract(e.result_json,'$.audit.run_id') AND q.account_id=e.account_id WHERE e.account_id=$a AND e.route='quest/start' AND json_extract(e.result_json,'$.audit.loop_session_id')=$s ORDER BY q.id"))
  {q.Parameters.AddWithValue("$a",a);q.Parameters.AddWithValue("$s",s.Id);using var rr=q.ExecuteReader();while(rr.Read()){if(rr.GetInt32(2)!=s.Quest||rr.GetInt64(3)!=a)throw new InvalidDataException("Invalid loop summary source.");using var cost=JsonDocument.Parse(rr.GetString(0));var k=cost.RootElement;ap=checked(ap+k.GetProperty("stamina_spent").GetInt32());foreach(var f in k.GetProperty("items").EnumerateArray()){var key=(6,f.GetProperty("ItemId").GetInt32());items[key]=checked(items.GetValueOrDefault(key)+f.GetProperty("Quantity").GetInt32());}foreach(var key in new[]{("free_spent",2),("paid_spent",1)}){var qty=k.GetProperty(key.Item1).GetInt32();if(qty>0)items[(key.Item2,0)]=checked(items.GetValueOrDefault((key.Item2,0))+qty);}}}
  var extra=new Dictionary<string,object?>{["is_follow_max"] = IsLocalBotFollowMax(c,t,a,p),["quest_loop_info"]=new{use_items=items.OrderBy(x=>x.Key.Type).ThenBy(x=>x.Key.Id).Select(x=>new{item_id=x.Key.Id,item_type=x.Key.Type,item_qty=x.Value}).ToArray(),total_use_stamina=ap}};
  CloseLoop(c,t,a,s,n,true);using(var q=Command(c,t,"UPDATE quest_loop_sessions SET summary_json=$j WHERE id=$s AND account_id=$a")){q.Parameters.AddWithValue("$j",JsonSerializer.Serialize(extra));q.Parameters.AddWithValue("$s",s.Id);q.Parameters.AddWithValue("$a",a);q.ExecuteNonQuery();}
  return new(p,extra,new{loop_session_id=s.Id,quest_id=s.Quest,typed_grants=Array.Empty<object>()},new{new_rewards=0},LoopFingerprint);
 });
}
