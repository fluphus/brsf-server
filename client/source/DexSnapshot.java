import com.android.tools.smali.dexlib2.*;
import com.android.tools.smali.dexlib2.iface.*;
import com.android.tools.smali.dexlib2.immutable.*;
import java.io.*;
import java.lang.reflect.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

/** Independent read-only dexlib snapshot. No writer, optimizer, or patch API. */
public class DexSnapshot {
  static String quote(String text) {
    StringBuilder out=new StringBuilder("\"");
    for(char c:text.toCharArray()) {
      if(c=='"' || c=='\\')out.append('\\').append(c);
      else if(c<32)out.append(String.format("\\u%04x",(int)c));
      else out.append(c);
    }
    return out.append('"').toString();
  }
  static String canonical(Object value) throws Exception {
    if(value==null)return "null";
    if(value instanceof String || value instanceof Character)return quote(value.toString());
    if(value instanceof Boolean)return value.toString();
    if(value instanceof Number) {
      String number=value.toString();
      return number.equals("NaN") || number.contains("Infinity")?quote(number):number;
    }
    if(value instanceof Enum<?>)return quote(((Enum<?>)value).name());
    if(value instanceof Iterable<?>) {
      List<String> elements=new ArrayList<>();
      for(Object element:(Iterable<?>)value)elements.add(canonical(element));
      if(value instanceof Set<?>)Collections.sort(elements);
      return "["+String.join(",",elements)+"]";
    }
    TreeMap<String,String> fields=new TreeMap<>();fields.put("_type",quote(value.getClass().getSimpleName()));
    for(Class<?> c=value.getClass();c!=null && c!=Object.class;c=c.getSuperclass()) {
      for(java.lang.reflect.Field field:c.getDeclaredFields()) {
        if(Modifier.isStatic(field.getModifiers()) || field.isSynthetic())continue;
        field.setAccessible(true);fields.put(field.getName(),canonical(field.get(value)));
      }
    }
    List<String> entries=new ArrayList<>();fields.forEach((k,v)->entries.add(quote(k)+":"+v));return "{"+String.join(",",entries)+"}";
  }
  public static void main(String[] args) throws Exception {
    var container=DexFileFactory.loadDexContainer(new File(args[0]),Opcodes.getDefault());
    int classes=0,methods=0;Set<String> unique=new HashSet<>();
    try(var out=new PrintWriter(new OutputStreamWriter(new FileOutputStream(args[1]),StandardCharsets.UTF_8))) {
      for(String entry:container.getDexEntryNames()) {
        for(ClassDef original:container.getEntry(entry).getDexFile().getClasses()) {
          if(!unique.add(original.getType()))throw new IllegalStateException("Duplicate class: "+original.getType());
          var immutable=ImmutableClassDef.of(original);
          out.println("{\"class\":"+quote(original.getType())+",\"dex_entry\":"+quote(entry)+",\"definition\":"+canonical(immutable)+"}");
          classes++;for(com.android.tools.smali.dexlib2.iface.Method m:original.getMethods())methods++;
        }
      }
    }
    System.out.println("classes="+classes+" methods="+methods+" dex="+container.getDexEntryNames().size());
  }
}
