import com.android.tools.smali.dexlib2.*;
import com.android.tools.smali.dexlib2.iface.*;
import com.android.tools.smali.dexlib2.iface.instruction.*;
import com.android.tools.smali.dexlib2.iface.reference.*;
import com.android.tools.smali.dexlib2.iface.value.*;
import com.android.tools.smali.dexlib2.immutable.*;
import com.android.tools.smali.dexlib2.immutable.instruction.*;
import com.android.tools.smali.dexlib2.immutable.value.*;
import com.android.tools.smali.dexlib2.writer.pool.DexPool;
import java.io.*;
import java.nio.*;
import java.nio.charset.*;
import java.nio.file.*;
import java.security.*;
import java.util.*;

/** Phase37 offline, fail-closed holder restoration. Does not invoke any device API. */
public final class RestoreHolders {
 static final Set<String> PATCHES=Set.of(
  "Lcom/pairip/StartupLauncher;->launch()V",
  "Lcom/pairip/SignatureCheck;->verifyIntegrity(Landroid/content/Context;)V",
  "Lcom/pairip/licensecheck3/LicenseClientV3;->onActivityCreate(Landroid/app/Activity;)V");
 record Value(int flags,String text) {}
 static String fieldKey(com.android.tools.smali.dexlib2.iface.Field f){return f.getDefiningClass()+"->"+f.getName()+":"+f.getType();}
 static String methodKey(MethodReference m){return m.getDefiningClass()+"->"+m.getName()+"("+String.join("",m.getParameterTypes())+")"+m.getReturnType();}
 static void require(boolean condition,String message){if(!condition)throw new IllegalStateException(message);}
 static String hash(byte[] bytes)throws Exception{return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes));}
 static String q(String text){StringBuilder s=new StringBuilder("\"");for(char c:text.toCharArray()){if(c=='"'||c=='\\')s.append('\\').append(c);else if(c<32||c>126)s.append(String.format("\\u%04x",(int)c));else s.append(c);}return s.append('"').toString();}
 // Exact Java UTF-16 code units, including NUL and isolated surrogates. No Charset replacement.
 static String decode(String b64){byte[] bytes=Base64.getDecoder().decode(b64);require(bytes.length%2==0,"Odd UTF16 byte length");char[] chars=new char[bytes.length/2];for(int i=0;i<chars.length;i++)chars[i]=(char)(((bytes[2*i]&255)<<8)|(bytes[2*i+1]&255));return new String(chars);}
 static byte[] encode(String text){byte[] out=new byte[text.length()*2];for(int i=0;i<text.length();i++){out[2*i]=(byte)(text.charAt(i)>>>8);out[2*i+1]=(byte)text.charAt(i);}return out;}
 static Map<String,Value> load(Path path,boolean preflight)throws Exception{
  Map<String,Value> out=new TreeMap<>();for(String line:Files.readAllLines(path,StandardCharsets.US_ASCII)){
   String[] p=line.split("\t",-1);require(p.length==3,"Invalid TSV row");require(p[0].endsWith(":Ljava/lang/String;"),"Invalid field type");
   Value v=new Value(Integer.parseInt(p[1]),preflight?null:decode(p[2]));require(out.put(p[0],v)==null,"Duplicate field "+p[0]);
  }require(out.size()==1448,"Expected 1448 exact fields");return out;
 }
 static Method patch(Method m){
  require(m.getImplementation()!=null&&m.getReturnType().equals("V"),"Unexpected patch source "+methodKey(m));
  var code=new ImmutableMethodImplementation(m.getImplementation().getRegisterCount(),List.of(new ImmutableInstruction10x(Opcode.RETURN_VOID)),List.of(),List.of());
  return new ImmutableMethod(m.getDefiningClass(),m.getName(),m.getParameters(),m.getReturnType(),m.getAccessFlags(),m.getAnnotations(),m.getHiddenApiRestrictions(),code);
 }
 static String canonical(ClassDef c)throws Exception{return DexSnapshot.canonical(ImmutableClassDef.of(c));}
 public static void main(String[] args)throws Exception{
  require(args.length==4,"Usage: RestoreHolders preflight|build base.apk fields.tsv output-directory");
  boolean preflight=args[0].equals("preflight");require(preflight||args[0].equals("build"),"Invalid mode");Path out=Path.of(args[3]);Files.createDirectories(out);
  Map<String,Value> values=load(Path.of(args[2]),preflight);Set<String> holders=new TreeSet<>();for(String key:values.keySet())holders.add(key.substring(0,key.indexOf("->")));require(holders.size()==32,"Expected 32 holders");
  var input=DexFileFactory.loadDexContainer(new File(args[1]),Opcodes.getDefault());Set<String> seenFields=new TreeSet<>(),seenPatches=new TreeSet<>(),seenClasses=new TreeSet<>();int count=0,changed=0;
  try(var changes=Files.newBufferedWriter(out.resolve("dex-semantic-diff.jsonl"));var dependencies=Files.newBufferedWriter(out.resolve("pairip-references.jsonl"));var proofs=Files.newBufferedWriter(out.resolve("field-roundtrip-proof.jsonl"));var selected=Files.newBufferedWriter(out.resolve("startup-selected-classes.jsonl"))){
   for(String entry:input.getDexEntryNames()){
    var source=input.getEntry(entry).getDexFile();List<ClassDef> result=new ArrayList<>();Map<String,String> expected=new TreeMap<>();
    for(ClassDef c:source.getClasses()){
     require(seenClasses.add(c.getType()),"Duplicate class "+c.getType());count++;List<com.android.tools.smali.dexlib2.iface.Field> fields=new ArrayList<>();List<Method> methods=new ArrayList<>();boolean edit=false;
     if(Set.of("La/k/b;","La/k/a;","Landroidx/core/app/CoreComponentFactory;","Lcom/pairip/VMRunner;","Lcom/pairip/StartupLauncher;","Lcom/pairip/SignatureCheck;","Lcom/pairip/licensecheck3/LicenseClientV3;").contains(c.getType()))selected.write("{\"class\":"+q(c.getType())+",\"definition\":"+canonical(c)+"}\n");
     if(holders.contains(c.getType())){require(!c.getMethods().iterator().hasNext(),"Holder unexpectedly has methods "+c.getType());require(!c.getInstanceFields().iterator().hasNext(),"Holder unexpectedly has instance fields");}
     for(var f:c.getFields()){
      String key=fieldKey(f);Value v=values.get(key);
      if(v!=null){require(holders.contains(c.getType()),"Holder inventory mismatch");require(seenFields.add(key),"Repeated field "+key);require(f.getAccessFlags()==v.flags()&&(f.getAccessFlags()&8)!=0,"Field flags mismatch "+key);require(f.getInitialValue()==null||f.getInitialValue() instanceof NullEncodedValue,"Expected original null field "+key);
       if(!preflight){f=new ImmutableField(f.getDefiningClass(),f.getName(),f.getType(),f.getAccessFlags(),new ImmutableStringEncodedValue(v.text()),f.getAnnotations(),f.getHiddenApiRestrictions());edit=true;}
      }else require(!holders.contains(c.getType()),"Unlisted holder field "+key);fields.add(f);
     }
     for(Method m:c.getMethods()){
      String key=methodKey(m);if(PATCHES.contains(key)){require(seenPatches.add(key),"Duplicate patch target");if(!preflight){m=patch(m);edit=true;}}
      if(m.getImplementation()!=null){int index=0;for(Instruction ins:m.getImplementation().getInstructions()){
       if(ins instanceof ReferenceInstruction r){var ref=r.getReference();String target=null;if(ref instanceof MethodReference mr&&mr.getDefiningClass().startsWith("Lcom/pairip/"))target=methodKey(mr);else if(ref instanceof TypeReference tr&&tr.getType().startsWith("Lcom/pairip/"))target=tr.getType();else if(ref instanceof FieldReference fr&&fr.getDefiningClass().startsWith("Lcom/pairip/"))target=fr.toString();if(target!=null)dependencies.write("{\"caller\":"+q(key)+",\"index\":"+index+",\"opcode\":"+q(ins.getOpcode().name())+",\"target\":"+q(target)+"}\n");}index++;
      }}methods.add(m);
     }
     var next=edit?new ImmutableClassDef(c.getType(),c.getAccessFlags(),c.getSuperclass(),c.getInterfaces(),c.getSourceFile(),c.getAnnotations(),fields,methods):c;result.add(next);
     if(!preflight){String canon=canonical(next);expected.put(c.getType(),canon);if(edit){changed++;changes.write("{\"class\":"+q(c.getType())+",\"dex\":"+q(entry)+",\"before_sha256\":"+q(hash(canonical(c).getBytes(StandardCharsets.UTF_8)))+",\"after_sha256\":"+q(hash(canon.getBytes(StandardCharsets.UTF_8)))+"}\n");}}
    }
    if(!preflight){Path dex=out.resolve(entry);require(!Files.exists(dex),"Refusing to replace DEX");DexPool.writeTo(dex.toString(),new ImmutableDexFile(source.getOpcodes(),result));var reread=DexFileFactory.loadDexFile(dex.toFile(),source.getOpcodes());int checked=0;
     for(ClassDef c:reread.getClasses()){require(Objects.equals(expected.get(c.getType()),canonical(c)),"DEX roundtrip semantic difference "+c.getType());checked++;for(var f:c.getFields()){var v=values.get(fieldKey(f));if(v!=null){require(f.getInitialValue() instanceof StringEncodedValue,"Roundtrip nonstring");String text=((StringEncodedValue)f.getInitialValue()).getValue();require(v.text().equals(text),"UTF16 roundtrip mismatch "+fieldKey(f));proofs.write("{\"key\":"+q(fieldKey(f))+",\"utf16_length\":"+text.length()+",\"utf16be_sha256\":"+q(hash(encode(text)))+",\"exact\":true}\n");}}}
     require(checked==expected.size(),"Class inventory changed");
    }
   }
  }
  require(seenFields.equals(values.keySet()),"Missing manifest field(s)");require(seenPatches.equals(PATCHES),"Missing exact patch target(s)");
  Files.writeString(out.resolve("writer-result.json"),"{\"preflight\":"+preflight+",\"classes\":"+count+",\"holders\":"+holders.size()+",\"fields\":"+seenFields.size()+",\"patch_targets\":"+seenPatches.size()+",\"changed_classes\":"+changed+",\"all_reencoded_classes_roundtripped\":"+!preflight+"}\n");
 }
}
