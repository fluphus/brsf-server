using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Brs.Local.Accounts;

// Staged pure original-data engine. No account IO, grants, schema changes or route registration.
public sealed class GrowthCatalog
{
    public const string ExpectedFingerprint = "612dfd3c36fe1904fff4d2aa9f14915fadfc7f8bd82823443a8ebfb08e684207";
    private readonly Dictionary<string, JsonElement[]> tables;
    public string Fingerprint { get; }
    public GrowthCatalog(string path)
    {
        var bytes=File.ReadAllBytes(path);Fingerprint=Convert.ToHexStringLower(SHA256.HashData(bytes));
        if(Fingerprint!=ExpectedFingerprint)throw new InvalidDataException("Growth original-data fingerprint mismatch.");
        using var json=JsonDocument.Parse(bytes);var root=json.RootElement;
        if(root.GetProperty("version").GetInt32()!=1 || root.GetProperty("policy").GetString()!="local-growth-v1")throw new InvalidDataException("Unknown growth policy.");
        tables=root.GetProperty("tables").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.EnumerateArray().Select(x=>x.Clone()).ToArray(),StringComparer.Ordinal);
        if(Rows("CardMaster").Length!=76 || Rows("EquipMaster").Length!=314)throw new InvalidDataException("Incomplete growth catalog.");
    }
    public JsonElement[] Rows(string table)=>tables.TryGetValue(table,out var result)?result:throw new InvalidDataException("Missing growth table.");
    public JsonElement One(string table,Func<JsonElement,bool> predicate)
    {
        var rows=Rows(table).Where(predicate).ToArray();
        return rows.Length==1 && (!rows[0].TryGetProperty("IsDeleted",out var del)||!del.GetBoolean())?rows[0]:throw new GrowthRejectedException();
    }
    public JsonElement Card(int id)=>One("CardMaster",r=>I(r,"CardId")==id);
    public JsonElement Equip(int id)=>One("EquipMaster",r=>I(r,"EquipId")==id);
    public int CardCap(int id,int awakening)=>I(One("CardParamMaster",r=>I(r,"CardId")==id&&I(r,"AwakeningCount")==awakening),"MaxLv");
    public int EquipCap(int rarity,int overlimit)=>I(One("EquipOverLimitMaster",r=>I(r,"Rarity")==rarity&&I(r,"OverLimitCount")==overlimit),"MaxLv");
    public int Threshold(string table,int group,int lv)=>lv==1?0:I(One(table,r=>I(r,"LvGroupId")==group&&I(r,"Lv")==lv),"Exp");
    public int Config(string key)=>int.Parse(One("ConfigMaster",r=>r.GetProperty("ConfigKey").GetString()==key).GetProperty("ConfigValue").GetString()!,CultureInfo.InvariantCulture);
    public static int I(JsonElement row,string field)=>row.GetProperty(field).GetInt32();
}
public sealed class GrowthRejectedException : Exception;
public sealed record GrowthQuantity(int ItemType,int ItemId,int Quantity);
public sealed record GrowthExperience(int Level,int Exp,int BaseXp,int EffectiveXp,int GameCoin,int SuccessType,int OverLimitCount=0);
public sealed record GrowthAwakening(int AwakeningCount,int GameCoin,GrowthQuantity Cost,int UnlockSkinId);
public sealed record GrowthSkill(int SkillType,int Level,int GameCoin,GrowthQuantity[] Costs);
public sealed record GrowthDonor(int EquipId,int Quantity,int OverLimitCount=0,int InstanceId=0);
public sealed record GrowthSale(int GameCoin,int ConsumeItemId,int ConsumeItemQuantity);
public sealed record GrowthProperty(string FreePropertyIds,int GameCoin,int InheritedPropertyId);
public sealed record GrowthCure(int StaminaAdded,int GemCost);

