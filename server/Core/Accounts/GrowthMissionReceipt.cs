using System.Text.Json;

namespace Brs.Local.Accounts;

internal sealed record GrowthReceiptFact(long Time,string Route,int CardId,int EquipId,int AwakeningDelta,int OverLimitDelta,int Operations,CommerceReward[] Grants)
{
    internal static GrowthReceiptFact Read(long time,string route,JsonElement result)
    {
        if(result.GetProperty("source").GetString()!="local-growth-v1"||result.GetProperty("route").GetString()!=route||!GrowthRequest.Routes.Contains(route,StringComparer.Ordinal))throw new InvalidDataException("Unknown growth receipt source.");
        var a=result.GetProperty("audit");int I(string key)=>a.GetProperty(key).GetInt32();
        var card=I("card_id");var equip=I("equip_id");var awaken=I("awakening_delta");var limit=I("over_limit_delta");var operations=I("operation_count");
        if(operations!=1||a.GetProperty("selected_unit_count").GetInt64()<=0||awaken!=I("awakening_after")-I("awakening_before")||limit!=I("over_limit_after")-I("over_limit_before")||awaken is<0 or>1||limit is<0 or>4)throw new InvalidDataException("Invalid typed growth deltas.");
        if((route=="card/awakening"&&(card<=0||awaken!=1))||(route!="card/awakening"&&awaken!=0)||(route!="equip/enhance"&&limit!=0)||
            (route is "equip/enhance" or "equip/grant_property"&&equip<=0))throw new InvalidDataException("Growth route/delta mismatch.");
        var grants=JsonSerializer.Deserialize<CommerceReward[]>(a.GetProperty("typed_grants"),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException("Missing typed growth grants.");
        if(grants.Any(x=>route!="equip/sale"||x.ItemType!=6||x.ItemId!=50001||x.ItemQty<=0))throw new InvalidDataException("Unknown growth reward source.");
        return new(time,route,card,equip,awaken,limit,operations,grants);
    }
}
