using System.Net;
using System.Text.Json;
using Brs.AndroidLocalHost;

var labels=new List<string>();
void Check(bool value,string label){if(!value)throw new InvalidDataException(label);labels.Add(label);}
async Task<bool> Fails(Func<Task> action){try{await action();return false;}catch(Exception e)when(e is IOException or HttpRequestException or InvalidDataException or OperationCanceledException){return true;}}
var scratch=Path.Combine(AppContext.BaseDirectory,"private-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);

var import=NativeOperations.TryBegin(NativeWorkKind.Import)??throw new Exception();
Check(NativeOperations.TryBegin(NativeWorkKind.Import)is null&&NativeOperations.TryAcquireHost()is null,"pending_import_blocks_repeated_import_and_service");
Check(ReferenceEquals(NativeOperations.ClaimPending(NativeWorkKind.Import),import),"recreated_activity_can_claim_existing_pending_import");
NativeOperations.CancelPending();
Check(NativeOperations.IsBusy&&NativeOperations.TryAcquireHost()is null,"activity_pending_cancel_cannot_release_active_import_cleanup");
import.Dispose();Check(!NativeOperations.IsBusy,"import_finally_releases_process_gate");
var host=NativeOperations.TryAcquireHost()??throw new Exception();
Check(NativeOperations.TryBegin(NativeWorkKind.Import)is null&&NativeOperations.TryBegin(NativeWorkKind.Cleanup)is null&&NativeOperations.TryAcquireHost()is null,"host_lease_excludes_import_cleanup_and_duplicate_host");
var export=NativeOperations.TryBegin(NativeWorkKind.Export)??throw new Exception();
Check(NativeOperations.TryBegin(NativeWorkKind.Export)is null,"export_selection_exclusive_across_activity_instances");
NativeOperations.CancelPending();Check(!NativeOperations.IsBusy,"cancelled_picker_releases_pending_export");
host.Dispose();var cleanup=NativeOperations.TryBegin(NativeWorkKind.Cleanup)??throw new Exception();
Check(NativeOperations.Claim(cleanup)&&NativeOperations.TryAcquireHost()is null,"confirmed_cleanup_blocks_service_until_finally");
cleanup.Dispose();var next=NativeOperations.TryBegin(NativeWorkKind.Import)??throw new Exception();
import.Dispose();Check(NativeOperations.IsBusy,"old_work_dispose_cannot_release_new_work");next.Dispose();
var confirmation=NativeOperations.TryBegin(NativeWorkKind.Cleanup)??throw new Exception();
var dismissed=NativeOperations.CancelPending(confirmation)&&!NativeOperations.IsBusy;
var activeCleanup=NativeOperations.TryBegin(NativeWorkKind.Cleanup)??throw new Exception();NativeOperations.Claim(activeCleanup);
Check(dismissed&&!NativeOperations.CancelPending(confirmation)&&!NativeOperations.CancelPending(activeCleanup)&&NativeOperations.IsBusy,"cleanup_dismiss_releases_only_same_pending_confirmation_never_active_work");activeCleanup.Dispose();

var root=Path.Combine(scratch,"content");Directory.CreateDirectory(root);
var generation=Path.Combine(root,"generation-v1");Directory.CreateDirectory(generation);File.WriteAllText(Path.Combine(generation,"content.zip"),"published-fixture");
var db=Path.Combine(scratch,"accounts.sqlite3");File.WriteAllText(db,"database-fixture");
string Stage(){var p=Path.Combine(root,".import-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(p);return p;}
var stage=Stage();var file=Path.Combine(stage,"content.zip");File.WriteAllText(file,"cancelled-import");File.SetAttributes(file,FileAttributes.ReadOnly);File.WriteAllText(Path.Combine(stage,"receipt.json"),"{}");
var confirmed=ImportStageCleanup.Scan(root);
Check(confirmed.Count==1&&confirmed[0].Bytes==18,"scan_only_fixed_import_stage_and_reports_size");
using(var cancelled=new CancellationTokenSource())
{cancelled.Cancel();Check(await Fails(()=>Task.Run(()=>ImportStageCleanup.DeleteConfirmed(root,confirmed,cancelled.Token)))&&File.Exists(file),"cancel_before_cleanup_does_not_delete");}
Check(ImportStageCleanup.DeleteConfirmed(root,confirmed,CancellationToken.None)==1&&!Directory.Exists(stage),"explicit_cleanup_removes_known_readonly_staging_files");
Check(File.ReadAllText(Path.Combine(generation,"content.zip"))=="published-fixture"&&File.ReadAllText(db)=="database-fixture","cleanup_preserves_published_generation_and_database");
var packageStage=Path.Combine(root,".package-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(packageStage,"materials"));
File.WriteAllText(Path.Combine(packageStage,"materials/protocol-key.bin"),"incomplete-key");
var packageConfirmed=ImportStageCleanup.Scan(root);
Check(packageConfirmed.Count==1&&packageConfirmed[0].Bytes==14,"native_cleanup_scan_includes_shared_resource_package_stages");
File.AppendAllText(Path.Combine(packageStage,"materials/protocol-key.bin"),"changed");
Check(await Fails(()=>Task.Run(()=>ImportStageCleanup.DeleteConfirmed(root,packageConfirmed,CancellationToken.None)))&&Directory.Exists(packageStage),"changed_package_stage_requires_fresh_confirmation");
Check(ImportStageCleanup.DeleteConfirmed(root,ImportStageCleanup.Scan(root),CancellationToken.None)==1&&!Directory.Exists(packageStage),"native_confirmation_uses_shared_bounded_package_cleanup");
Check(File.ReadAllText(Path.Combine(generation,"content.zip"))=="published-fixture"&&File.ReadAllText(db)=="database-fixture","package_cleanup_preserves_generation_and_accounts");
var unsafeStage=Stage();File.WriteAllText(Path.Combine(unsafeStage,"content.zip"),"keep");File.WriteAllText(Path.Combine(unsafeStage,"unexpected.txt"),"keep");
Check(await Fails(()=>Task.Run(()=>ImportStageCleanup.Scan(root)))&&File.Exists(Path.Combine(unsafeStage,"content.zip")),"unexpected_stage_child_refused_without_deletion");
Check(await Fails(()=>Task.Run(()=>ImportStageCleanup.DeleteConfirmed(root,[new(generation,0)],CancellationToken.None)))&&Directory.Exists(generation),"published_path_cannot_be_passed_as_stage");
Check(await Fails(()=>Task.Run(()=>ImportStageCleanup.DeleteConfirmed(root,[new(scratch,0)],CancellationToken.None)))&&File.Exists(db),"outside_root_cleanup_refused");

var backup=NativeExportPolicy.Resolve("http://127.0.0.1:18180/management/v1/backup")??throw new Exception();
var account=NativeExportPolicy.Resolve("http://127.0.0.1:18180/management/v1/accounts/51/export")??throw new Exception();
Check(backup.Mime=="application/octet-stream"&&account.Mime=="application/json","native_export_only_two_exact_route_shapes");
foreach(var url in new[]{"http://evil.invalid:18180/management/v1/backup","http://127.0.0.1:18445/management/v1/backup","http://secret@127.0.0.1:18180/management/v1/backup","file:///management/v1/backup","http://127.0.0.1:18180/management/v1/backup?x=1","http://127.0.0.1:18180/management/v1/backup#x","http://127.0.0.1:18180/management/v1/accounts/0/export","http://127.0.0.1:18180/management/v1/accounts/51%2f2/export","http://127.0.0.1:18180/management/v1/accounts/51/other"})
 Check(NativeExportPolicy.Resolve(url)is null,"reject_export_"+labels.Count);
Check(NativeExportPolicy.Resolve(NativeExportPolicy.Origin+backup.Path,"POST")is null,"non_get_native_export_refused");

bool correctRequest=false;var sink=new Sink();
using(var client=new HttpClient(new Handler(request=>
{correctRequest=request.Method==HttpMethod.Get&&request.RequestUri!.AbsoluteUri==NativeExportPolicy.Origin+backup.Path&&request.Headers.GetValues("Cookie").Single()=="session=fixture";return new(HttpStatusCode.OK){Content=new ByteArrayContent("export-fixture"u8.ToArray())};})))
 Check(await NativeExportTransfer.SaveAsync(client,backup,"session=fixture",()=>sink,CancellationToken.None)==14&&correctRequest&&sink.Closed&&sink.ToArray().SequenceEqual("export-fixture"u8.ToArray()),"native_cookie_get_stream_flushed_and_closed_before_success");
var opened=false;
using(var redirect=new HttpClient(new Handler(_=>new(HttpStatusCode.Found){Headers={Location=new Uri("https://evil.invalid/")}})))
 Check(await Fails(()=>NativeExportTransfer.SaveAsync(redirect,backup,"session=fixture",()=>{opened=true;return new Sink();},CancellationToken.None))&&!opened,"redirect_fails_before_destination_open");
using(var denied=new HttpClient(new Handler(_=>new(HttpStatusCode.Unauthorized))))
 Check(await Fails(()=>NativeExportTransfer.SaveAsync(denied,backup,"session=fixture",()=>{opened=true;return new Sink();},CancellationToken.None))&&!opened,"auth_failure_never_opens_destination");
var partial=new Sink{FailAfterFirst=true};
using(var client=new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[131073])})))
 Check(await Fails(()=>NativeExportTransfer.SaveAsync(client,backup,"session=fixture",()=>partial,CancellationToken.None))&&partial.Closed&&partial.ToArray().Length==65536,"partial_destination_failure_closes_stream_and_never_returns_success");
