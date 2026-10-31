using Brs.AndroidLocalHost;
using System.Text.Json;

var checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; }
void SystemLanguage(params string[] tags) => Android.Content.Res.Resources.System.Configuration.Locales.Tags = tags;
const string origin = "http://127.0.0.1:18180/";
Check(HostLanguage.Preference == "system", "clean installation defaults to system");
foreach (var (tag, language) in new[] { ("zh-Hans-CN", "zh"), ("zh-TW", "zh"), ("ja-JP", "ja"), ("en-GB", "en"), ("fr-FR", "en") })
{
    SystemLanguage(tag); Check(HostLanguage.Language == language, "system resolution " + tag);
}
SystemLanguage("fr-FR", "ja-JP"); Check(HostLanguage.Language == "en", "first locale fallback English");
SystemLanguage("ja-JP"); Check(HostLanguage.Language == "ja", "system language change takes effect");
Check(HostLanguage.Text("启动") == "起動", "Japanese text");
Check(HostLanguage.Display("文件已保存（123 字节）。") == "ファイルを保存しました（123 バイト）。", "formatted download outcome");
Check(HostLanguage.Display("API=True；资源=False；后端=False。").Contains("リソース=いいえ"), "structured boolean status");
Check(HostLanguage.Handle(origin, origin, "zh"), "bridge accepts exact management root");
Check(HostLanguage.Preference == "zh" && HostLanguage.Language == "zh", "manual override persists");
Check(Android.App.Application.Context.Preferences.GetString("ui-language", "") == "zh", "committed preference");
Check(HostLanguage.Display("created") == "待命", "Chinese stage translation");
Check(HostLanguage.Display("api-ready-content-absent") == "API已就绪；尚未导入资源", "Chinese ready status");
Check(HostLanguage.Display("setup-required-resource-package") == "请先停止服务，导入完整资源包，再启动。", "clean installation guides resource import");
var data = "是否存在 fixtures/是/失败.json exception-value created-user";
Check(HostLanguage.Display(data) == data, "freeform details unchanged");
Check(HostLanguage.Display("操作失败：是否存在 fixtures/是/失败.json") == "操作失败：是否存在 fixtures/是/失败.json", "error details unchanged");
foreach (var (sender, current) in new[] { ("https://127.0.0.1:18180/", origin), ("http://localhost:18180/", origin), ("http://127.0.0.1:18181/", origin), (origin + "page", origin), (origin, "https://example.test/"), (origin, origin + "?x") })
{
    Check(!HostLanguage.Handle(sender, current, "en") && HostLanguage.Preference == "zh", "origin rejection " + sender + " " + current);
}
foreach (var input in new string?[] { null, "auto", "zh-CN", "EN", "system ", "<script>" }) Check(!HostLanguage.Handle(origin, origin, input), "invalid preference rejected");
Check(HostLanguage.Set("en"), "English preference");
Check(HostLanguage.Display("setup-required-resource-package") == "Stop the service, import the complete resource package, then start.", "English import guidance");
Check(HostLanguage.Display("操作失败：fixtures/是/失败.json") == "Operation failed: fixtures/是/失败.json", "prefix only translated");
Check(HostLanguage.Display("游戏18445 / 管理18180；API已启动") == "Game18445 / Admin18180; API ready", "port/status line");
Check(HostLanguage.Display("保存失败（IOException）；目标文件可能为空或不完整，请删除或另选文件重试。").StartsWith("Save failed (IOException)."), "failure format");
Check(HostLanguage.Set("system"), "return to system");
SystemLanguage("zh-HK"); Check(HostLanguage.Language == "zh" && HostLanguage.Preference == "system", "resolved language never overwrites system");
Check(HostLanguage.Script.Contains("\"preference\":\"system\"") && HostLanguage.Script.Contains("systemLanguages"), "bridge preserves preference and system locales");
var navigation = new ManagementNavigation();
Check(navigation.CanResume(origin), "healthy retained management page resumes");
Check(!navigation.OnError(false, origin) && navigation.CanResume(origin), "subresource failure keeps retained page");
Check(!navigation.OnError(true, "https://example.test/") && navigation.CanResume(origin), "untrusted document failure ignored");
Check(navigation.OnError(true, origin) && !navigation.CanResume(origin), "failed management main frame requires reload");
Check(!navigation.CanResume("about:blank"), "blank view never treated as management");
navigation.BeginLoad();
Check(navigation.CanResume(origin), "retry resets failed load state");
Console.WriteLine($"Host language checks passed: {checks}");
if (args.Length > 0)
{
    var assembly = System.Reflection.Assembly.LoadFrom(Path.GetFullPath(args[0]));
    var assetsType = assembly.GetType("Brs.Local.Management.ManagementAssets", true)!;
    var bundled = assetsType.GetMethod("LoadBundled")!.Invoke(null, null)!;
    var lookup = assetsType.GetMethod("TryGet")!;
    var source = Path.GetFullPath(args.Length > 1 ? args[1] : "webui");
    var paths = new[] { "index.html", "app.js", "api.js", "styles.css", "i18n.js", "catalog-locales.js" };
    foreach (var name in paths)
    {
        object?[] lookupArgs = [name == "index.html" ? "/" : "/" + name, null];
        Check((bool)lookup.Invoke(bundled, lookupArgs)!, "compiled asset served " + name);
        var asset = ((byte[] Bytes, string ContentType))lookupArgs[1]!;
        Check(asset.Bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(source, name))), "compiled asset matches source " + name);
    }
    Check(assembly.GetManifestResourceNames().Count(n => n.StartsWith("Brs.Local.ManagementWeb.", StringComparison.Ordinal)) == 6, "compiled assembly has exactly six UI resources");
    Console.WriteLine("Compiled management resource checks passed: 13");
}


