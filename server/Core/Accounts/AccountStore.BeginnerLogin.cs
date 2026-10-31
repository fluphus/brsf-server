using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
 private sealed record LoginBonusObservation(object[] Notices,object[] NotificationItems,bool IsSendPresentBox);
 private sealed record LoginProgress(long Epoch,int Count,long LastDay,int Cycle,int Chara);
 private static long BeginnerBusinessDay(long now)=>checked((now+14400)/86400);
 private int LoginViewChara(SqliteConnection c,SqliteTransaction t,long account)
 {
  using var q=Command(c,t,"SELECT chara_id FROM owned_cards WHERE account_id=$a ORDER BY CASE WHEN card_id=COALESCE((SELECT slot_1_card_id FROM home_preferences WHERE account_id=$a),0)THEN 0 ELSE 1 END,user_card_id LIMIT 1");q.Parameters.AddWithValue("$a",account);
  return q.ExecuteScalar()is long n?checked((int)n):throw new InvalidDataException("Login bonus requires a genuine owned character.");
 }
 private LoginBonusObservation ObserveBeginnerLogin(SqliteConnection c,SqliteTransaction t,long account,string player,long now,string triggerRoute)
 {
  if(triggerRoute is not("user/login"or"home"))throw new InvalidDataException("Invalid login producer trigger.");
  int? VipDay()
  {
   if(vipPassConfig is null)return null;using var q=Command(c,t,"SELECT day_count FROM vip_daily_deliveries WHERE account_id=$a AND business_day=$d");q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$d",VipBusinessDay(now));return q.ExecuteScalar()is long n?checked((int)n):null;
  }
  var vipBefore=beginnerConfig is null?null:VipDay();DeliverVipLoginPresent(c,t,account,now);
  if(beginnerConfig is null||!HasFinishedTutorial(player))return new([],[],false);
  var notices=new List<object>();var notificationItems=new List<object>();var sent=false;var chara=LoginViewChara(c,t,account);var day=BeginnerBusinessDay(now);
  foreach(var bonus in new[]{1001,4001,5001})
  {
   var rule=beginnerConfig.LoginBonuses.Single(b=>b.LoginBonusId==bonus);if(rule.IsDeleted||!ActivityDates.IsOpen(rule.OpenDate,rule.CloseDate,now))continue;
   LoginProgress? progress;using(var q=Command(c,t,"SELECT epoch_seconds,login_count,last_business_day,cycle_index,view_chara_id FROM login_bonus_progress WHERE account_id=$a AND login_bonus_id=$b"))
   {q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$b",bonus);using var r=q.ExecuteReader();progress=r.Read()?new(r.GetInt64(0),r.GetInt32(1),r.GetInt64(2),r.GetInt32(3),r.GetInt32(4)):null;}
   if(progress is not null&&(progress.LastDay>=day||bonus==4001&&progress.Count>=14))continue;
   var count=checked((progress?.Count??0)+1);var cycle=bonus==1001?(count-1)/10:0;var issuedDay=bonus==1001?(count-1)%10+1:count;var view=progress?.Chara??chara;
   var definition=beginnerConfig.LoginDays.SingleOrDefault(d=>d.LoginBonusId==bonus&&d.DayCount==issuedDay&&!d.IsDeleted);
   var retry=Hash($"local-login-bonus:{account}:{bonus}:{day}");if(FindEconomyReceipt(c,t,account,retry)is not null)throw new GachaCapacityException();
   using(var q=Command(c,t,"INSERT INTO login_bonus_progress(account_id,login_bonus_id,epoch_seconds,login_count,last_business_day,cycle_index,view_chara_id)VALUES($a,$b,$e,$count,$day,$cycle,$chara)ON CONFLICT(account_id,login_bonus_id)DO UPDATE SET login_count=excluded.login_count,last_business_day=excluded.last_business_day,cycle_index=excluded.cycle_index"))
   {q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$b",bonus);q.Parameters.AddWithValue("$e",progress?.Epoch??now);q.Parameters.AddWithValue("$count",count);q.Parameters.AddWithValue("$day",day);q.Parameters.AddWithValue("$cycle",cycle);q.Parameters.AddWithValue("$chara",view);q.ExecuteNonQuery();}
   object[] items=[];object[] presents=[];string? producer=null;
   if(definition is not null)
   {
    producer=$"login-bonus:{bonus}:cycle:{cycle}:day:{issuedDay}";var rewards=beginnerConfig.LoginRewardsFor(bonus,definition.ItemGroupId);if(rewards.Length==0)throw new InvalidDataException("Original login day has no rewards.");
    var queued=EnqueueGeneralPresents(c,t,account,now,producer,retry,BeginnerCatalog.ExpectedFingerprint,1600,bonus,rewards);items=queued.NotificationItems;presents=queued.Presents.Cast<object>().ToArray();sent|=queued.Presents.Length>0;
    using var q=Command(c,t,"INSERT INTO login_bonus_deliveries(account_id,login_bonus_id,cycle_index,day_count,business_day,created_at,producer_key,receipt_hash)VALUES($a,$b,$c,$d,$day,$now,$key,$hash)");q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$b",bonus);q.Parameters.AddWithValue("$c",cycle);q.Parameters.AddWithValue("$d",issuedDay);q.Parameters.AddWithValue("$day",day);q.Parameters.AddWithValue("$now",now);q.Parameters.AddWithValue("$key",producer);q.Parameters.AddWithValue("$hash",retry);q.ExecuteNonQuery();
    notices.Add(new{login_bonus_id=bonus,receive_days=issuedDay.ToString(CultureInfo.InvariantCulture),view_chara_id=view});notificationItems.AddRange(items);
   }
   var frozen=JsonSerializer.Serialize(new{source="local-login-bonus",trigger_route=triggerRoute,login_bonus_id=bonus,business_day=day,observed_login_count=count,cycle_index=cycle,issued_day=definition is null?0:issuedDay,producer_key=producer,notification_items=items,presents});
   InsertEconomyReceipt(c,t,account,retry,triggerRoute,$"login-observation:{bonus}:{day}",BeginnerCatalog.ExpectedFingerprint,frozen,"{}",now);
  }
  if(vipBefore is null&&VipDay()is{} vipDay)
  {
   sent=true;notices.Add(new{login_bonus_id=9001,receive_days=vipDay.ToString(CultureInfo.InvariantCulture),view_chara_id=chara});notificationItems.Add(new{item_type=2,item_id=0,item_qty=50});
  }
  return new(notices.ToArray(),notificationItems.ToArray(),sent);
 }
 public AccountResult ListLoginBonuses(VipLoginBonusListRequest request)
 {
  if(beginnerConfig is null)return ListVipLoginBonus(request);if(string.IsNullOrEmpty(request.AuthToken))return new(401);
  if(request.LoginBonusIds.Length>100||request.LoginBonusIds.Distinct().Count()!=request.LoginBonusIds.Length||request.LoginBonusIds.Any(id=>!beginnerConfig.LoginBonuses.Any(b=>b.LoginBonusId==id)))return new(400);
  lock(gate)
  {
   using var c=new SqliteConnection(new SqliteConnectionStringBuilder(connectionString){Mode=SqliteOpenMode.ReadOnly}.ToString());c.Open();using var t=c.BeginTransaction(deferred:true);var now=Now;
   if(!TryReadAuthenticatedAccount(c,t,request.AuthToken,now,out var account,out var player))return new(401);if(!HasFinishedTutorial(player))return new(409);
   var rows=new List<object>();foreach(var bonus in request.LoginBonusIds.Order())
   {
    var days=new List<int>();var view=0;
    if(bonus==9001&&vipPassConfig is not null)
    {
     using var q=Command(c,t,"SELECT day_count FROM vip_daily_deliveries WHERE account_id=$a AND activation_id=(SELECT activation_id FROM vip_entitlements WHERE account_id=$a)ORDER BY day_count");q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();while(r.Read())days.Add(r.GetInt32(0));
     view=LoginViewChara(c,t,account);
    }
    else
    {
     using var q=Command(c,t,"SELECT d.day_count,p.view_chara_id FROM login_bonus_deliveries d JOIN login_bonus_progress p ON p.account_id=d.account_id AND p.login_bonus_id=d.login_bonus_id WHERE d.account_id=$a AND d.login_bonus_id=$b AND d.cycle_index=p.cycle_index ORDER BY d.day_count");q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$b",bonus);using var r=q.ExecuteReader();while(r.Read()){days.Add(r.GetInt32(0));view=r.GetInt32(1);}
    }
    if(days.Count>0)rows.Add(new{login_bonus_id=bonus,receive_days=string.Join(',',days.Select(n=>n.ToString(CultureInfo.InvariantCulture))),view_chara_id=view});
   }
   var action=Snapshot(c,t,account,player,nowOverride:now);action["login_bonus"]=rows.ToArray();return new(200,action);
  }
 }
}
