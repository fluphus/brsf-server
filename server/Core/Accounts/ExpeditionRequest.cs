using System.Text.Json;
namespace Brs.Local.Accounts;
public sealed record ExpeditionRequest(string Route,string RetryToken,string? AuthToken,int SlotNo,int ExpeditionId,int[] CardIds,int[] SlotNos)
{
    public EquipmentGrantOptions EquipmentOptions { get; init; }

 public static readonly string[] Routes=["expedition/start","expedition/stop","expedition/skip","expedition/result","expedition/restart"];
 public string Semantic=>MainlineFields.Semantic(Route,Route switch{"expedition/start"=>(object)new{slot_no=SlotNo,expedition_id=ExpeditionId,card_ids=CardIds},"expedition/restart"=>SlotNos.Order().ToArray(),_=>SlotNo});
 public static ExpeditionRequest Read(string route,JsonElement root)
 {
  if(!Routes.Contains(route,StringComparer.Ordinal))throw new InvalidDataException("Unknown expedition route.");
  var common=MainlineFields.Read(root,route=="expedition/start"?["slot_no","expedition_id","card_ids"]:route=="expedition/restart"?["slot_nos"]:["slot_no"]);
  var slots=route=="expedition/restart"?VipRequestFields.IntArray(root,"slot_nos",3,nonempty:true):Array.Empty<int>();var slot=route=="expedition/restart"?0:MainlineFields.Int(root,"slot_no");
  if(route=="expedition/restart"?slots.Any(n=>n is <1 or >3):slot is <1 or >3)throw new InvalidDataException("Invalid expedition slot.");
  var destination=0;var cards=Array.Empty<int>();
  if(route=="expedition/start")
  {
   destination=MainlineFields.Int(root,"expedition_id");var a=root.GetProperty("card_ids");
   if(destination<=0||a.ValueKind!=JsonValueKind.Array||a.GetArrayLength()!=3)throw new InvalidDataException("Invalid expedition roster shape.");
   cards=a.EnumerateArray().Select(v=>v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out var n)&&n>=0?n:throw new InvalidDataException("Invalid expedition card.")).ToArray();
   if(!ValidCards(cards))throw new InvalidDataException("Invalid expedition roster.");
  }
  return new(route,MainlineFields.Retry(common),MainlineFields.Auth(common),slot,destination,cards,slots) { EquipmentOptions = EquipmentGrantOptions.Read(Brs.Local.Transport.RequestEnvelope.ReadCommon(root)) };
 }
 internal static bool ValidCards(int[] a)=>a.Length==3&&a.All(x=>x>=0)&&a.Any(x=>x>0)&&a.Where(x=>x>0).Distinct().Count()==a.Count(x=>x>0);
}
