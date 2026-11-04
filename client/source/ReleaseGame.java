import com.android.tools.smali.dexlib2.*;
import com.android.tools.smali.dexlib2.iface.*;
import com.android.tools.smali.dexlib2.iface.instruction.*;
import com.android.tools.smali.dexlib2.iface.reference.*;
import com.android.tools.smali.dexlib2.immutable.*;
import com.android.tools.smali.dexlib2.writer.pool.DexPool;
import com.reandroid.apk.ApkModule;
import com.reandroid.arsc.chunk.xml.ResXmlElement;
import java.io.*;
import java.nio.file.*;
import java.util.*;

/** Exactly two diagnostic method restorations and split-only manifest cleanup. */
public class ReleaseGame {
  static final String VM="Lcom/pairip/VMRunner;";
  static final Set<String> METHODS=Set.of("<clinit>()V","invoke(Ljava/lang/String;[Ljava/lang/Object;)Ljava/lang/Object;");
  static final Set<String> FIREBASE=Set.of("Lcom/google/firebase/iid/v$a;->onReceive","Lcom/google/firebase/messaging/y0$a;->onReceive","Lcom/google/firebase/messaging/b1$a;->onReceive","Lcom/google/firebase/messaging/cpp/RegistrationIntentService;->onHandleWork");
  static String key(Method m){return m.getName()+"("+String.join("",m.getParameterTypes())+")"+m.getReturnType();}
  static String canonical(Object value)throws Exception{return DexSnapshot.canonical(value);}
  static void need(boolean value,String message){if(!value)throw new IllegalStateException(message);}
  static int count(Method method){int value=0;for(var ignored:method.getImplementation().getInstructions())value++;return value;}
  static ClassDef findVm(File input)throws Exception{
    var container=DexFileFactory.loadDexContainer(input,Opcodes.getDefault());ClassDef result=null;
    for(var entry:container.getDexEntryNames())for(var c:container.getEntry(entry).getDexFile().getClasses())if(c.getType().equals(VM)){need(result==null,"Duplicate VMRunner");result=ImmutableClassDef.of(c);}
    need(result!=null,"Missing VMRunner");return result;
  }
  public static void main(String[] args)throws Exception{
    var source=new File(args[0]);var baseline=new File(args[1]);var output=Path.of(args[2]);Files.createDirectories(output);
    var original=findVm(baseline);var replacements=new HashMap<String,Method>();
    for(var method:original.getMethods())if(METHODS.contains(key(method)))replacements.put(key(method),ImmutableMethod.of(method));
    need(replacements.keySet().equals(METHODS),"Missing original method bodies");
    var container=DexFileFactory.loadDexContainer(source,Opcodes.getDefault());int classes=0,restored=0,firebase=0;var notes=new ArrayList<String>();var callbacks=new ArrayList<String>();
    for(var entry:container.getDexEntryNames()){
      var dex=container.getEntry(entry).getDexFile();var rewritten=new ArrayList<ClassDef>();var expected=new HashMap<String,String>();var firebaseBefore=new HashMap<String,String>();boolean touched=false;
      for(var c:dex.getClasses()){
        classes++;ClassDef next=c;
        for(var m:c.getMethods())if(FIREBASE.contains(c.getType()+"->"+m.getName()))firebaseBefore.put(c.getType()+"->"+m.getName(),canonical(ImmutableMethod.of(m)));
        if(c.getType().equals(VM)){
          var methods=new ArrayList<Method>();
          for(Method m:c.getMethods()){
            var replacement=replacements.get(key(m));
            if(replacement!=null){
              need(!canonical(ImmutableMethod.of(m)).equals(canonical(ImmutableMethod.of(replacement))),"Diagnostic already absent");
              notes.add("{\"method\":"+DexSnapshot.quote(key(m))+",\"beforeInstructions\":"+count(m)+",\"afterInstructions\":"+count(replacement)+",\"beforeRegisters\":"+m.getImplementation().getRegisterCount()+",\"afterRegisters\":"+replacement.getImplementation().getRegisterCount()+"}");
              m=replacement;restored++;
            }
            methods.add(m);
          }
          next=new ImmutableClassDef(c.getType(),c.getAccessFlags(),c.getSuperclass(),c.getInterfaces(),c.getSourceFile(),c.getAnnotations(),c.getFields(),methods);
          need(canonical(ImmutableClassDef.of(next)).equals(canonical(ImmutableClassDef.of(original))),"Unexpected VMRunner drift outside two diagnostics");
          for(var m:next.getMethods()){
            boolean nativeMethod=(m.getAccessFlags()&0x100)!=0;
            callbacks.add("{\"method\":"+DexSnapshot.quote(key(m))+",\"native\":"+nativeMethod+",\"instructions\":"+(m.getImplementation()==null?0:count(m))+"}");
          }
          touched=true;
        }
        rewritten.add(next);expected.put(c.getType(),canonical(ImmutableClassDef.of(next)));
      }
      if(touched){
        need(entry.equals("classes.dex"),"Unexpected diagnostic DEX entry");
        var target=output.resolve(entry);need(!Files.exists(target),"Immutable output exists");DexPool.writeTo(target.toString(),new ImmutableDexFile(dex.getOpcodes(),rewritten));
        var reread=DexFileFactory.loadDexFile(target.toFile(),dex.getOpcodes());int checked=0;
        for(var c:reread.getClasses()){
          need(expected.get(c.getType()).equals(canonical(ImmutableClassDef.of(c))),"Unexpected DEX roundtrip drift: "+c.getType());checked++;
          for(var m:c.getMethods()){
            var id=c.getType()+"->"+m.getName();if(firebaseBefore.containsKey(id)){need(firebaseBefore.get(id).equals(canonical(ImmutableMethod.of(m))),"Firebase callback changed");firebase++;}
          }
        }
        need(checked==expected.size(),"DEX class count changed");
      }else need(firebaseBefore.isEmpty(),"Expected Firebase callbacks outside rewritten DEX");
    }
    need(restored==2&&firebase==4,"Unexpected target counts");
    try(var module=ApkModule.loadApkFile(source)){
      module.setLoadDefaultFramework(false);var manifest=module.getAndroidManifest();var root=manifest.getManifestElement();var app=manifest.getApplicationElement();
      need(root.removeAttributesWithName("requiredSplitTypes"),"Missing requiredSplitTypes");
      need(app.removeAttributesWithId(0x01010591),"Missing isSplitRequired");
      int[] metadata={0};
      app.removeElementsIf(node->{var element=(ResXmlElement)node;if(!element.getName().equals("meta-data"))return false;var name=element.searchAttributeByName("name");if(name==null)return false;var value=name.getValueAsString();boolean remove=value.equals("com.android.vending.splits")||value.equals("com.android.vending.splits.required");if(remove)metadata[0]++;return remove;});
      need(metadata[0]==2,"Missing split metadata");manifest.refreshFull();Files.write(output.resolve("AndroidManifest.xml"),manifest.getBytes());
    }
    Files.writeString(output.resolve("dex-proof.json"),"{\"success\":true,\"restoredDiagnosticMethods\":["+String.join(",",notes)+"],\"classesChecked\":"+classes+",\"allOtherV9MethodsPreserved\":true,\"firebaseOfficialCallbacksPreserved\":"+firebase+",\"vmClassEqualsV7\":true,\"remainingVmMethods\":["+String.join(",",callbacks)+"]}\n");
    System.out.println("restoredDiagnostics=2 firebaseCallbacksPreserved=4 allOtherV9MethodsPreserved=true");
  }
}
