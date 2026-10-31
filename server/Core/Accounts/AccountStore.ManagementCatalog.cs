using System.Globalization;
using System.Text.Json;
namespace Brs.Local.Accounts;
public sealed partial class AccountStore
{
 private string ManagementName(string section,int id,int type=0)=>managementLabels.GetValueOrDefault(type==0?$"{section}:{id}":$"{section}:{type}:{id}")??id.ToString(CultureInfo.InvariantCulture);
 private GrowthCatalog RequireManagementGrowth()=>managementGrowth??throw Reject(503,"catalog_unavailable");
 private object[] ManagementCardCaps(JsonElement card)
 {
  var g=RequireManagementGrowth();var id=GrowthCatalog.I(card,"CardId");var rarity=GrowthCatalog.I(card,"Rarity");
  return Enumerable.Range(0,6).Select(awakening=>{var row=g.One("CardAwakeningMaster",r=>GrowthCatalog.I(r,"Rarity")==rarity&&GrowthCatalog.I(r,"AwakeningCount")==awakening);return(object)new{awakening,maxLevel=g.CardCap(id,awakening),maxMainSkillLevel=GrowthCatalog.I(row,"MaxMainSkillLv"),maxKillAbilityLevel=GrowthCatalog.I(row,"MaxKillAbilityLv"),maxKillZoneSkillLevel=GrowthCatalog.I(row,"MaxKillZoneSkillLv")};}).ToArray();
 }
 private object[] ManagementEquipCaps(JsonElement equip){var g=RequireManagementGrowth();return Enumerable.Range(0,5).Select(b=>(object)new{breakthrough=b,maxLevel=g.EquipCap(GrowthCatalog.I(equip,"Rarity"),b)}).ToArray();}
 public ManagementResult ManagementCatalog()=>ManagementRead((c,t)=>
 {
  var g=RequireManagementGrowth();bool Active(JsonElement r)=>!r.TryGetProperty("IsDeleted",out var d)||!d.GetBoolean();var items=new List<object>();
  foreach(var (table,type,key)in new[]{("ItemConsumeMaster",6,"ItemId"),("ItemEnhanceMaster",12,"ItemId"),("CardMaster",11,"CardId")})foreach(var row in g.Rows(table).Where(Active)){var id=GrowthCatalog.I(row,key);items.Add(new{type,id,name=ManagementName("items",id,type),maxQty=99999999});}
  foreach(var m in inventoryMaterialConfig.Materials)items.Add(new{type=7,id=m.EquipId,name=ManagementName("items",m.EquipId,7),maxQty=99999999});
  foreach(var e in eventQuestConfig?.EventItems??[])items.Add(new{type=14,id=e.ItemId,name=ManagementName("items",e.ItemId,14),maxQty=99999999,eventId=e.EventId});
  // Enumerate the same immutable original list, validating every label metadata key.
  var backgroundIds=new[]{100101,100102,100103,100201,100301,100401,100501,100502,100503,100504,100601,100602,100603,100604,100701,100702,100703,100801,100802,100803,100901,100902,100903,100904,101001,101101,101201,101202,101301,101302,101401,101501,101601,101701,101702,101801};
  return new{
   cards=g.Rows("CardMaster").Where(Active).Select(row=>new{id=GrowthCatalog.I(row,"CardId"),name=ManagementName("cards",GrowthCatalog.I(row,"CardId")),charaId=GrowthCatalog.I(row,"CharaId"),rarity=GrowthCatalog.I(row,"Rarity"),defaultSkinId=GrowthCatalog.I(row,"DefaultCardSkinId"),maxAwakening=5,maxMasteryLevel=mainlineConfig?.MasteryLevels.Max(x=>x.MasteryLv)??5,growth=ManagementCardCaps(row)}).ToArray(),
   equipment=g.Rows("EquipMaster").Where(r=>Active(r)&&inventoryMaterialConfig.Find(GrowthCatalog.I(r,"EquipId"))is null&&GrowthCatalog.I(r,"EquipType")<301).Select(row=>new{id=GrowthCatalog.I(row,"EquipId"),name=ManagementName("equipment",GrowthCatalog.I(row,"EquipId")),rarity=GrowthCatalog.I(row,"Rarity"),type=GrowthCatalog.I(row,"EquipType"),maxBreakthrough=4,growth=ManagementEquipCaps(row)}).ToArray(),items,
   backgrounds=backgroundIds.Select(id=>new{id,name=ManagementName("backgrounds",id),isDefault=HomeBackgroundCatalog.Find(id)!.IsDefault}).ToArray(),
   titles=(activityConfig?.TitleIds??[]).Select(id=>new{id,name=ManagementName("titles",id)}).ToArray(),
   skins=(cardSkinConfig?.Skins??[]).Where(x=>!x.IsDeleted).Select(x=>new{id=x.SkinId,cardId=x.CardId,name=ManagementName("skins",x.SkinId),isDefault=cardSkinConfig!.Cards.Single(y=>y.CardId==x.CardId).DefaultSkinId==x.SkinId}).ToArray(),
   limits=new{maxItemsPerCommand=200,maxEquipment=999,maxItemQuantity=99999999,maxFreeGems=99999999,maxPaidGems=99999999,maxCombinedGems=2147483647,maxGf=999999999,maxFriendPoints=99999999,maxStamina=999}
  };
 });
}
