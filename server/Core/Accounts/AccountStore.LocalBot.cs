using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
 private sealed record BotRelation(bool Following,bool Follower,bool NewFollower,long FollowedAt,long FollowerAt);
 private static BotRelation ReadBotRelation(SqliteConnection c,SqliteTransaction t,long account,int target)
 {
  var bot=LocalBotCatalog.Canonical(target);if(bot==0)throw new InvalidDataException("Unknown Bot identity.");
  using var q=Command(c,t,bot==LocalBotCatalog.CanonicalId?"SELECT is_following,is_follower,new_follower,followed_at,follower_at FROM local_bot_relations WHERE account_id=$id":"SELECT is_following,is_follower,new_follower,followed_at,follower_at FROM local_bot_peer_relations WHERE account_id=$id AND bot_user_id=$bot");
  q.Parameters.AddWithValue("$id",account);if(bot!=LocalBotCatalog.CanonicalId)q.Parameters.AddWithValue("$bot",bot);using var r=q.ExecuteReader();
  return r.Read()?new(r.GetInt32(0)==1,r.GetInt32(1)==1,r.GetInt32(2)==1,r.GetInt64(3),r.GetInt64(4)):new(false,false,false,0,0);
 }
 private static long BotRelationSum(SqliteConnection c,SqliteTransaction t,long account,string field)
 {
  if(field is not("is_following"or"new_follower"))throw new InvalidDataException("Unknown relation count.");
  using var q=Command(c,t,"SELECT COALESCE(SUM(n),0) FROM (SELECT "+field+" n FROM local_bot_relations WHERE account_id=$id UNION ALL SELECT "+field+" n FROM local_bot_peer_relations WHERE account_id=$id)");q.Parameters.AddWithValue("$id",account);return(long)q.ExecuteScalar()!;
 }
 internal static long ReadLocalBotFollowCount(SqliteConnection c,SqliteTransaction t,long account)=>BotRelationSum(c,t,account,"is_following");
 private static int ReadLocalBotNewFollowerCount(SqliteConnection c,SqliteTransaction t,long account)=>checked((int)BotRelationSum(c,t,account,"new_follower"));
 private static int BotFollowMax(string player)
 {using var d=JsonDocument.Parse(player);return d.RootElement.GetProperty("rank").GetInt32()switch{1=>10,2=>15,3=>20,4=>25,5=>30,>=6=>50,_=>throw new InvalidDataException("Invalid player rank.")};}
 private static bool IsLocalBotFollowMax(SqliteConnection c,SqliteTransaction t,long account,string player)=>ReadLocalBotFollowCount(c,t,account)>=BotFollowMax(player);
 private static int BotFriendType(BotRelation r)=>r.Following?r.Follower?4:2:r.Follower?3:1;
 private Dictionary<string,object?> BotUser(SqliteConnection c,SqliteTransaction t,long account,int target,long now)=>LocalBotCatalog.User(target,BotFriendType(ReadBotRelation(c,t,account,target)),now,compactBotPublicIds);
 private AccountResult BotSnapshot(SqliteConnection c,SqliteTransaction t,long account,string player,long now,Dictionary<string,object?>? extras=null)
 {
  var action=Snapshot(c,t,account,player,nowOverride:now);if(extras is not null)foreach(var p in extras)action[p.Key]=p.Value;
  return new(200,action,NewFollowerBadge:ReadLocalBotNewFollowerCount(c,t,account));
 }
 private static void SaveBotRelation(SqliteConnection c,SqliteTransaction t,long account,int bot,BotRelation after,long now)
 {
  var legacy=bot==LocalBotCatalog.CanonicalId;
  var sql=legacy?"INSERT INTO local_bot_relations(account_id,is_following,is_follower,new_follower,followed_at,follower_at,updated_at) VALUES($id,$out,$in,$new,$fo,$fi,$now) ON CONFLICT(account_id)":"INSERT INTO local_bot_peer_relations(account_id,bot_user_id,is_following,is_follower,new_follower,followed_at,follower_at,updated_at) VALUES($id,$bot,$out,$in,$new,$fo,$fi,$now) ON CONFLICT(account_id,bot_user_id)";
  using var q=Command(c,t,sql+" DO UPDATE SET is_following=excluded.is_following,is_follower=excluded.is_follower,new_follower=excluded.new_follower,followed_at=excluded.followed_at,follower_at=excluded.follower_at,updated_at=excluded.updated_at");
  q.Parameters.AddWithValue("$id",account);if(!legacy)q.Parameters.AddWithValue("$bot",bot);q.Parameters.AddWithValue("$out",after.Following?1:0);q.Parameters.AddWithValue("$in",after.Follower?1:0);q.Parameters.AddWithValue("$new",after.NewFollower?1:0);q.Parameters.AddWithValue("$fo",after.FollowedAt);q.Parameters.AddWithValue("$fi",after.FollowerAt);q.Parameters.AddWithValue("$now",now);q.ExecuteNonQuery();
 }
 public AccountResult LocalBotSocial(LocalBotRequest request)
 {
  if(string.IsNullOrEmpty(request.AuthToken))return new(401);
  var read=request.Route is "friend/follow_list" or "friend/search";
  lock(gate)
  {
   using var c=new SqliteConnection(new SqliteConnectionStringBuilder(connectionString){Mode=read?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWrite}.ToString());c.Open();using var t=c.BeginTransaction(deferred:read);var now=Now;
   if(!TryReadAuthenticatedAccount(c,t,request.AuthToken,now,out var account,out var player))return new(401);
   if(!HasFinishedTutorial(player))return new(409);
   if(read)
   {
    if(request.Route=="friend/search")return LocalBotCatalog.FromGameId(request.GameId,compactBotPublicIds)is var found&&found!=0?BotSnapshot(c,t,account,player,now,new(){["user_data"]=BotUser(c,t,account,found,now)}):new(404);
    var following=new List<object>();var followers=new List<object>();
    foreach(var id in LocalBotCatalog.CanonicalIds){var state=ReadBotRelation(c,t,account,id);if(state.Following)following.Add(BotUser(c,t,account,id,now));if(state.Follower)followers.Add(BotUser(c,t,account,id,now));}
    return BotSnapshot(c,t,account,player,now,new(){["follow_list"]=following.ToArray(),["follower_list"]=followers.ToArray()});
   }
   var retry=Hash(request.RetryToken);var old=FindEconomyReceipt(c,t,account,retry);
   if(old is not null)return old.Route==request.Route&&old.Semantic==request.Semantic?BotSnapshot(c,t,account,player,now):new(409);
   if(request.Route!="friend/del_follow_badge"&&!LocalBotCatalog.Known(request.TargetId))return new(409);
   if(request.Route=="friend/del_follow_badge")
   {
    var count=ReadLocalBotNewFollowerCount(c,t,account);
    foreach(var table in new[]{"local_bot_relations","local_bot_peer_relations"}){using var q=Command(c,t,"UPDATE "+table+" SET new_follower=0,updated_at=$now WHERE account_id=$id AND new_follower=1");q.Parameters.AddWithValue("$id",account);q.Parameters.AddWithValue("$now",now);q.ExecuteNonQuery();}
    InsertEconomyReceipt(c,t,account,retry,request.Route,request.Semantic,LocalBotCatalog.Fingerprint,JsonSerializer.Serialize(new{source="local-bot-social",cleared_badges=count}),"{}",now);
   }
   else
   {
    var bot=LocalBotCatalog.Canonical(request.TargetId);var before=ReadBotRelation(c,t,account,bot);
    if(request.Route=="friend/follow"&&!before.Following&&IsLocalBotFollowMax(c,t,account,player))return new(409);
    var after=request.Route switch
    {
     "friend/follow"=>before.Following?before:new(true,true,before.NewFollower||!before.Follower,now,before.Follower?before.FollowerAt:now),
     "friend/un_follow"=>before with{Following=false,FollowedAt=0},
     "friend/cancel_follower"=>before with{Follower=false,NewFollower=false,FollowerAt=0},
     _=>throw new InvalidDataException("Unknown Bot mutation.")
    };
    if(after!=before)SaveBotRelation(c,t,account,bot,after,now);
    InsertEconomyReceipt(c,t,account,retry,request.Route,request.Semantic,LocalBotCatalog.SupportFingerprint(bot),JsonSerializer.Serialize(new{source="local-bot-social",canonical_bot_id=bot,before,after}),"{}",now);
   }
   var result=BotSnapshot(c,t,account,player,now);t.Commit();return result;
  }
 }
 private void ValidateLocalBotIdentities(SqliteConnection c)
 {
  using var q=c.CreateCommand();q.CommandText="SELECT account_id,player_json FROM accounts";using var r=q.ExecuteReader();
  while(r.Read())
  {
   using var d=JsonDocument.Parse(r.GetString(1));if(r.GetInt64(0)is >=LocalBotCatalog.ReservedMin and <=LocalBotCatalog.ReservedMax||LocalBotCatalog.PublicIdKnown(d.RootElement.GetProperty("my_game_id").GetUInt64(),IdentitySchema.Read(c).Compact))throw new InvalidDataException("Reserved local Bot identity collides with actual account.");
  }
 }
 public AccountResult ProjectLocalBotBadge(AccountResult result)
 {
  if(result.Status!=200||result.Action is null||!result.Action.TryGetValue("u_user",out var value)||value is not JsonElement[]{Length:1}users)return result;
  using var c=new SqliteConnection(new SqliteConnectionStringBuilder(connectionString){Mode=SqliteOpenMode.ReadOnly}.ToString());c.Open();using var t=c.BeginTransaction(deferred:true);
  return result with{NewFollowerBadge=ReadLocalBotNewFollowerCount(c,t,users[0].GetProperty("user_id").GetInt32())};
 }
 private Dictionary<string,object?> BotSupportList(SqliteConnection c,SqliteTransaction t,long account,long now)=>new(){["support_list"]=LocalBotCatalog.RecommendedSupportIds.Select(id=>BotUser(c,t,account,id,now)).ToArray()};
 private AccountResult ActiveLocalBotSupport(QuestActiveSupportRequest request)
 {
  if(string.IsNullOrEmpty(request.AuthToken))return new(401);
  lock(gate)
  {
   using var c=new SqliteConnection(new SqliteConnectionStringBuilder(connectionString){Mode=SqliteOpenMode.ReadOnly}.ToString());c.Open();using var t=c.BeginTransaction(deferred:true);var now=Now;
   if(!TryReadAuthenticatedAccount(c,t,request.AuthToken,now,out var account,out var player))return new(401);
   if(request.SupportUserId==0)return BotSnapshot(c,t,account,player,now,new(){["support_list"]=Array.Empty<object>()});
   if(!LocalBotCatalog.Known(request.SupportUserId))return new(409);
   var active=ActiveMainlineRun(c,t,account);
   if(active is not null)
   {
    using var d=JsonDocument.Parse(active.Payload);
    if(!d.RootElement.TryGetProperty("support_user_id",out var id)||id.GetInt32()!=request.SupportUserId||!d.RootElement.TryGetProperty("bot_support",out var frozen)||frozen.ValueKind!=JsonValueKind.Object)return new(409);
    return BotSnapshot(c,t,account,player,now,new(){["support_list"]=new[]{frozen.GetProperty("user").Clone()}});
   }
   // Suspend-result cache recovery can occur after the server already committed.
   using var q=Command(c,t,"SELECT payload_json FROM quest_runs WHERE account_id=$id AND kind=1 AND json_extract(payload_json,'$.support_user_id')=$support ORDER BY created_at DESC,id DESC LIMIT 1");q.Parameters.AddWithValue("$id",account);q.Parameters.AddWithValue("$support",request.SupportUserId);
   if(q.ExecuteScalar()is string payload)
   {
    using var d=JsonDocument.Parse(payload);if(d.RootElement.TryGetProperty("bot_support",out var frozen)&&frozen.ValueKind==JsonValueKind.Object)return BotSnapshot(c,t,account,player,now,new(){["support_list"]=new[]{frozen.GetProperty("user").Clone()}});
   }
   return BotSnapshot(c,t,account,player,now,new(){["support_list"]=new[]{BotUser(c,t,account,request.SupportUserId,now)}});
  }
 }
 private object? FreezeLocalBotSupport(SqliteConnection c,SqliteTransaction t,long account,MainlineQuest quest,int target,long now)
 {
  if(target==0)return null;
  if(!LocalBotCatalog.Known(target)||quest.BattleId<=0||RealQuestType(quest.QuestId)is <1 or >5)throw new MainlineRejectedException();
  var relation=BotFriendType(ReadBotRelation(c,t,account,target));return new{canonical_bot_id=LocalBotCatalog.Canonical(target),support_user_id=target,config_hash=LocalBotCatalog.SupportFingerprint(target),friend_type=relation,get_friend_point=LocalBotCatalog.Points(relation),user=LocalBotCatalog.User(target,relation,now,compactBotPublicIds)};
 }
 private static int GrantLocalBotVictory(JsonObject wallet,MainlineRun run,bool won,bool story)
 {
  if(!won||story)return 0;using var data=JsonDocument.Parse(run.Payload);
  if(!data.RootElement.TryGetProperty("bot_support",out var frozen)||frozen.ValueKind==JsonValueKind.Null)return 0;
  var id=data.RootElement.GetProperty("support_user_id").GetInt32();var relation=frozen.GetProperty("friend_type").GetInt32();var amount=frozen.GetProperty("get_friend_point").GetInt32();
  if(!LocalBotCatalog.Known(id)||id!=frozen.GetProperty("support_user_id").GetInt32()||frozen.GetProperty("canonical_bot_id").GetInt32()!=LocalBotCatalog.Canonical(id)||amount!=LocalBotCatalog.Points(relation)||frozen.GetProperty("user").GetProperty("user_id").GetInt32()!=id)throw new InvalidDataException("Invalid frozen Bot support.");
  var before=wallet["friend_pt"]!.GetValue<long>();if(before<0||before>99999999-amount)throw new GachaCapacityException();wallet["friend_pt"]=before+amount;return amount;
 }
 private AccountResult ReadLocalBotProfile(ProfileTopRequest request)=>ReadFormation(request.AuthToken,(c,t,account,player,now)=>
 {
  if(!LocalBotCatalog.Known(request.UserId))return new(403);var bot=BotUser(c,t,account,request.UserId,now);var date=(string)bot["last_login_at"]!;
  return BotSnapshot(c,t,account,player,now,new(){["profile_data"]=new{name=bot["name"],rank=100,my_game_id=bot["my_game_id"],message=bot["message"],profile_birthday="",title_id1=0,title_id2=0,card_id=bot["support_card_id"],profile_skin_id=bot["support_card_skin_id"],main_quest_id=0,card_count=LocalBotCatalog.Canonical(request.UserId)==LocalBotCatalog.CanonicalId?3:1,expedition_count=0,equip_count=LocalBotCatalog.Canonical(request.UserId)==LocalBotCatalog.CanonicalId?5:3,created_at=date}});
 });
}
