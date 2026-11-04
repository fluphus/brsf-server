import com.android.tools.smali.dexlib2.*;
import com.android.tools.smali.dexlib2.iface.*;
import com.android.tools.smali.dexlib2.iface.instruction.*;
import com.android.tools.smali.dexlib2.iface.reference.*;
import com.android.tools.smali.dexlib2.immutable.*;
import com.android.tools.smali.dexlib2.immutable.reference.*;
import com.android.tools.smali.dexlib2.rewriter.*;
import com.android.tools.smali.dexlib2.writer.pool.DexPool;
import java.io.*;
import java.nio.file.*;
import java.security.*;
import java.util.*;

/** Copy four exact-version official method bodies, mapping only existing app symbols. */
public class FirebaseRestore {
 static final String IID="Lcom/google/firebase/iid/",MSG="Lcom/google/firebase/messaging/";
 static final Map<String,String> TYPES=Map.of(
  IID+"SyncTask;",IID+"v;",IID+"SyncTask$ConnectivityChangeReceiver;",IID+"v$a;",
  MSG+"SyncTask;",MSG+"y0;",MSG+"SyncTask$ConnectivityChangeReceiver;",MSG+"y0$a;",
  MSG+"TopicsSyncTask;",MSG+"b1;",MSG+"TopicsSyncTask$ConnectivityChangeReceiver;",MSG+"b1$a;",
  MSG+"cpp/RegistrationIntentService$1;",MSG+"cpp/RegistrationIntentService$a;");
 static final Map<String,String> METHODS=Map.ofEntries(
  Map.entry(IID+"SyncTask;->getContext","b"),Map.entry(IID+"SyncTask;->isDeviceConnected","c"),Map.entry(IID+"SyncTask;->access$000","a"),
  Map.entry(IID+"FirebaseInstanceId;->isDebugLogEnabled","r"),Map.entry(IID+"FirebaseInstanceId;->enqueueTaskWithDelaySeconds","g"),
  Map.entry(MSG+"SyncTask;->getContext","b"),Map.entry(MSG+"SyncTask;->isDeviceConnected","d"),Map.entry(MSG+"SyncTask;->access$000","a"),Map.entry(MSG+"SyncTask;->isDebugLogEnabled","c"),
  Map.entry(MSG+"TopicsSyncTask;->access$000","a"),Map.entry(MSG+"TopicsSyncTask;->access$100","b"),Map.entry(MSG+"TopicsSyncTask;->access$200","c"),
  Map.entry(MSG+"TopicsSubscriber;->scheduleSyncTaskWithDelaySeconds","l"));
 static final Set<String> TARGETS=Set.of(IID+"v$a;->onReceive",MSG+"y0$a;->onReceive",MSG+"b1$a;->onReceive",MSG+"cpp/RegistrationIntentService;->onHandleWork");
 static String type(String t){return TYPES.getOrDefault(t,t);}
 static String key(MethodReference m){return m.getDefiningClass()+"->"+m.getName()+"("+String.join("",m.getParameterTypes())+")"+m.getReturnType();}
 static String fieldKey(FieldReference f){return f.getDefiningClass()+"->"+f.getName()+":"+f.getType();}
 static void require(boolean b,String s){if(!b)throw new IllegalStateException(s);}
 static String canon(Object x)throws Exception{return DexSnapshot.canonical(x);}
 static byte[] digest(String s)throws Exception{return MessageDigest.getInstance("SHA-256").digest(s.getBytes(java.nio.charset.StandardCharsets.UTF_8));}
 static DexRewriter rewriter(){return new DexRewriter(new RewriterModule(){
  public Rewriter<String> getTypeRewriter(Rewriters r){return FirebaseRestore::type;}
  public Rewriter<MethodReference> getMethodReferenceRewriter(Rewriters r){return m->{
   var p=new ArrayList<String>();for(var t:m.getParameterTypes())p.add(type(t.toString()));
   return new ImmutableMethodReference(type(m.getDefiningClass()),METHODS.getOrDefault(m.getDefiningClass()+"->"+m.getName(),m.getName()),p,type(m.getReturnType()));
  };}
  public Rewriter<FieldReference> getFieldReferenceRewriter(Rewriters r){return f->new ImmutableFieldReference(type(f.getDefiningClass()),f.getName().equals("task")&&TYPES.containsKey(f.getDefiningClass())?"a":f.getName(),type(f.getType()));}
 });}
 public static void main(String[] args)throws Exception{
  var input=DexFileFactory.loadDexContainer(new File(args[0]),Opcodes.getDefault());var official=DexFileFactory.loadDexContainer(new File(args[1]),Opcodes.getDefault());var out=Path.of(args[2]);Files.createDirectories(out);
  Map<String,Method> replacements=new HashMap<>();var rw=rewriter();
  for(var entry:official.getDexEntryNames())for(var c:official.getEntry(entry).getDexFile().getClasses())for(var m:c.getMethods()){
   var mapped=rw.getMethodRewriter().rewrite(m);if(TARGETS.contains(mapped.getDefiningClass()+"->"+mapped.getName()))require(replacements.put(mapped.getDefiningClass()+"->"+mapped.getName(),ImmutableMethod.of(mapped))==null,"Duplicate official method");
  }
  require(replacements.keySet().equals(TARGETS),"Missing official targets");
  Map<String,ClassDef> all=new HashMap<>();Map<String,Method> methods=new HashMap<>();Map<String,com.android.tools.smali.dexlib2.iface.Field> fields=new HashMap<>();
  for(var entry:input.getDexEntryNames())for(var c:input.getEntry(entry).getDexFile().getClasses()){
   all.put(c.getType(),c);for(var m:c.getMethods())methods.put(key(m),m);for(var f:c.getFields())fields.put(fieldKey(f),f);
  }
  var notes=new ArrayList<String>();Set<String> patched=new TreeSet<>();int classes=0;
  for(var entry:input.getDexEntryNames()){
   var source=input.getEntry(entry).getDexFile();var result=new ArrayList<ClassDef>();Map<String,byte[]> expected=new HashMap<>();boolean touched=false;
   for(var c:source.getClasses()){
    classes++;var nextMethods=new ArrayList<Method>();boolean edited=false;
    for(Method m:c.getMethods()){
     String target=m.getDefiningClass()+"->"+m.getName();var replacement=replacements.get(target);
     if(replacement!=null){
      require(patched.add(target),"Repeated target");require(m.getAccessFlags()==replacement.getAccessFlags(),"Method flags differ "+target+" old="+m.getAccessFlags()+" official="+replacement.getAccessFlags());
      require(key(m).equals(key(replacement)),"Signature differs");
      boolean protectedBody=false;for(var ins:m.getImplementation().getInstructions())if(ins instanceof ReferenceInstruction ri&&ri.getReference() instanceof MethodReference mr&&mr.getDefiningClass().equals("Lcom/pairip/VMRunner;"))protectedBody=true;
      require(protectedBody,"Target no longer VM protected");
      for(var ins:replacement.getImplementation().getInstructions())if(ins instanceof ReferenceInstruction ri){
       var ref=ri.getReference();
       if(ref instanceof MethodReference mr&&all.containsKey(mr.getDefiningClass())){
        var actual=methods.get(key(mr));require(actual!=null,"Unresolved mapped method "+key(mr));
        require(((actual.getAccessFlags()&8)!=0)==ins.getOpcode().name.startsWith("invoke-static"),"Static/instance mismatch "+key(mr));
        require((actual.getAccessFlags()&2)==0||actual.getDefiningClass().equals(m.getDefiningClass()),"Private method inaccessible "+key(mr));
       }
       if(ref instanceof FieldReference fr&&all.containsKey(fr.getDefiningClass())){
        var actual=fields.get(fieldKey(fr));require(actual!=null,"Unresolved mapped field "+fieldKey(fr));
        require(((actual.getAccessFlags()&8)!=0)==ins.getOpcode().name.startsWith("s"),"Static/instance field mismatch "+fieldKey(fr));
        require((actual.getAccessFlags()&2)==0||actual.getDefiningClass().equals(m.getDefiningClass()),"Private field inaccessible "+fieldKey(fr));
       }
       if(ref instanceof TypeReference tr&&tr.getType().startsWith("Lcom/google/"))require(all.containsKey(tr.getType()),"Unresolved mapped class "+tr.getType());
      }
      var impl=ImmutableMethodImplementation.of(replacement.getImplementation());
      m=new ImmutableMethod(m.getDefiningClass(),m.getName(),m.getParameters(),m.getReturnType(),m.getAccessFlags(),m.getAnnotations(),m.getHiddenApiRestrictions(),impl);
      require(canon(ImmutableMethodImplementation.of(m.getImplementation())).equals(canon(ImmutableMethodImplementation.of(replacement.getImplementation()))),"Body is not official mapped body");
      notes.add(target+" official mapped body exact; flags="+m.getAccessFlags()+"; registers="+impl.getRegisterCount());edited=true;touched=true;
     }
     nextMethods.add(m);
    }
    ClassDef next=edited?new ImmutableClassDef(c.getType(),c.getAccessFlags(),c.getSuperclass(),c.getInterfaces(),c.getSourceFile(),c.getAnnotations(),c.getFields(),nextMethods):c;
    if(edited){var restored=new ArrayList<Method>();for(var m:next.getMethods()){
     Method original=m;for(var old:c.getMethods())if(key(old).equals(key(m))){if(TARGETS.contains(m.getDefiningClass()+"->"+m.getName()))original=old;else require(canon(ImmutableMethod.of(m)).equals(canon(ImmutableMethod.of(old))),"Other method changed");break;}restored.add(original);
    }require(canon(ImmutableClassDef.of(c)).equals(canon(new ImmutableClassDef(c.getType(),c.getAccessFlags(),c.getSuperclass(),c.getInterfaces(),c.getSourceFile(),c.getAnnotations(),c.getFields(),restored))),"Other class metadata changed");}
    result.add(next);expected.put(c.getType(),digest(canon(ImmutableClassDef.of(next))));
   }
   if(touched){var path=out.resolve(entry);require(!Files.exists(path),"Output exists");DexPool.writeTo(path.toString(),new ImmutableDexFile(source.getOpcodes(),result));int checked=0;
    for(var c:DexFileFactory.loadDexFile(path.toFile(),source.getOpcodes()).getClasses()){require(Arrays.equals(expected.get(c.getType()),digest(canon(ImmutableClassDef.of(c)))),"Roundtrip drift "+c.getType());checked++;}require(checked==expected.size(),"Class count differs");
   }
  }
  require(patched.equals(TARGETS),"Missing app targets");Files.write(out.resolve("method-proof.txt"),notes);Files.writeString(out.resolve("dex-proof.json"),"{\"restored_methods\":4,\"classes_checked\":"+classes+",\"official_mapped_method_bodies_exact\":true,\"all_other_methods_unchanged_to_v8\":true,\"all_reencoded_classes_roundtrip_exact\":true,\"diagnostic_methods_retained\":2}\n");System.out.println(String.join("\n",notes));
 }
}
