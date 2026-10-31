namespace Android.Content
{
    public enum FileCreationMode { Private }
    public interface ISharedPreferences { string? GetString(string key, string fallback); Editor Edit(); }
    public sealed class Editor(MemoryPreferences store)
    {
        private string? key, value;
        public Editor PutString(string name, string text) { key = name; value = text; return this; }
        public bool Commit() { store.Data[key!] = value!; return true; }
    }
    public sealed class MemoryPreferences : ISharedPreferences
    {
        public Dictionary<string, string> Data { get; } = new();
        public string? GetString(string key, string fallback) => Data.GetValueOrDefault(key, fallback);
        public Editor Edit() => new(this);
    }
    public sealed class Context
    {
        public MemoryPreferences Preferences { get; } = new();
        public ISharedPreferences GetSharedPreferences(string key, FileCreationMode mode) => Preferences;
    }
}
namespace Android.App { public static class Application { public static Android.Content.Context Context { get; } = new(); } }
namespace Java.Util
{
    public sealed class Locale(string tag)
    {
        public static Locale? Default { get; set; } = new("en-US");
        public string ToLanguageTag() => tag;
    }
}
namespace Android.Content.Res
{
    public sealed class LocaleList
    {
        public string[] Tags { get; set; } = ["en-US"];
        public bool IsEmpty => Tags.Length == 0;
        public int Size() => Tags.Length;
        public Java.Util.Locale Get(int index) => new(Tags[index]);
    }
    public sealed class Configuration { public LocaleList Locales { get; } = new(); }
    public sealed class Resources
    {
        public static Resources System { get; } = new();
        public Configuration Configuration { get; } = new();
    }
}
