import com.reandroid.apk.ApkModule;
import com.reandroid.arsc.chunk.*;
import com.reandroid.arsc.value.*;
import java.nio.file.*;
import java.util.*;

/** Add one loopback network resource and its manifest reference; no DEX/package edits. */
public class LoopbackContainer {
 static SortedMap<String,String> snapshot(PackageBlock pkg){
  var rows=new TreeMap<String,String>();var specs=pkg.getSpecTypePairs();
  while(specs.hasNext())for(TypeBlock type:specs.next())for(Entry e:type){
   if(e!=null&&e.getName()!=null)rows.put(String.format("%08x",e.getResourceId())+":"+type.getQualifiers(),e.toJson().toString());
  }
  return rows;
 }
 public static void main(String[] args)throws Exception{
  Path out=Path.of(args[1]);Files.createDirectories(out);
  try(var module=ApkModule.loadApkFile(new java.io.File(args[0]))){
   module.setLoadDefaultFramework(false);var manifest=module.getAndroidManifest();
   var table=module.getTableBlock();var packages=table.getPackages();var pkg=packages.next();
   if(packages.hasNext()||!pkg.getName().equals("jp.goodsmile.brsfragment"))throw new IllegalStateException("Package changed");
   var before=snapshot(pkg);var xml=pkg.getOrCreateTypeBlock("","xml");
   if(xml.getTypeId()!=15||xml.getEntry("network_security_config")!=null)throw new IllegalStateException("Unexpected XML type");
   var entry=xml.getOrCreateEntry(2);if(entry.getName()!=null)throw new IllegalStateException("Resource slot occupied");
   entry.setValueAsString("res/xml/network_security_config.xml");entry.setName("network_security_config");
   var app=manifest.getApplicationElement();
   if(app.searchAttributeByName("networkSecurityConfig")!=null)throw new IllegalStateException("Existing network config");
   var attr=app.getOrCreateAndroidAttribute("networkSecurityConfig",0x01010527);attr.setValueType(ValueType.REFERENCE);attr.setData(entry.getResourceId());
   table.refreshFull();manifest.refreshFull();var after=snapshot(pkg);
   for(var row:before.entrySet())if(!row.getValue().equals(after.get(row.getKey())))throw new IllegalStateException("Original resource changed: "+row.getKey());
   if(after.size()!=before.size()+1||entry.getResourceId()!=0x7f0f0002)throw new IllegalStateException("Unexpected added resources");
   Files.write(out.resolve("resources.arsc"),table.getBytes());Files.write(out.resolve("AndroidManifest.xml"),manifest.getBytes());
   System.out.println("originalResourcesPreserved="+before.size()+" addedNetworkConfig=0x7f0f0002 packageUnchanged=true");
  }
 }
}
