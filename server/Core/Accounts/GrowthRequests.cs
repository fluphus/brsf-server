using System.Text.Json;

namespace Brs.Local.Accounts;

public sealed record GrowthLockChange(int UserEquipId,int IsLock);
public sealed class GrowthRequest
{
    public static readonly string[] Routes=["card/enhance","card/awakening","card/skill/enhance","equip/enhance","equip/lock","equip/sale","equip/grant_property","item/cure_stamina"];
    public string Route {get;}
    public string RetryToken {get;}
    public string? AuthToken {get;}
    public int CardId {get;private init;}
    public int UserEquipId {get;private init;}
    public int SkillType {get;private init;}
    public bool IsCommonItem {get;private init;}
    public int TotalPower {get;private init;}
    public int SlotNo {get;private init;}
    public int MaterialUserEquipId {get;private init;}
    public int MaterialSlotNo {get;private init;}
    public int GemRefills {get;private init;}
    public int[] EquipIds {get;private init;}=[];
    public GrowthQuantity[] Items {get;private init;}=[];
    public GrowthLockChange[] Changes {get;private init;}=[];
    private GrowthRequest(string route,string retry,string? auth){Route=route;RetryToken=retry;AuthToken=auth;}
    public string Semantic=>FormationRequestFields.Semantic(Route,new{CardId,UserEquipId,SkillType,IsCommonItem,TotalPower,SlotNo,MaterialUserEquipId,MaterialSlotNo,GemRefills,
        EquipIds=EquipIds.Order().ToArray(),Items=Items.OrderBy(x=>x.ItemType).ThenBy(x=>x.ItemId).ToArray(),Changes=Changes.OrderBy(x=>x.UserEquipId).ToArray()});
    public static GrowthRequest Read(string route,JsonElement root)
    {
        string[] names=route switch
        {
            "card/enhance"=>["card_id","items"],"card/awakening"=>["card_id","is_common_item","total_power"],
            "card/skill/enhance"=>["card_id","skill_type","total_power"],"equip/enhance"=>["user_equip_id","material_user_equip_ids","material_items"],
            "equip/lock"=>["changes"],"equip/sale"=>["user_equip_ids","material_items"],
            "equip/grant_property"=>["user_equip_id","slot_no","material_user_equip_id","material_slot_no","total_power"],
            "item/cure_stamina"=>["free_paid_coin_cure_count","items"],_=>throw new InvalidDataException("Unknown growth route.")
        };
        var common=EconomyRequestFields.Read(root,names);var retry=EconomyRequestFields.Retry(common);var auth=EconomyRequestFields.Auth(common);
        int I(string name,int min=0){var n=VipRequestFields.Int(root,name);if(n<min)throw new InvalidDataException("Invalid growth integer.");return n;}
        GrowthQuantity[] Items(string name,int type,int max)
        {
            var list=root.GetProperty(name);if(list.ValueKind!=JsonValueKind.Array||list.GetArrayLength()>max)throw new InvalidDataException("Invalid growth item list.");
            var result=new List<GrowthQuantity>();var seen=new HashSet<int>();
            foreach(var item in list.EnumerateArray())
            {
                FormationRequestFields.Fields(item,"item_type","item_id","item_qty");var ty=VipRequestFields.Int(item,"item_type");var id=VipRequestFields.Int(item,"item_id");var qty=VipRequestFields.Int(item,"item_qty");
                if(ty!=type||id<=0||qty<=0||!seen.Add(id))throw new InvalidDataException("Invalid or duplicate growth item.");result.Add(new(ty,id,qty));
            }
            return result.ToArray();
        }
        int[] Ids(string name){var ids=VipRequestFields.IntArray(root,name,20);if(ids.Any(x=>x<=0))throw new InvalidDataException("Invalid growth equipment ID.");return ids;}
        GrowthRequest request;
        switch(route)
        {
            case "card/enhance":request=new(route,retry,auth){CardId=I("card_id",1),Items=Items("items",12,51)};if(request.Items.Length==0)throw new InvalidDataException("Empty card enhancement.");break;
            case "card/awakening":request=new(route,retry,auth){CardId=I("card_id",1),IsCommonItem=VipRequestFields.Bool(root,"is_common_item"),TotalPower=I("total_power")};break;
            case "card/skill/enhance":request=new(route,retry,auth){CardId=I("card_id",1),SkillType=I("skill_type",1),TotalPower=I("total_power")};if(request.SkillType>3)throw new InvalidDataException("Unsupported skill enum.");break;
            case "equip/enhance":case "equip/sale":
                request=new(route,retry,auth){UserEquipId=route=="equip/enhance"?I("user_equip_id",1):0,EquipIds=Ids(route=="equip/enhance"?"material_user_equip_ids":"user_equip_ids"),Items=Items("material_items",7,8)};
                var qty=request.EquipIds.Length+request.Items.Sum(x=>(long)x.Quantity);
                if(qty is<1 or>20||request.EquipIds.Contains(request.UserEquipId))throw new InvalidDataException("Invalid growth donor selection.");break;
            case "equip/lock":
                var changes=root.GetProperty("changes");if(changes.ValueKind!=JsonValueKind.Array||changes.GetArrayLength() is<1 or>1000)throw new InvalidDataException("Invalid lock changes.");
                var locks=new List<GrowthLockChange>();var seen=new HashSet<int>();
                foreach(var item in changes.EnumerateArray())
                {
                    FormationRequestFields.Fields(item,"user_equip_id","is_lock");var id=VipRequestFields.Int(item,"user_equip_id");var state=VipRequestFields.Int(item,"is_lock");
                    if(id<=0||state is not(0 or 1)||!seen.Add(id))throw new InvalidDataException("Invalid lock integer or repeated equipment.");locks.Add(new(id,state));
                }
                request=new(route,retry,auth){Changes=locks.ToArray()};break;
            case "equip/grant_property":
                request=new(route,retry,auth){UserEquipId=I("user_equip_id",1),SlotNo=I("slot_no",1),MaterialUserEquipId=I("material_user_equip_id",1),MaterialSlotNo=I("material_slot_no",1),TotalPower=I("total_power")};
                if(request.UserEquipId==request.MaterialUserEquipId)throw new InvalidDataException("Same property base and donor.");break;
            case "item/cure_stamina":
                request=new(route,retry,auth){GemRefills=I("free_paid_coin_cure_count"),Items=Items("items",6,67)};
                if(request.GemRefills==0&&request.Items.Length==0)throw new InvalidDataException("Empty AP recovery.");break;
            default:throw new InvalidDataException("Unknown growth route.");
        }
        return request;
    }
}
