using System.Text.Json;
namespace Brs.Local.Accounts;
public sealed record ManagementResult(int Status,object? Value=null,string? Code=null);
public sealed record ManagementCommand(string OperationId,string ExpectedRevision,string Kind,JsonElement Payload);
// Deliberately no credential-bearing record / generated ToString.
public sealed class ManagementBackupFile : IDisposable
{
 public string Path{get;} public string DownloadName=>"brsf-owner-backup.sqlite3";
 internal ManagementBackupFile(string path){Path=path;}
 public void Dispose(){try{File.Delete(Path);}catch(IOException){}catch(UnauthorizedAccessException){}}
}
