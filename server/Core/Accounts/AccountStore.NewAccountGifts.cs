using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed partial class AccountStore
{
 // An entitlement audit entry, with no ownership/currency grant. Only new
 // account allocation calls this in its creation transaction; no clock cutoff.
 private static void EnrollNewAccountGifts(SqliteConnection c,SqliteTransaction t,long account,byte[] nonceHash,long now)
 {
  var policy=NewAccountGiftPolicy.Fingerprint;
  InsertEconomyReceipt(c,t,account,Hash("local-new-account-gifts-enrollment:"+account+":"+policy),
   "system/new-account-gifts-enrollment","pending65:"+policy,policy,
   JsonSerializer.Serialize(new{source="local-new-account-gifts65-enrollment-v1",account_id=account,
    creation_nonce_hash=Convert.ToHexStringLower(nonceHash),policy_hash=policy,no_reward_granted=true}),"{}",now);
 }
 // Called only by authenticated Home after tutorial completion, in its immediate TX.
 // False must return before Snapshot/Commit so the entire Home transaction rolls back.
 private bool TryGrantNewAccountGifts(SqliteConnection c,SqliteTransaction t,long account,long now)
 {
  try
  {
   var policy=NewAccountGiftPolicy.Fingerprint;
   var hash=Hash("local-new-account-gifts:"+account+":"+policy);
   var prior=FindEconomyReceipt(c,t,account,hash);
   if(prior is not null)return prior.Route=="system/new-account-gifts"&&prior.Semantic=="gifts65:"+policy;
   var enrollment=FindEconomyReceipt(c,t,account,Hash("local-new-account-gifts-enrollment:"+account+":"+policy));
   // Preserve old accounts which never received this explicitly enrolled gift.
   if(enrollment is null)return true;
   if(enrollment.Route!="system/new-account-gifts-enrollment"||enrollment.Semantic!="pending65:"+policy)return false;
   if(presentCatalog is null||cardSkinConfig is null||activityConfig is null)return false;
   byte[] nonceHash;
   using(var creation=Command(c,t,"SELECT nonce_hash FROM creation_receipts WHERE account_id=$a"))
   {
    creation.Parameters.AddWithValue("$a",account);
    if(creation.ExecuteScalar()is not byte[] bytes||bytes.Length!=32)return false;
    nonceHash=bytes;
   }
   // This card has no gacha producer. The existing complete present master holds all76 cards.
   var card=presentCatalog.Definition(5,NewAccountGiftPolicy.CardId).Card;
   if(card is null||card.DefaultSkinId!=NewAccountGiftPolicy.DefaultSkinId||
    cardSkinConfig.Cards.SingleOrDefault(v=>v.CardId==card.CardId)is not{IsDeleted:false} mapping||mapping.DefaultSkinId!=card.DefaultSkinId||
    NewAccountGiftPolicy.SkinIds.Any(id=>!cardSkinConfig.CanUseSkin(card.CardId,id,true)||id==mapping.DefaultSkinId)||
    NewAccountGiftPolicy.BackgroundIds.Any(id=>HomeBackgroundCatalog.Find(id)is not{IsDefault:false,IsDeleted:false})||
    NewAccountGiftPolicy.TitleIds.Length!=58||NewAccountGiftPolicy.TitleIds.Distinct().Count()!=58||
    NewAccountGiftPolicy.TitleIds.Any(id=>id<=0||!activityConfig.TitleIds.Contains(id)))return false;
   bool missingCard;
   using(var q=Command(c,t,"SELECT NOT EXISTS(SELECT 1 FROM owned_cards WHERE account_id=$a AND card_id=$card)"))
   {
    q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$card",card.CardId);
    missingCard=(long)q.ExecuteScalar()! ==1;
   }
   var skins=Owned("SELECT card_skin_id FROM owned_card_skins WHERE account_id=$a AND card_id="+card.CardId);
   var backgrounds=Owned("SELECT background_id FROM owned_backgrounds WHERE account_id=$a");
   var titles=Owned("SELECT title_id FROM owned_titles WHERE account_id=$a");
   var missingSkins=NewAccountGiftPolicy.SkinIds.Where(id=>!skins.Contains(id)).ToArray();
   var missingBackgrounds=NewAccountGiftPolicy.BackgroundIds.Where(id=>!backgrounds.Contains(id)).ToArray();
   var missingTitles=NewAccountGiftPolicy.TitleIds.Where(id=>!titles.Contains(id)).ToArray();
   if(missingCard)EnsureInstanceCapacity(c,t,"owned_cards",1);
   if(missingTitles.Length>0)
    using(var q=Command(c,t,"SELECT MAX(v)FROM(SELECT COALESCE(MAX(id),0)v FROM owned_titles UNION ALL SELECT COALESCE(MAX(seq),0)FROM sqlite_sequence WHERE name='owned_titles')"))
     if((long)q.ExecuteScalar()!>int.MaxValue-missingTitles.Length)return false;
   if(missingCard)_=GrantCard(c,t,account,card);
   // Existing cards never pass through GrantCard's duplicate-to-material branch.
   foreach(var id in NewAccountGiftPolicy.SkinIds.Prepend(card.DefaultSkinId))
   {
    using var q=Command(c,t,"INSERT INTO owned_card_skins(account_id,card_id,card_skin_id)SELECT $a,$card,$skin WHERE NOT EXISTS(SELECT 1 FROM owned_card_skins WHERE account_id=$a AND card_id=$card AND card_skin_id=$skin)");
    q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$card",card.CardId);q.Parameters.AddWithValue("$skin",id);q.ExecuteNonQuery();
   }
   foreach(var id in missingBackgrounds)
   {
    using var q=Command(c,t,"INSERT INTO owned_backgrounds(account_id,background_id)VALUES($a,$id)");q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$id",id);q.ExecuteNonQuery();
   }
   foreach(var id in missingTitles)
   {
    using var q=Command(c,t,"INSERT INTO owned_titles(account_id,title_id)VALUES($a,$id)");q.Parameters.AddWithValue("$a",account);q.Parameters.AddWithValue("$id",id);q.ExecuteNonQuery();
   }
   InsertEconomyReceipt(c,t,account,hash,"system/new-account-gifts","gifts65:"+policy,policy,
    JsonSerializer.Serialize(new{source=NewAccountGiftPolicy.Version,trigger_route="home",account_id=account,
     creation_nonce_hash=Convert.ToHexStringLower(nonceHash),policy_hash=policy,granted_at=now,
     actual_ownership=new{cards=new[]{card.CardId},card_skins=NewAccountGiftPolicy.SkinIds,derived_default_skin=card.DefaultSkinId,
      backgrounds=NewAccountGiftPolicy.BackgroundIds,titles=NewAccountGiftPolicy.TitleIds},explicit_gift_count=65,
     actual_added=new{cards=missingCard?new[]{card.CardId}:Array.Empty<int>(),card_skins=missingSkins,backgrounds=missingBackgrounds,titles=missingTitles},
     actual_added_count=(missingCard?1:0)+missingSkins.Length+missingBackgrounds.Length+missingTitles.Length,
     existing_ownership_preserved=true,
     no_past_action_or_playback_claim=true}),"{}",now);
   return true;
   HashSet<int> Owned(string sql)
   {using var q=Command(c,t,sql);q.Parameters.AddWithValue("$a",account);using var r=q.ExecuteReader();var ids=new HashSet<int>();while(r.Read())ids.Add(r.GetInt32(0));return ids;}
  }
  catch(GachaCapacityException){return false;}
  catch(OverflowException){return false;}
  catch(SqliteException){return false;}
  catch(InvalidDataException){return false;}
 }
}
