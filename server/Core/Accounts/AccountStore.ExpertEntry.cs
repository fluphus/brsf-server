using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Brs.Local.Accounts;

public sealed record UserBlankRequest(string? AuthToken)
{
    public const string Route="user/blank";
    public static UserBlankRequest Read(JsonElement root)=>new(MainlineFields.Auth(MainlineFields.Read(root)));
}

public sealed partial class AccountStore
{
    private int? activeExpertQuestTypeId;
    private string? expertAvailabilityFingerprint;

    // The original manifest supplies the default. A validated global setting
    // in this save domain overrides it without rewriting account progress.
    public void ConfigureExpertAvailability(int questTypeId,string contentFingerprint)
    {
        lock(gate)
        {
            if(activeExpertQuestTypeId is not null)throw new InvalidOperationException("Expert availability already configured.");
            if(questModesConfig is null||!questModesConfig.ExpertSchedules.Any(s=>s.QuestTypeId==questTypeId)||
                contentFingerprint.Length!=64||!contentFingerprint.All(Uri.IsHexDigit))throw new InvalidDataException("Invalid original Expert selection.");
            using var c=Open();using var t=c.BeginTransaction(deferred:true);
            defaultExpertQuestTypeId=questTypeId;
            var setting=ReadExpertPeriod(c,t);
            if(!questModesConfig.ExpertSchedules.Any(s=>s.QuestTypeId==setting.QuestTypeId))throw new InvalidDataException("Invalid persisted Expert selection.");
            activeExpertQuestTypeId=setting.QuestTypeId;expertAvailabilityFingerprint=contentFingerprint;
        }
    }
    private bool IsModeQuestAvailable(MainlineQuest quest,long now)=>questModesConfig!.IsOpen(quest,now)&&
        (questModesConfig.Chapter(quest).Type!=3||activeExpertQuestTypeId is null||quest.QuestTypeId==activeExpertQuestTypeId);

    public AccountResult RefreshAccountSnapshot(UserBlankRequest request)
    {
        if(string.IsNullOrEmpty(request.AuthToken))return new(401);
        lock(gate)
        {
            using var c=new SqliteConnection(new SqliteConnectionStringBuilder(connectionString){Mode=SqliteOpenMode.ReadOnly}.ToString());c.Open();
            using var t=c.BeginTransaction(deferred:true);var now=Now;
            if(!TryReadAuthenticatedAccount(c,t,request.AuthToken,now,out var account,out var player))return new(401);
            // An original menu refresh: no login/session renewal, daily observer,
            // receipt, challenge reservation, inventory grant or player save.
            return new(200,Snapshot(c,t,account,player,nowOverride:now));
        }
    }
}
