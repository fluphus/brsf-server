using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;
public sealed partial class AccountStore
{
    // Skip bypasses the ordinary slot-1 teaching. Restore only this new local
    // seed's empty first slot, inside the original Skip transaction.
    private bool EnsurePhoneSkipStarterFirstSlot(SqliteConnection c,SqliteTransaction t,long account,long now)
    {
        using var first=Command(c,t,"SELECT EXISTS(SELECT 1 FROM deck_slots WHERE account_id=$a AND deck_no=1 AND slot_no=1)");first.Parameters.AddWithValue("$a",account);
        if((long)first.ExecuteScalar()! !=0)return true;
        var hash=Hash("local-tutorial-starter:"+account);var initial=FindEconomyReceipt(c,t,account,hash);
        if(initial is null||initial.Route!="tutorial/initialize"||initial.Semantic!="starter-v1"||!CompleteTutorialReceiptConfig(c,t,account,hash))return false;
        using var d=JsonDocument.Parse(initial.Result);var s=d.RootElement;
        if(!s.GetProperty("created_deck").GetBoolean()||!s.TryGetProperty("phone_starter_empty_first_slot",out var policy)||!policy.GetBoolean())return false;
        using var check=Command(c,t,"SELECT (SELECT count(*)FROM deck_slots WHERE account_id=$a)=2 AND (SELECT count(*)FROM deck_slots WHERE account_id=$a AND deck_no=1 AND ((slot_no=2 AND card_id=30010003)OR(slot_no=3 AND card_id=30010004)))=2 AND (SELECT count(*)FROM deck_slot_equips WHERE account_id=$a)=0 AND EXISTS(SELECT 1 FROM owned_cards WHERE account_id=$a AND card_id=30010002)");check.Parameters.AddWithValue("$a",account);
        if((long)check.ExecuteScalar()! !=1)return false;
        var marker=Hash("phone-tutorial-skip-first-slot-v1:"+account);
        if(FindEconomyReceipt(c,t,account,marker)is not null)return false;
        using var insert=Command(c,t,"INSERT INTO deck_slots(account_id,deck_no,slot_no,card_id)VALUES($a,1,1,30010002)");insert.Parameters.AddWithValue("$a",account);
        if(insert.ExecuteNonQuery()!=1)throw new InvalidDataException("Skip starter slot changed.");
        InsertEconomyReceipt(c,t,account,marker,"system/phone-tutorial-skip-first-slot","skip-first-slot-v1",CompleteTutorialCatalog.Fingerprint,
            JsonSerializer.Serialize(new{source="phone-tutorial-skip-first-slot-v1",trigger_route="tutorial/skip",account_id=account,inserted_reference=new{deck_no=1,slot_no=1,card_id=30010002},no_inventory_wallet_mutation=true}),"{}",now);
        return true;
    }
}
