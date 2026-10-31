using System.Text.Json;
namespace Brs.Local.Accounts;
public sealed record PresentRequest(string Route,string? Auth,string Retry,int[] Ids,int[] ItemTypes,int[] Rarities,int[] EquipTypes,int[] Skills,int Expiration,int Sort,int Order,int Page)
{
    public EquipmentGrantOptions EquipmentOptions { get; init; }

 public static readonly string[] Routes=["present/item/list","present/item/receive","present/equip/list","present/equip/receive","present/equip/sell","present/history"];
 public bool IsRead=>Route is "present/item/list" or "present/equip/list" or "present/history";
 public int Category=>Route.StartsWith("present/equip/",StringComparison.Ordinal)?1:0;
 public string Semantic=>Route==VipPresentReceiveRequest.Route?new VipPresentReceiveRequest(Retry,Auth,Ids).Semantic:MainlineFields.Semantic(Route,Ids.Order().ToArray());
 public static PresentRequest Read(string route,JsonElement root)
 {
  if(!Routes.Contains(route,StringComparer.Ordinal))throw new InvalidDataException("Unknown present route.");
  if(route=="present/history")return new(route,EconomyRequestFields.Auth(EconomyRequestFields.Read(root)),"",[],[],[],[],[],0,1,1,1) { EquipmentOptions = EquipmentGrantOptions.Read(Brs.Local.Transport.RequestEnvelope.ReadCommon(root)) };
  if(route is "present/item/list" or "present/equip/list")
  {
   JsonElement common;int[] types=[],rarity=[],equip=[],skills=[];
   if(route=="present/item/list")
   {
    var v=VipPresentListRequest.Read(root);types=v.ItemTypes;common=EconomyRequestFields.Read(root,"filter_item_types","filter_receive_expiration","sort_type","sort_order_type","page_num","is_need_unread_present_qty","is_need_warning_receive_present");
   }
   else
   {
    _=PresentEquipListRequest.Read(root);common=EconomyRequestFields.Read(root,"filter_rarities","filter_equip_types","filter_equip_skill_types","filter_receive_expiration","sort_order_type","sort_type","page_num","is_need_unread_present_qty","is_need_warning_receive_present");rarity=VipRequestFields.IntArray(root,"filter_rarities",5);equip=VipRequestFields.IntArray(root,"filter_equip_types",8);skills=VipRequestFields.IntArray(root,"filter_equip_skill_types",5);
   }
   var page=VipRequestFields.Int(root,"page_num");if(page is<1 or>20)throw new InvalidDataException("Present display page outside original1000 limit.");
   return new(route,EconomyRequestFields.Auth(common),"",[],types,rarity,equip,skills,VipRequestFields.Int(root,"filter_receive_expiration"),VipRequestFields.Int(root,"sort_type"),VipRequestFields.Int(root,"sort_order_type"),page) { EquipmentOptions = EquipmentGrantOptions.Read(Brs.Local.Transport.RequestEnvelope.ReadCommon(root)) };
  }
  var field=route switch{"present/item/receive"=>"receive_user_present_item_ids","present/equip/receive"=>"receive_user_present_equip_ids",_=>"sell_user_present_equip_ids"};
  var c=EconomyRequestFields.Read(root,field);var ids=VipRequestFields.IntArray(root,field,route=="present/equip/sell"?20:50,nonempty:true);if(ids.Any(x=>x<=0))throw new InvalidDataException("Invalid present ID.");
  return new(route,EconomyRequestFields.Auth(c),EconomyRequestFields.Retry(c),ids,[],[],[],[],0,1,1,1) { EquipmentOptions = EquipmentGrantOptions.Read(Brs.Local.Transport.RequestEnvelope.ReadCommon(root)) };
 }
}