public static class GrowthEngine
{
    public static int SuccessType(int roll)=>roll is>=0 and<6000?1:roll is>=6000 and<8500?2:roll is>=8500 and<10000?3:throw new ArgumentOutOfRangeException(nameof(roll));
    private static int Rate(int success)=>success switch{1=>100,2=>150,3=>200,_=>throw new GrowthRejectedException()};
    private static void Positive(int value){if(value<=0)throw new GrowthRejectedException();}
    private static void Quantities(GrowthQuantity[] items,int type)
    {
        if(items.Length==0||items.Any(x=>x.ItemType!=type||x.ItemId<=0||x.Quantity<=0)||items.Select(x=>x.ItemId).Distinct().Count()!=items.Length)throw new GrowthRejectedException();
    }
    private static (int Level,int Exp,int Effective) Experience(GrowthCatalog c,string table,int group,int oldLv,int oldExp,int cap,int rawXp)
    {
        if(oldLv<1||oldLv>cap||oldExp<c.Threshold(table,group,oldLv)||oldExp>c.Threshold(table,group,cap)||oldLv<cap&&oldExp>=c.Threshold(table,group,oldLv+1))throw new GrowthRejectedException();
        var after=(int)Math.Min(c.Threshold(table,group,cap),checked((long)oldExp+rawXp));
        var lv=oldLv;while(lv<cap&&after>=c.Threshold(table,group,lv+1))lv++;
        return(lv,after,after-oldExp);
    }
    public static GrowthExperience EnhanceCard(GrowthCatalog c,int cardId,int lv,int exp,int awakening,GrowthQuantity[] items,int success)
    {
        Quantities(items,12);var card=c.Card(cardId);var cap=c.CardCap(cardId,awakening);if(lv>=cap)throw new GrowthRejectedException();
        long raw=0;foreach(var item in items){var row=c.One("ItemEnhanceMaster",r=>GrowthCatalog.I(r,"ItemId")==item.ItemId&&GrowthCatalog.I(r,"ItemCategory")==1);raw=checked(raw+(long)GrowthCatalog.I(row,"EffectValue")*item.Quantity);}
        var group=GrowthCatalog.I(card,"LvGroupId");var baseResult=Experience(c,"CardLvMaster",group,lv,exp,cap,checked((int)raw));
        var costRow=c.One("CardEnhanceCostMaster",r=>GrowthCatalog.I(r,"EnhanceCostGroupId")==GrowthCatalog.I(card,"EnhanceCostGroupId")&&GrowthCatalog.I(r,"Lv")==lv);
        var cost=checked(baseResult.Effective*GrowthCatalog.I(costRow,"Cost"));
        var result=Experience(c,"CardLvMaster",group,lv,exp,cap,checked((int)(raw*Rate(success)/100)));
        return new(result.Level,result.Exp,checked((int)raw),result.Effective,cost,success);
    }
    public static GrowthAwakening Awaken(GrowthCatalog c,int cardId,int lv,int awakening,bool common)
    {
        var card=c.Card(cardId);if(awakening is<0 or>=5||lv!=c.CardCap(cardId,awakening))throw new GrowthRejectedException();
        var next=awakening+1;var row=c.One("CardAwakeningMaster",r=>GrowthCatalog.I(r,"Rarity")==GrowthCatalog.I(card,"Rarity")&&GrowthCatalog.I(r,"AwakeningCount")==next);
        var item=common?GrowthCatalog.I(c.One("ItemEnhanceMaster",r=>GrowthCatalog.I(r,"ItemCategory")==5&&GrowthCatalog.I(r,"EffectValue")==GrowthCatalog.I(card,"JobClassId")),"ItemId"):cardId;
        return new(next,GrowthCatalog.I(row,"GameCoin"),new(common?12:11,item,GrowthCatalog.I(row,common?"CommonItemQty":"AwakeningItemQty")),row.GetProperty("IsAwakeningSkin").GetBoolean()?GrowthCatalog.I(card,"AwakeningCardSkinId"):0);
    }
    public static GrowthSkill Skill(GrowthCatalog c,int cardId,int awakening,int skillType,int currentLevel)
    {
        var prefix=skillType switch{1=>"KillAbility",2=>"MainSkill",3=>"KillZoneSkill",_=>throw new GrowthRejectedException()};var card=c.Card(cardId);
        var max=c.One("CardAwakeningMaster",r=>GrowthCatalog.I(r,"Rarity")==GrowthCatalog.I(card,"Rarity")&&GrowthCatalog.I(r,"AwakeningCount")==awakening);
        if(currentLevel<1||currentLevel>=GrowthCatalog.I(max,"Max"+prefix+"Lv"))throw new GrowthRejectedException();
        var group=prefix+"EnhanceGroupId";var row=c.One("Card"+prefix+"LvMaster",r=>GrowthCatalog.I(r,group)==GrowthCatalog.I(card,group)&&GrowthCatalog.I(r,prefix+"Lv")==currentLevel+1);
        var costs=c.Rows("CardItemGroupSpendMaster").Where(r=>GrowthCatalog.I(r,"UsageType")==skillType&&GrowthCatalog.I(r,"ItemGroupSpendId")==GrowthCatalog.I(row,"ItemGroupSpendId")).Select(r=>new GrowthQuantity(GrowthCatalog.I(r,"ItemType"),GrowthCatalog.I(r,"ItemId"),GrowthCatalog.I(r,"ItemQty"))).ToArray();
        Quantities(costs,12);return new(skillType,currentLevel+1,GrowthCatalog.I(row,"GameCoin"),costs);
    }
    public static GrowthExperience EnhanceEquip(GrowthCatalog c,int equipId,int lv,int exp,int overlimit,GrowthDonor[] donors,int success)
    {
        var target=c.Equip(equipId);if(GrowthCatalog.I(target,"EquipType")>=301||overlimit is<0 or>4||donors.Length==0)throw new GrowthRejectedException();
        var instances=new HashSet<int>();var stacks=new HashSet<int>();long xp=0;var delta=0;var qty=0;var rarity=GrowthCatalog.I(target,"Rarity");
        foreach(var d in donors)
        {
            Positive(d.Quantity);qty=checked(qty+d.Quantity);var row=c.Equip(d.EquipId);var type=GrowthCatalog.I(row,"EquipType");
            if(type is 301 or 302){if(d.InstanceId!=0||d.OverLimitCount!=0||!stacks.Add(d.EquipId))throw new GrowthRejectedException();}
            else if(d.InstanceId<=0||d.Quantity!=1||d.OverLimitCount is<0 or>4||!instances.Add(d.InstanceId))throw new GrowthRejectedException();
            var xpRow=c.One("EquipEnhanceExpMaster",r=>GrowthCatalog.I(r,"Rarity")==GrowthCatalog.I(row,"Rarity")&&r.GetProperty("IsExclusive").GetBoolean()==(type==301));
            xp=checked(xp+(long)GrowthCatalog.I(xpRow,"Exp")*d.Quantity);
            if(overlimit<4 && (type==302&&GrowthCatalog.I(row,"Rarity")>=rarity||type<301&&GrowthCatalog.I(row,"EquipGroupId")==GrowthCatalog.I(target,"EquipGroupId")))delta=checked(delta+(type==302?d.Quantity:d.OverLimitCount+1));
        }
        if(qty>c.Config("EQUIP_ENHANCE_MATERIAL_LIMIT"))throw new GrowthRejectedException();
        var afterLimit=Math.Min(c.Config("EQUIP_MAX_OVER_LIMIT_COUNT"),overlimit+delta);var cap=c.EquipCap(rarity,afterLimit);if(lv==cap&&afterLimit==overlimit)throw new GrowthRejectedException();
        var group=GrowthCatalog.I(target,"LvGroupId");var baseResult=Experience(c,"EquipLvMaster",group,lv,exp,cap,checked((int)xp));var cost=0;var remain=baseResult.Effective;var at=exp;var level=lv;
        while(remain>0&&level<cap)
        {
            var room=c.Threshold("EquipLvMaster",group,level+1)-at;var take=Math.Min(remain,room);
            var row=c.One("EquipEnhanceCostMaster",r=>GrowthCatalog.I(r,"EnhanceCostGroupId")==GrowthCatalog.I(target,"EnhanceCostGroupId")&&GrowthCatalog.I(r,"Lv")==level);
            cost=checked(cost+take*GrowthCatalog.I(row,"Cost"));at+=take;remain-=take;if(at>=c.Threshold("EquipLvMaster",group,level+1))level++;
        }
        var result=Experience(c,"EquipLvMaster",group,lv,exp,cap,checked((int)(xp*Rate(success)/100)));
        return new(result.Level,result.Exp,checked((int)xp),result.Effective,cost,success,afterLimit);
    }
    public static GrowthSale Sale(GrowthCatalog c,GrowthDonor[] donors)
    {
        if(donors.Length==0)throw new GrowthRejectedException();var qty=0;long gf=0,items=0;var instances=new HashSet<int>();var stacks=new HashSet<int>();
        foreach(var d in donors)
        {
            Positive(d.Quantity);var row=c.Equip(d.EquipId);var material=GrowthCatalog.I(row,"EquipType")is 301 or 302;
            if(material){if(d.InstanceId!=0||d.OverLimitCount!=0||!stacks.Add(d.EquipId))throw new GrowthRejectedException();}
            else if(d.Quantity!=1||d.InstanceId<=0||d.OverLimitCount is<0 or>4||!instances.Add(d.InstanceId))throw new GrowthRejectedException();
            qty=checked(qty+d.Quantity);var multiplier=material?d.Quantity:d.OverLimitCount+1;
            gf=checked(gf+(long)GrowthCatalog.I(row,"BaseSalePrice")*multiplier);items=checked(items+(long)GrowthCatalog.I(row,"SaleItemQty")*multiplier);
        }
        if(qty>c.Config("EQUIP_SALE_LIMIT"))throw new GrowthRejectedException();return new(checked((int)gf),c.Config("EQUIP_SALE_GET_ITEM_ID"),checked((int)items));
    }
    public static GrowthProperty Property(GrowthCatalog c,int targetEquip,int donorEquip,string targetProperties,string donorProperties,int slot,int donorSlot)
    {
        var target=c.Equip(targetEquip);var donor=c.Equip(donorEquip);var type=GrowthCatalog.I(target,"EquipType");
        if(type>=301||type!=GrowthCatalog.I(donor,"EquipType"))throw new GrowthRejectedException();
        int[] Parse(string value,int count)
        {
            var a=new List<int>();
            if(value.Length>0)foreach(var part in value.Split(','))
            {if(!int.TryParse(part,NumberStyles.None,CultureInfo.InvariantCulture,out var item)||item<0)throw new GrowthRejectedException();a.Add(item);}
            if(a.Count>count)throw new GrowthRejectedException();while(a.Count<count)a.Add(0);return a.ToArray();
        }
        var a=Parse(targetProperties,GrowthCatalog.I(target,"FreePropertySlotCount"));var b=Parse(donorProperties,GrowthCatalog.I(donor,"FreePropertySlotCount"));
        if(slot<1||slot>a.Length||donorSlot<1||donorSlot>b.Length||b[donorSlot-1]<=0||a[slot-1]==b[donorSlot-1])throw new GrowthRejectedException();
        var property=b[donorSlot-1];_ =c.One("EquipFreePropertyMaster",r=>GrowthCatalog.I(r,"FreePropertyId")==property);a[slot-1]=property;
        return new(string.Join(',',a),GrowthCatalog.I(target,"FreePropertyGrantCost"),property);
    }
    public static GrowthCure Cure(GrowthCatalog c,int current,int max,int gemRefills,GrowthQuantity[] foods,long now)
    {
        if(current<0||max<1||gemRefills<0||gemRefills==0&&foods.Length==0)throw new GrowthRejectedException();
        if(foods.Length>0)Quantities(foods,6);long added=checked((long)gemRefills*max);
        foreach(var food in foods)
        {
            var row=c.One("ItemConsumeMaster",r=>GrowthCatalog.I(r,"ItemId")==food.ItemId);var category=GrowthCatalog.I(row,"ItemCategory");if(category is not(3 or 4))throw new GrowthRejectedException();
            var end=row.GetProperty("EndDate").GetString();
            if(!string.IsNullOrEmpty(end))
            {
                if(!DateTime.TryParse(end,CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))throw new InvalidDataException("Unknown original food expiry.");
                var stamp=new DateTimeOffset(DateTime.SpecifyKind(date,DateTimeKind.Unspecified),TimeSpan.FromHours(9)).ToUnixTimeSeconds();if(now>stamp)throw new GrowthRejectedException();
            }
            var value=GrowthCatalog.I(row,"EffectValue");var one=category==3?value:checked((int)(((long)max*value+99)/100));added=checked(added+(long)one*food.Quantity);
        }
        if(added<=0||current+added>c.Config("MAX_STAMINA_HOLDING"))throw new GrowthRejectedException();
        return new(checked((int)added),checked(gemRefills*c.Config("STAMINA_CURE_FREE_PAID_COIN_COUNT")));
    }
}
