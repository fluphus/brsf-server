using System.Text.Json;

namespace Brs.Local.Accounts;

public sealed record QuestRankingRequest(string? AuthToken,int QuestId)
{
    public const string Route="quest/ranking/list";
    public static QuestRankingRequest Read(JsonElement root)
    {
        var common=MainlineFields.Read(root,"quest_id");return new(MainlineFields.Auth(common),MainlineFields.Int(root,"quest_id"));
    }
}
public sealed record QuestRankingEntryRequest(string RetryToken,string? AuthToken,int QuestId,bool IsEntry)
{
    public const string Route="quest/ta_entry";
    public string Semantic=>MainlineFields.Semantic(Route,new{QuestId,IsEntry});
    public static QuestRankingEntryRequest Read(JsonElement root)
    {
        var common=MainlineFields.Read(root,"quest_id","is_entry");
        return new(MainlineFields.Retry(common),MainlineFields.Auth(common),MainlineFields.Int(root,"quest_id"),MainlineFields.Bool(root,"is_entry"));
    }
}
