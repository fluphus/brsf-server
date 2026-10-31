using System.Security.Cryptography;
using System.Text.Json;
namespace Brs.Local.Accounts;

public sealed record PresentDefinition(int ItemType,int ItemId,int Rarity,int EquipType,int EffectType,GachaCard? Card,GachaEquip? Equip,int SaleGameCoin,int SaleItemQty);
internal sealed record PresentFrozen(string PolicyVersion,CommerceReward Reward,PresentDefinition Definition);
public sealed class PresentCatalog
{
 public const string ExpectedFingerprint="1ff0e3d98870f8b7b1883d1bf349b9eb2424107955fd769949696ec52b92bcaa";
 internal static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,PropertyNameCaseInsensitive=true};
 private readonly Dictionary<string,JsonElement[]> tables;
 public PresentCatalog(string path)
 {
  var bytes=File.ReadAllBytes(path);if(Convert.ToHexStringLower(SHA256.HashData(bytes))!=ExpectedFingerprint)throw new InvalidDataException("Present catalog fingerprint mismatch.");
  using var d=JsonDocument.Parse(bytes);var r=d.RootElement;
  if(r.GetProperty("policy_version").GetString()!="local-general-present-v1"||r.GetProperty("ordinary_equip_capacity").GetInt32()!=999||r.GetProperty("warning_seconds").GetInt32()!=86400||r.GetProperty("history_days").GetInt32()!=7||r.GetProperty("history_max").GetInt32()!=100||r.GetProperty("filtered_display_max").GetInt32()!=1000)throw new InvalidDataException("Unknown present policy.");
  tables=r.GetProperty("tables").EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.EnumerateArray().Select(a=>a.Clone()).ToArray());
  if(tables["CardMaster"].Length!=76||tables["EquipMaster"].Length!=314||tables["TitleMaster"].Length!=191)throw new InvalidDataException("Incomplete present master.");
 }
 private JsonElement One(string table,string key,int id)
 {
  var a=tables[table].Where(r=>r.GetProperty(key).GetInt32()==id&&!r.GetProperty("IsDeleted").GetBoolean()).ToArray();
  if(a.Length!=1)throw new InvalidDataException("Unknown original present reference.");return a[0];
 }
 internal PresentDefinition Definition(int type,int id)
 {
  switch(type)
  {
   case 2:case 3:case 4:
    if(id!=0)throw new InvalidDataException("Invalid present currency identity.");return new(type,id,0,0,0,null,null,0,0);
   case 5:case 11:
    var c=One("CardMaster","CardId",id);return new(type,id,c.GetProperty("Rarity").GetInt32(),0,0,new(id,c.GetProperty("CharaId").GetInt32(),c.GetProperty("DefaultCardSkinId").GetInt32()),null,0,0);
   case 6:case 12:
    var a=One(type==6?"ItemConsumeMaster":"ItemEnhanceMaster","ItemId",id);return new(type,id,a.GetProperty("Rarity").GetInt32(),0,0,null,null,0,0);
   case 10:
    _=One("TitleMaster","TitleId",id);return new(type,id,0,0,0,null,null,0,0);
   case 7:
    var e=One("EquipMaster","EquipId",id);int I(string n)=>e.GetProperty(n).GetInt32();var skill=I("EquipSkillId");var effect=skill==0?0:One("EquipSkillMaster","EquipSkillId",skill).GetProperty("EquipSkillType").GetInt32();
    return new(type,id,I("Rarity"),I("EquipType"),effect,null,new(id,I("EquipGroupId"),e.GetProperty("FreePropertyIds").GetString()!,I("FreePropertySlotCount")),I("BaseSalePrice"),I("SaleItemQty"));
   default:throw new InvalidDataException("Present type has no approved producer.");
  }
 }
 internal PresentFrozen Freeze(CommerceReward reward)
 {
  if(reward.ItemQty<=0)throw new InvalidDataException("Invalid present quantity.");var d=Definition(reward.ItemType,reward.ItemId);
  if(reward.ItemType==10&&reward.ItemQty!=1)throw new InvalidDataException("Unique title present quantity must be one.");
  return new("local-general-present-v1",reward,d);
 }
 internal static string Hash(string value)=>Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