using(var cancellation=new CancellationTokenSource())
using(var client=new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[131073])})))
{var cancelledSink=new Sink{CancelAfterWrite=cancellation};Check(await Fails(()=>NativeExportTransfer.SaveAsync(client,backup,"session=fixture",()=>cancelledSink,cancellation.Token))&&cancelledSink.Closed,"mid_copy_cancel_closes_destination_and_never_returns_success");}
using(var client=new HttpClient(new Handler(_=>{var bytes=new ByteArrayContent("abc"u8.ToArray());bytes.Headers.ContentLength=9;return new(HttpStatusCode.OK){Content=bytes};})))
 Check(await Fails(()=>NativeExportTransfer.SaveAsync(client,backup,"session=fixture",()=>new Sink(),CancellationToken.None)),"short_response_length_rejected");
var result=new{success=true,checks=labels.Count,passed=labels,scope="Pure managed gate/path/stream tests; Android UI and SAF require device acceptance"};
File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"packaging-checks.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(new{success=true,checks=labels.Count}));

sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(response(request));}}
sealed class Sink:MemoryStream
{
 public bool Closed;public bool FailAfterFirst;public CancellationTokenSource? CancelAfterWrite;private int writes;
 public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data,CancellationToken token=default)
 {if(FailAfterFirst&&writes++>0)throw new IOException("private sink failure");await base.WriteAsync(data,token);CancelAfterWrite?.Cancel();}
 protected override void Dispose(bool disposing){Closed=true;base.Dispose(disposing);}
}
