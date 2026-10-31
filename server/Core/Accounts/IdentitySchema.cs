using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;

// Public-ID allocation is a local policy, independent of internal account/FK IDs.
internal static class IdentitySchema
{
 internal const string Sql="""
CREATE TABLE account_identity_policy (
 singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton=1),
 bot_public_mode TEXT NOT NULL CHECK(bot_public_mode IN('compact','legacy')),
 next_public_id INTEGER NOT NULL CHECK(next_public_id BETWEEN 51 AND 9223372036854775807),
 exhausted INTEGER NOT NULL CHECK(exhausted IN(0,1)),
 CHECK(exhausted=0 OR next_public_id=9223372036854775807)
) STRICT
""";
 internal sealed record Policy(bool Compact,long Next,bool Exhausted);
 internal static Policy Read(SqliteConnection c,SqliteTransaction? t=null)
 {
  using var q=Command(c,t,"SELECT singleton,bot_public_mode,next_public_id,exhausted FROM account_identity_policy");using var r=q.ExecuteReader();
  if(!r.Read()||r.GetInt64(0)!=1)throw new InvalidDataException("Missing identity policy singleton.");
  var mode=r.GetString(1);var next=r.GetInt64(2);var exhausted=r.GetInt64(3);
  if(mode is not("compact"or"legacy")||next<51||exhausted is not(0 or 1)||exhausted==1&&next!=long.MaxValue||r.Read())throw new InvalidDataException("Invalid identity policy singleton.");
  return new(mode=="compact",next,exhausted==1);
 }
 internal static long ReadPublicId(JsonElement player)
 {
  if(!player.TryGetProperty("my_game_id",out var value)||value.ValueKind!=JsonValueKind.Number||!value.TryGetInt64(out var id)||id<=0)
   throw new InvalidDataException("Public ID must be within 1..Int64.MaxValue.");
  return id;
 }
 private static long ValidatePlayers(SqliteConnection c,SqliteTransaction? t,bool compact)
 {
  using var q=Command(c,t,"SELECT account_id,player_json FROM accounts");using var r=q.ExecuteReader();var seen=new HashSet<long>();long maximum=0;
  while(r.Read())
  {
   using var json=JsonDocument.Parse(r.GetString(1));var id=ReadPublicId(json.RootElement);
   if(r.GetInt64(0)is >=LocalBotCatalog.ReservedMin and <=LocalBotCatalog.ReservedMax||LocalBotCatalog.PublicIdKnown((ulong)id,compact)||!seen.Add(id))throw new InvalidDataException("Player identity collides with a reserved or duplicate identity.");
   maximum=Math.Max(maximum,id);
  }
  return maximum;
 }
 internal static void Validate(SqliteConnection c,SqliteTransaction? t=null)
 {
  var p=Read(c,t);var maximum=ValidatePlayers(c,t,p.Compact);
  if(LocalBotCatalog.PublicIdKnown((ulong)p.Next,p.Compact)||!p.Exhausted&&p.Next<=maximum)throw new InvalidDataException("Identity counter is behind existing public IDs or within the Bot namespace.");
 }
 private static long SkipBots(long value,bool compact)=>!compact&&value is >=900000001 and <=900000050?900000051:value;
 internal static void Create(SqliteConnection c,SqliteTransaction t,bool legacy)
 {
  var maximum=ValidatePlayers(c,t,!legacy);var exhausted=maximum==long.MaxValue;
  var next=exhausted?long.MaxValue:SkipBots(Math.Max(51,maximum+1),!legacy);
  using var q=Command(c,t,Sql+"; INSERT INTO account_identity_policy(singleton,bot_public_mode,next_public_id,exhausted) VALUES(1,$mode,$next,$exhausted)");
  q.Parameters.AddWithValue("$mode",legacy?"legacy":"compact");q.Parameters.AddWithValue("$next",next);q.Parameters.AddWithValue("$exhausted",exhausted?1:0);q.ExecuteNonQuery();
 }
 internal static long? Allocate(SqliteConnection c,SqliteTransaction t)
 {
  Validate(c,t);var p=Read(c,t);if(p.Exhausted)return null;
  var id=p.Next;var last=id==long.MaxValue;var next=last?id:SkipBots(id+1,p.Compact);
  using var q=Command(c,t,"UPDATE account_identity_policy SET next_public_id=$next,exhausted=$last WHERE singleton=1 AND next_public_id=$old AND exhausted=0");
  q.Parameters.AddWithValue("$next",next);q.Parameters.AddWithValue("$last",last?1:0);q.Parameters.AddWithValue("$old",id);if(q.ExecuteNonQuery()!=1)throw new InvalidDataException("Identity allocation changed inside the transaction.");return id;
 }
 // This is a real player lookup. Friend-search shortcuts are deliberately absent.
 internal static long? FindAccountByPublicId(SqliteConnection c,SqliteTransaction t,long publicId)
 {
  if(publicId<=0)return null;
  using var q=Command(c,t,"SELECT account_id FROM accounts WHERE json_extract(player_json,'$.my_game_id')=$public");q.Parameters.AddWithValue("$public",publicId);using var r=q.ExecuteReader();if(!r.Read())return null;var id=r.GetInt64(0);if(r.Read())throw new InvalidDataException("Duplicate public player ID.");return id;
 }
 private static SqliteCommand Command(SqliteConnection c,SqliteTransaction? t,string sql){var q=c.CreateCommand();q.Transaction=t;q.CommandText=sql;return q;}
}
