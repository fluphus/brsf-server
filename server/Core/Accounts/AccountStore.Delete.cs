using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;

public sealed record UserDeleteResult(AccountResult Result,string Reason);

public sealed partial class AccountStore
{
 // Root-reviewed schema21 ownership whitelist. Unknown future tables fail exact startup validation.
 private static readonly string[] AccountDeletionTables=["management_operations","acquired_equip_groups","beginner_claims","creation_receipts","deck_slot_equips","deck_slots","economy_transactions","event_drop_claims","event_point_reward_claims","event_points","event_quest_mission_claims","event_quests","expedition_allocations","expedition_runs","expedition_slots","home_preferences","local_bot_peer_relations","local_bot_relations","login_bonus_deliveries","login_bonus_progress","mission_claims","mission_epoch_excluded_receipts","mission_epochs","mission_facts","owned_awakenings","owned_backgrounds","owned_card_skins","owned_cards","owned_chara_voices","owned_consumes","owned_enhances","owned_equip_materials","owned_equips","owned_event_items","owned_extra_shop_counts","owned_shop_counts","owned_titles","present_equips","present_items","quest_daily_counts","quest_expert_cycles","quest_loop_sessions","quest_mission_claims","quest_power_reports","quest_runs","sessions","story_reads","takeover_credentials","takeover_grants","tutorial_gacha_draws","tutorial_receipts","user_decks","user_gacha_exchanges","user_gachas","user_profiles","user_quests","user_support_leaders","vip_daily_deliveries","vip_entitlements","vip_present_items","accounts"];
 private static UserDeleteResult DeleteStatus(int status,string reason)=>new(new(status,status==200?new():null),reason);
 public UserDeleteResult DeleteAccount(UserDeleteRequest request)
 {
  lock(gate)
  {
   using var c=Open();using var t=c.BeginTransaction(deferred:false);var now=Now;
   try
   {
    var uuidHash=Hash("local-account-delete-uuid-v1\0"+request.Uuid);long account=0;
    using(var q=Command(c,t,"SELECT account_id FROM accounts WHERE uuid=$u")){q.Parameters.AddWithValue("$u",request.Uuid);if(q.ExecuteScalar()is long id)account=id;}
    bool deleted;using(var q=Command(c,t,"SELECT EXISTS(SELECT 1 FROM account_deletion_journal WHERE uuid_hash=$h)")){q.Parameters.AddWithValue("$h",uuidHash);deleted=Convert.ToInt64(q.ExecuteScalar())!=0;}
    var authAccount=0L;var validAuth=!string.IsNullOrEmpty(request.AuthToken)&&TryReadAuthenticatedAccount(c,t,request.AuthToken!,now,out authAccount,out _);
    if(deleted)
    {
     if(account!=0)return DeleteStatus(409,"journal-account-conflict");
     // All sessions of the deleted account were removed. Any still-valid token belongs elsewhere.
     if(validAuth)return DeleteStatus(409,"session-account-mismatch");
     return DeleteStatus(200,"replay");
    }
    if(account==0)return DeleteStatus(401,"uuid-unknown");
    if(IsManagementLeased(account))return DeleteStatus(409,"maintenance-active");
    if(validAuth&&authAccount!=account)return DeleteStatus(409,"session-account-mismatch");
    // Hard deletion includes active/pending work. Do not settle it, grant rewards or demand IDPASS.
    using(var q=Command(c,t,"PRAGMA defer_foreign_keys=ON"))q.ExecuteNonQuery();
    foreach(var table in AccountDeletionTables)
    {
     using var q=Command(c,t,"DELETE FROM \""+table+"\" WHERE account_id=$a");q.Parameters.AddWithValue("$a",account);
     var count=q.ExecuteNonQuery();if(table=="accounts"&&count!=1)throw new InvalidDataException("Deletion target changed.");
    }
    using(var q=Command(c,t,"PRAGMA foreign_key_check"))using(var r=q.ExecuteReader())if(r.Read())throw new InvalidDataException("Deletion ownership closure failed.");
    using(var q=Command(c,t,"INSERT INTO account_deletion_journal(uuid_hash,deleted_at)VALUES($h,$now)")){q.Parameters.AddWithValue("$h",uuidHash);q.Parameters.AddWithValue("$now",now);q.ExecuteNonQuery();}
    t.Commit();return DeleteStatus(200,"ok");
   }
   catch(SqliteException){return DeleteStatus(503,"storage-unavailable");}
   catch(InvalidDataException){return DeleteStatus(503,"closure-invalid");}
  }
 }
}
