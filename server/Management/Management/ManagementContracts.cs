using System.Text.Json;

namespace Brs.Local.Management;

// This seam allows the HTTP security boundary to be checked without booting the game/content archive.
public enum ManagementAction
{
    Status, Catalog, Accounts, Account, BeginMaintenance, RenewMaintenance, EndMaintenance,
    Preview, Execute, Create, Credentials, Export, Backup, ExpertPeriod, SetExpertPeriod
}

public sealed record ManagementResponse(int Status, object? Value = null, string? Code = null);

public interface IManagementBackend
{
    ManagementResponse Invoke(ManagementAction action, string? uid, string ownerSession, string? lease, JsonElement body);
    void ReleaseOwner(string ownerSession);
}

public sealed class ManagementDownload(string downloadName, string contentType, Func<Stream> open, Action dispose) : IDisposable
{
    public string DownloadName { get; } = downloadName;
    public string ContentType { get; } = contentType;
    public Stream Open() => open();
    public void Dispose() => dispose();
}

public sealed record ManagementHostOptions(int Port, string KeyFile)
{
    public const int MaximumBodyBytes = 65536;
    public const int MaximumJsonDepth = 16;
    public const int MaximumArrayItems = 200;
    public const int MaximumSessions = 16;
    public const int MaximumLoginAttempts = 10;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan LoginWindow = TimeSpan.FromMinutes(1);

}
