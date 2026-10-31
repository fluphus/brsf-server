using System.Globalization;
using System.Text.Json;

namespace Brs.Local.Accounts;

public sealed record StoryReadRequest(string RetryToken,string? AuthToken,int StoryId)
{
    public const string Route="story/read";
    public string Semantic=>MainlineFields.Semantic(Route,StoryId);
    public static StoryReadRequest Read(JsonElement root)
    {
        var common=MainlineFields.Read(root,"story_id");var id=MainlineFields.Int(root,"story_id");
        if(id<=0)throw new InvalidDataException("Invalid story identity.");
        return new(MainlineFields.Retry(common),MainlineFields.Auth(common),id);
    }
}
public sealed record VoicePlayGroup(string PlayDate,int[] CharaVoiceIds);
public sealed record CharaSetVoiceRequest(string RetryToken,string? AuthToken,VoicePlayGroup[] CharaVoices)
{
    public const string Route="chara/set_voice";
    public string Semantic=>MainlineFields.Semantic(Route,CharaVoices);
    public static CharaSetVoiceRequest Read(JsonElement root)
    {
        var common=MainlineFields.Read(root,"chara_voices");var groups=root.GetProperty("chara_voices");
        // Local resource limits preserve queued reports without unbounded input.
        if(groups.ValueKind!=JsonValueKind.Array || groups.GetArrayLength() is <1 or >128)
            throw new InvalidDataException("Invalid voice report groups.");
        var result=new List<VoicePlayGroup>();var count=0;
        foreach(var g in groups.EnumerateArray())
        {
            FormationRequestFields.Fields(g,"play_date","chara_voice_ids");
            var date=MainlineFields.String(g,"play_date");
            if(!TryDate(date,out _))throw new InvalidDataException("Invalid voice report date.");
            var ids=VipRequestFields.IntArray(g,"chara_voice_ids",2608,nonempty:true);
            count=checked(count+ids.Length);
            if(ids.Any(x=>x<=0)||count>2608)throw new InvalidDataException("Invalid voice report identities.");
            result.Add(new(date,ids));
        }
        return new(MainlineFields.Retry(common),MainlineFields.Auth(common),result.ToArray());
    }
    internal static bool TryDate(string text,out DateTime date)=>DateTime.TryParseExact(text,MiscCatalog.DateFormat,
        CultureInfo.InvariantCulture,DateTimeStyles.None,out date)&&date.ToString(MiscCatalog.DateFormat,CultureInfo.InvariantCulture)==text;
}
public sealed record UpdateSupportRequest(string RetryToken,string? AuthToken,int CardId,int[] EquipIds)
{
    public const string Route="user/update_leader";
    public string Semantic=>MainlineFields.Semantic(Route,new{card_id=CardId,user_equip_ids=string.Join(',',EquipIds)});
    public static UpdateSupportRequest Read(JsonElement root)
    {
        var common=MainlineFields.Read(root,"card_id","user_equip_ids");var card=MainlineFields.Int(root,"card_id");
        var raw=MainlineFields.String(root,"user_equip_ids");
        // Native empty string creates an empty list. Canonical three zero slots
        // preserve positions thereafter; no empty comma segment reaches Convert.
        var ids=raw.Length==0?new int[3]:FormationRequestFields.EquipIds(root.GetProperty("user_equip_ids"));
        if(card<=0)throw new InvalidDataException("Invalid support card identity.");
        return new(MainlineFields.Retry(common),MainlineFields.Auth(common),card,ids);
    }
}
