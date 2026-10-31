using System.Security.Cryptography;
using System.Text.Json;
namespace Brs.Local.Accounts;
public sealed class CompleteTutorialCatalog
{
 public const string Fingerprint="fc652676faeb0104f31679a5ae1daef85596494cf4fa252a2084717211b9d481";
 private readonly Dictionary<string,JsonElement[]> tables;
 public CompleteTutorialCatalog(string path)
 {
  var bytes=File.ReadAllBytes(path);if(bytes.Length>200000||Convert.ToHexStringLower(SHA256.HashData(bytes))!=Fingerprint)throw new InvalidDataException("Tutorial catalog differs.");
  using var d=JsonDocument.Parse(bytes);var r=d.RootElement;if(r.GetProperty("policy_version").GetString()!="local-complete-tutorial-v1"||r.GetProperty("steps").GetArrayLength()!=26)throw new InvalidDataException("Unknown tutorial policy.");
  tables=r.GetProperty("tables").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.EnumerateArray().Select(x=>x.Clone()).ToArray());
  if(tables["QuestMaster"].Length!=2||tables["CardMaster"].Length!=3||tables["EquipMaster"].Length!=1)throw new InvalidDataException("Tutorial closure differs.");
 }
 internal JsonElement Quest(int id)=>tables["QuestMaster"].Single(r=>I(r,"QuestId")==id);
 internal GachaCard[] Cards=>tables["CardMaster"].Select(r=>new GachaCard(I(r,"CardId"),I(r,"CharaId"),I(r,"DefaultCardSkinId"))).ToArray();
 internal GachaEquip Equip {get{var r=tables["EquipMaster"].Single();return new(I(r,"EquipId"),I(r,"EquipGroupId"),r.GetProperty("FreePropertyIds").GetString()!,I(r,"FreePropertySlotCount"));}}
 internal MainlineConfig RewardConfig(MainlineConfig original)=>original with{Equips=original.Equips.Where(e=>e.EquipId!=Equip.EquipId).Append(Equip).ToArray(),EnhanceIds=original.EnhanceIds.Append(10001).Distinct().ToArray()};
 internal static int I(JsonElement r,string key)=>r.GetProperty(key).GetInt32();
}
