using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;
internal static class Schema19
{
 internal const string PeerSql="""
CREATE TABLE local_bot_peer_relations (
 account_id INTEGER NOT NULL REFERENCES accounts(account_id),
 bot_user_id INTEGER NOT NULL CHECK(bot_user_id BETWEEN 2147000020 AND 2147000068),
 is_following INTEGER NOT NULL CHECK(is_following IN(0,1)),
 is_follower INTEGER NOT NULL CHECK(is_follower IN(0,1)),
 new_follower INTEGER NOT NULL CHECK(new_follower IN(0,1) AND new_follower<=is_follower),
 followed_at INTEGER NOT NULL CHECK(followed_at>=0),
 follower_at INTEGER NOT NULL CHECK(follower_at>=0),
 updated_at INTEGER NOT NULL CHECK(updated_at>0),
 PRIMARY KEY(account_id,bot_user_id),
 CHECK((is_following=0 AND followed_at=0) OR (is_following=1 AND followed_at>0)),
 CHECK((is_follower=0 AND follower_at=0) OR (is_follower=1 AND follower_at>0))
) STRICT
""";
 internal static void Create(SqliteConnection c,SqliteTransaction t){using var q=c.CreateCommand();q.Transaction=t;q.CommandText=PeerSql;q.ExecuteNonQuery();}
}
