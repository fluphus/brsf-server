using System.Text.Json;
using Brs.Local.Accounts;

namespace Brs.Local.Management;

// No second store, database handle, or service process. Every operation uses the game's AccountStore gate.
public sealed class AccountStoreManagementBackend(AccountStore accounts) : IManagementBackend
{
    public void ReleaseOwner(string ownerSession) => accounts.ManagementReleaseOwner(ownerSession);

    public ManagementResponse Invoke(ManagementAction action, string? uid, string ownerSession, string? lease, JsonElement body)
    {
        ManagementResult result;
        switch (action)
        {
            case ManagementAction.Status:
                return new(200, new { version = 1, schemaVersion = AccountStore.AccountSchemaVersion, mode = "local-owner", loopbackOnly = true });
            case ManagementAction.ExpertPeriod: result=accounts.ManagementExpertPeriod(); break;
            case ManagementAction.SetExpertPeriod:
                ManagementHost.BodyObject(body,"expectedRevision","questTypeId");
                if(!body.TryGetProperty("expectedRevision",out var revision)||revision.ValueKind!=JsonValueKind.Number||!revision.TryGetInt64(out var expected)||expected<0||
                    !body.TryGetProperty("questTypeId",out var period)||period.ValueKind!=JsonValueKind.Number||!period.TryGetInt32(out var questTypeId))
                    throw new ManagementHost.RequestFailure(400,"invalid_request");
                result=accounts.ManagementSetExpertPeriod(expected,questTypeId);break;
            case ManagementAction.Catalog: result = accounts.ManagementCatalog(); break;
            case ManagementAction.Accounts: result = accounts.ManagementAccounts(); break;
            case ManagementAction.Account: result = accounts.ManagementAccount(uid!); break;
            case ManagementAction.BeginMaintenance:
                ManagementHost.BodyObject(body);
                result = accounts.ManagementBeginMaintenance(uid!, ownerSession); break;
            case ManagementAction.RenewMaintenance:
                ManagementHost.BodyObject(body);
                result = accounts.ManagementRenewMaintenance(uid!, ownerSession, lease!); break;
            case ManagementAction.EndMaintenance:
                ManagementHost.BodyObject(body);
                result = accounts.ManagementEndMaintenance(uid!, ownerSession, lease!); break;
            case ManagementAction.Preview:
                result = accounts.ManagementPreview(uid!, ownerSession, lease!, ReadCommand(body)); break;
            case ManagementAction.Execute:
                result = accounts.ManagementExecute(uid!, ownerSession, lease!, ReadCommand(body)); break;
            case ManagementAction.Create:
                ManagementHost.BodyObject(body, "operationId", "name", "password");
                result = accounts.ManagementCreate(Text(body, "operationId", 36), Text(body, "name", 64), Text(body, "password", 12)); break;
            case ManagementAction.Credentials:
                ManagementHost.BodyObject(body, "operationId", "expectedRevision", "password");
                result = accounts.ManagementCredentials(uid!, ownerSession, lease!, Text(body, "operationId", 36),
                    Text(body, "expectedRevision", 128), Text(body, "password", 12)); break;
            case ManagementAction.Export: result = accounts.ManagementExport(uid!); break;
            case ManagementAction.Backup:
                result = accounts.ManagementBackup();
                if (result.Status == 200 && result.Value is ManagementBackupFile file)
                    return new(200, new ManagementDownload(file.DownloadName, "application/vnd.sqlite3",
                        () => new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan), file.Dispose));
                break;
            default: return new(404, Code: "invalid_request");
        }
        return new(result.Status, result.Value, result.Code);
    }

    private static ManagementCommand ReadCommand(JsonElement body)
    {
        ManagementHost.BodyObject(body, "operationId", "expectedRevision", "kind", "payload");
        if (!body.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            throw new ManagementHost.RequestFailure(400, "invalid_request");
        return new ManagementCommand(Text(body, "operationId", 36), Text(body, "expectedRevision", 128), Text(body, "kind", 40), payload.Clone());
    }

    private static string Text(JsonElement body, string name, int maximum) => ManagementHost.RequiredString(body, name, maximum);
}
