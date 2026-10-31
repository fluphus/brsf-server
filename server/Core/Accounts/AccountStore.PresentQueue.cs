using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Brs.Local.Accounts;
internal sealed record PresentQueuedIdentity(int Category,int PresentId,string ProducerKey,int ItemType,int ItemId,int ItemQty);
internal sealed record PresentEnqueueResult(PresentQueuedIdentity[] Presents,object[] NotificationItems);

public sealed partial class AccountStore
{
 private PresentCatalog? presentCatalog;
 private sealed class PresentRejectedException:Exception;
 public void ConfigurePresents(PresentCatalog catalog)
 {
  lock(gate)
  {
   if(presentCatalog is not null||vipPassConfig is null||activityConfig is null||allGachaConfig is null||commerceConfig is null)throw new InvalidDataException("Presents require proven VIP and typed inventory catalogs.");
   presentCatalog=catalog;
  }
 }
 private static int NextPresentItemId(SqliteConnection c,SqliteTransaction t)
 {
  using var q=Command(c,t,"SELECT MAX(v)FROM(SELECT COALESCE(MAX(user_present_item_id),0)v FROM vip_present_items UNION ALL SELECT COALESCE(MAX(user_present_item_id),0)FROM present_items UNION ALL SELECT COALESCE(MAX(seq),0)FROM sqlite_sequence WHERE name IN('vip_present_items','present_items'))");
  var max=(long)q.ExecuteScalar()!;if(max>=int.MaxValue)throw new GachaCapacityException();return checked((int)max+1);
 }
 private PresentEnqueueResult EnqueueGeneralPresents(SqliteConnection c,SqliteTransaction t,long account,long now,string producerPrefix,byte[] producerReceiptHash,string producerConfigHash,int receivePlace,int conditionValue,IReadOnlyList<CommerceReward> rewards,int expireTimestamp=0,bool isOverQtyLimit=false)
 {
  if(presentCatalog is null||producerPrefix.Length is<1 or>240||producerReceiptHash.Length!=32||producerConfigHash.Length!=64||receivePlace<=0||conditionValue<0||rewards.Count==0||expireTimestamp!=0&&expireTimestamp<=now)throw new InvalidDataException("Invalid authenticated present producer.");
  var ids=new List<PresentQueuedIdentity>();var notifications=new List<object>();
  for(var i=0;i<rewards.Count;i++)
  {
   var reward=rewards[i];var frozen=presentCatalog.Freeze(reward);var json=JsonSerializer.Serialize(frozen,PresentCatalog.Json);var payloadHash=PresentCatalog.Hash(json);var key=producerPrefix+":"+i;var category=reward.ItemType==7?1:0;
   using(var old=Command(c,t,"SELECT 0,user_present_item_id,producer_config_hash,frozen_reward_json,receive_place,condition_value,is_over_qty_limit,expire_timestamp,source_retry_hash FROM present_items WHERE account_id=$a AND producer_key=$k UNION ALL SELECT 1,user_present_equip_id,producer_config_hash,frozen_reward_json,receive_place,condition_value,is_over_qty_limit,expire_timestamp,source_retry_hash FROM present_equips WHERE account_id=$a AND producer_key=$k"))
   {
    old.Parameters.AddWithValue("$a",account);old.Parameters.AddWithValue("$k",key);using var reader=old.ExecuteReader();
    if(reader.Read())
    {
     if(reader.GetInt32(0)!=category||reader.GetString(2)!=producerConfigHash||reader.GetString(3)!=json||reader.GetInt32(4)!=receivePlace||reader.GetInt32(5)!=conditionValue||reader.GetInt32(6)!=(isOverQtyLimit?1:0)||reader.GetInt32(7)!=expireTimestamp||!((byte[])reader.GetValue(8)).SequenceEqual(producerReceiptHash))throw new InvalidDataException("Present producer identity changed payload.");
     var oldId=reader.GetInt32(1);if(reader.Read())throw new InvalidDataException("Duplicate producer across present categories.");ids.Add(new(category,oldId,key,reward.ItemType,reward.ItemId,reward.ItemQty));notifications.Add(new{item_type=reward.ItemType,item_id=reward.ItemId,item_qty=reward.ItemQty});continue;
    }
   }
   var table=category==0?"present_items":"present_equips";var pk=category==0?"user_present_item_id":"user_present_equip_id";int id;
   if(category==0)id=NextPresentItemId(c,t);
   else
   {
    using var capacity=Command(c,t,"SELECT MAX(v)FROM(SELECT COALESCE(MAX(user_present_equip_id),0)v FROM present_equips UNION ALL SELECT COALESCE(MAX(seq),0)FROM sqlite_sequence WHERE name='present_equips')");if((long)capacity.ExecuteScalar()!>=int.MaxValue)throw new GachaCapacityException();id=0;
   }
   using var insert=Command(c,t,category==0?
    "INSERT INTO present_items(user_present_item_id,account_id,item_type,item_id,item_qty,receive_place,condition_value,is_over_qty_limit,expire_timestamp,created_at,state,terminal_at,producer_key,producer_config_hash,frozen_reward_json,payload_hash,source_retry_hash)VALUES($id,$a,$type,$item,$qty,$place,$condition,$over,$expire,$now,0,0,$key,$config,$frozen,$payload,$source) RETURNING "+pk:
    "INSERT INTO present_equips(account_id,equip_id,equip_type,rarity,effect_type,qty,receive_place,condition_value,is_over_qty_limit,expire_timestamp,created_at,state,terminal_at,producer_key,producer_config_hash,frozen_reward_json,payload_hash,source_retry_hash)VALUES($a,$item,$etype,$rarity,$effect,$qty,$place,$condition,$over,$expire,$now,0,0,$key,$config,$frozen,$payload,$source) RETURNING "+pk);
   if(category==0){insert.Parameters.AddWithValue("$id",id);insert.Parameters.AddWithValue("$type",reward.ItemType);}
   else{insert.Parameters.AddWithValue("$etype",frozen.Definition.EquipType);insert.Parameters.AddWithValue("$rarity",frozen.Definition.Rarity);insert.Parameters.AddWithValue("$effect",frozen.Definition.EffectType);}
   foreach(var pair in new(string,object)[]{("$a",account),("$item",reward.ItemId),("$qty",reward.ItemQty),("$place",receivePlace),("$condition",conditionValue),("$over",isOverQtyLimit?1:0),("$expire",expireTimestamp),("$now",now),("$key",key),("$config",producerConfigHash),("$frozen",json),("$payload",payloadHash),("$source",producerReceiptHash)})insert.Parameters.AddWithValue(pair.Item1,pair.Item2);
   id=checked((int)(long)insert.ExecuteScalar()!);ids.Add(new(category,id,key,reward.ItemType,reward.ItemId,reward.ItemQty));notifications.Add(new{item_type=reward.ItemType,item_id=reward.ItemId,item_qty=reward.ItemQty});
  }
  return new(ids.ToArray(),notifications.ToArray());
 }
}
