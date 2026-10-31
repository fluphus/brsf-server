using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;
public sealed partial class AccountStore
{
    // Explicit LOCAL teaching fallback. The original ten RNG selections and
    // frozen ten-item result remain untouched. Only normal 1200 calls this.
    private void EnsurePhoneTutorialMainWeapon(SqliteConnection c,SqliteTransaction t,long account,long now,EquipmentGrantContext grantContext)
    {
        var enrollment=FindEconomyReceipt(c,t,account,Hash("local-new-account-gifts-enrollment:"+account+":"+NewAccountGiftPolicy.Fingerprint));
        if(enrollment is null)return; // No retroactive change to old accounts.
        if(enrollment.Route!="system/new-account-gifts-enrollment"||enrollment.Semantic!="pending65:"+NewAccountGiftPolicy.Fingerprint||formationConfig is null||mainlineConfig is null||completeTutorialGrowth is null)throw new GachaCapacityException();
        using var target=Command(c,t,"SELECT max(card_id)FROM owned_cards WHERE account_id=$a AND card_id NOT IN(30010002,30010003,30010004)");target.Parameters.AddWithValue("$a",account);
        if(target.ExecuteScalar()is not long rawCard)throw new GachaCapacityException();var cardId=checked((int)rawCard);
        var card=formationConfig.Cards.SingleOrDefault(x=>x.CardId==cardId&&!x.IsDeleted)??throw new GachaCapacityException();
        var type=card.EquipType;
        using(var owned=Command(c,t,"SELECT equip_id FROM owned_equips e WHERE account_id=$a AND is_deleted=0 AND NOT EXISTS(SELECT 1 FROM deck_slot_equips d WHERE d.account_id=e.account_id AND d.user_equip_id=e.user_equip_id)"))
        {
            owned.Parameters.AddWithValue("$a",account);using var r=owned.ExecuteReader();
            while(r.Read())if(formationConfig.Equips.Any(x=>x.EquipId==r.GetInt32(0)&&x.EquipType==type&&!x.IsDeleted))return;
        }
        var equipId=type switch{101=>110100103,102=>110200103,111=>111100103,112=>111200103,113=>111300103,_=>throw new GachaCapacityException()};
        var equip=mainlineConfig.Equips.SingleOrDefault(x=>x.EquipId==equipId)??throw new GachaCapacityException();
        var form=formationConfig.Equips.SingleOrDefault(x=>x.EquipId==equipId&&!x.IsDeleted)??throw new GachaCapacityException();
        var master=completeTutorialGrowth.Equip(equipId);
        if(form.EquipType!=type||form.EquipGroupId!=equip.EquipGroupId||GrowthCatalog.I(master,"Rarity")!=1||GrowthCatalog.I(master,"EquipType")!=type||GrowthCatalog.I(master,"EquipGroupId")!=equip.EquipGroupId)throw new GachaCapacityException();
        var marker=Hash("phone-tutorial-main-weapon-supply-v1:"+account);
        if(FindEconomyReceipt(c,t,account,marker)is not null)throw new GachaCapacityException();
        EnsureEquipmentCapacity(c,t,account,[(equip,1)],grantContext);
        _=GrantEquip(c,t,account,now,equip,grantContext);
        using var last=Command(c,t,"SELECT last_insert_rowid()");var instance=(long)last.ExecuteScalar()!;
        InsertEconomyReceipt(c,t,account,marker,"system/tutorial-main-weapon-supply-v1","normal1200:card"+cardId+":equip"+equipId,CompleteTutorialCatalog.Fingerprint,
            JsonSerializer.Serialize(new{source="local-teaching-main-weapon-fallback-v1",trigger_route="tutorial/gacha_result",step_before=1200,target_card_id=cardId,required_equip_type=type,
                actual_grant=new{item_type=7,item_id=equipId,item_qty=1,user_equip_id=instance},original_ten_draw_result_unchanged=true,original_server_rule_claim=false}),"{}",now);
    }
}
