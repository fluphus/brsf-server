using System.Text.Json;
using Brs.AndroidLocalHost;

var checks=new List<string>();void Need(bool value,string label){if(!value)throw new InvalidDataException(label);checks.Add(label);}
var memory=new NativeCredentials();const string root=NativeCredentials.RootUrl;
const string id="ABcd012345EFgh67",password="SecretPass12";var copied=new List<string>();
string Call(object payload,string? sender=root,string? current=root,Action<string>? copy=null)=>memory.Handle(sender,current,JsonSerializer.Serialize(payload),copy??copied.Add);
bool Ok(string reply){using var j=JsonDocument.Parse(reply);return j.RootElement.GetProperty("ok").GetBoolean();}
JsonElement Restore(){using var j=JsonDocument.Parse(Call(new{action="restore"}));return j.RootElement.GetProperty("record").Clone();}
Need(Restore().ValueKind==JsonValueKind.Null&&!Ok(Call(new{action="copy",field="all"})),"empty_memory_cannot_restore_or_copy");
Need(Ok(Call(new{action="retain",uid="51",takeoverId=id,password})),"retain_one_valid_bounded_record");
var record=Restore();Need(record.GetProperty("uid").GetString()=="51"&&record.GetProperty("takeoverId").GetString()==id&&record.GetProperty("password").GetString()==password,"restore_original_case_sensitive_values");
Need(Ok(Call(new{action="copy",field="id"}))&&copied[^1]==id,"copy_id_exact_after_callback");
Need(Ok(Call(new{action="copy",field="password"}))&&copied[^1]==password,"copy_password_exact_after_callback");
Need(Ok(Call(new{action="copy",field="all"}))&&copied[^1]=="ID: "+id+"\nPASS: "+password,"copy_all_exact_two_line_format");
var copyCount=copied.Count;
Need(!Ok(Call(new{action="copy",field="all",text="arbitrary text"}))&&copied.Count==copyCount,"no_arbitrary_text_clipboard_operation");
Need(!Ok(Call(new{action="copy",field="unknown"}))&&copied.Count==copyCount,"unknown_copy_field_rejected");
var failed=Call(new{action="copy",field="all"},copy:_=>throw new IOException(password));
Need(!Ok(failed)&&!failed.Contains(password)&&Restore().GetProperty("password").GetString()==password,"clipboard_failure_false_ack_no_exception_secret_and_record_preserved");
foreach(var url in new[]{"http://evil.invalid/","http://127.0.0.1:18445/","http://localhost:18180/","http://127.0.0.1:18180/index.html","http://127.0.0.1:18180/?x=1","http://127.0.0.1:18180/#fragment"})
 Need(!Ok(Call(new{action="restore"},sender:url))&&!Ok(Call(new{action="restore"},current:url)),"both_exact_root_checks_"+checks.Count);
Need(!Ok(Call(new{action="restore"},sender:null))&&!Ok(Call(new{action="restore"},current:null)),"missing_frame_context_rejected");
foreach(var invalid in new object[]{new{action="retain",uid="051",takeoverId=id,password},new{action="retain",uid="0",takeoverId=id,password},new{action="retain",uid="51",takeoverId="short",password},new{action="retain",uid="51",takeoverId=id,password="bad-characters"},new{action="retain",uid="51",takeoverId=id,password="short"}})
 Need(!Ok(Call(invalid))&&Restore().GetProperty("password").GetString()==password,"invalid_retain_never_replaces_record_"+checks.Count);
Need(!Ok(memory.Handle(root,root,"{\"action\":\"clear\",\"action\":\"restore\"}",copied.Add)),"duplicate_properties_rejected");
Need(!Ok(memory.Handle(root,root,new string('x',1025),copied.Add))&&!Ok(memory.Handle(root,root,"{invalid",copied.Add)),"oversize_and_malformed_payload_fixed_failure");
Need(Ok(Call(new{action="retain",uid="52",takeoverId=id,password="NextPass12"}))&&Restore().GetProperty("uid").GetString()=="52","new_record_replaces_single_slot");
Need(Ok(Call(new{action="clear"}))&&Restore().ValueKind==JsonValueKind.Null&&!Ok(Call(new{action="copy",field="id"})),"explicit_clear_removes_restore_and_copy");
Need(new NativeCredentials().Handle(root,root,"{\"action\":\"restore\"}",copied.Add)=="{\"ok\":true,\"record\":null}","new_process_memory_has_no_disk_restore");

// The actual slot/owner logic used by the Android adapter, with a small view
// double to exercise rebind/unbind ordering without an Android device.
var slot=new MemoryPageSlot<ViewDouble>();var appContext=new object();var owner1=new object();var owner2=new object();var token1=new object();var token2=new object();int creates=0;
ViewDouble Create(){creates++;return new(){Dom="unclosed credential dialog"};}
void Bind(ViewDouble view,object activity){view.Context=activity;view.Parent=activity;view.Client=activity;view.Chrome=activity;view.Download=activity;}
void Unbind(ViewDouble view,bool destroy){view.Parent=null;view.Context=appContext;view.Client=null;view.Chrome=null;view.Download=null;if(destroy){view.Destroyed=true;view.Dom="";}}
var first=slot.Acquire(token1,Create,v=>Bind(v,owner1));slot.ShowingManagement=true;
slot.Release(token1,first,true,Unbind);
Need(first.Context==appContext&&first.Parent is null&&first.Client is null&&first.Chrome is null&&first.Download is null&&!first.Destroyed,"detach_drops_old_activity_context_and_all_callback_owners");
var second=slot.Acquire(token2,Create,v=>Bind(v,owner2));
Need(ReferenceEquals(first,second)&&creates==1&&second.Dom=="unclosed credential dialog"&&slot.ShowingManagement&&second.Context==owner2&&second.Client==owner2,"rotation_reuses_live_page_dom_and_management_selection");
slot.Release(token1,first,false,Unbind);
Need(slot.Owns(token2,second)&&!second.Destroyed&&second.Context==owner2,"late_old_activity_destroy_cannot_unbind_new_owner");
slot.Release(token2,second,false,Unbind);
Need(second.Destroyed&&second.Context==appContext&&second.Parent is null,"explicit_finish_releases_view_without_retaining_activity");
var third=slot.Acquire(token1,Create,v=>Bind(v,owner1));slot.Forget(third,Unbind);
var fourth=slot.Acquire(token2,Create,v=>Bind(v,owner2));
Need(third.Destroyed&&!ReferenceEquals(third,fourth)&&creates==3,"renderer_loss_forgets_destroyed_view_and_allows_fresh_page");
slot.Release(token2,fourth,false,Unbind);
Console.WriteLine(JsonSerializer.Serialize(new{success=true,checks=checks.Count,passed=checks,scope="Bounded credential protocol and actual in-memory page ownership logic; Android clipboard/rotation still require device acceptance"}));
sealed class ViewDouble
{public object? Context,Parent,Client,Chrome,Download;public string Dom="";public bool Destroyed;}
