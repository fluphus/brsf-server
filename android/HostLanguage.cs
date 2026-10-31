using Android.Content;
using System.Text.Json;

namespace Brs.AndroidLocalHost;

// This preference belongs to the host UI only; game data and protocol fields are untouched.
internal static class HostLanguage
{
    private const string Key = "ui-language";
    private static ISharedPreferences Store => Android.App.Application.Context.GetSharedPreferences("brsf-ui", FileCreationMode.Private)!;
    public static event Action? Changed;
    public static string Preference => Normalize(Store.GetString(Key, "system"));
    public static string Normalize(string? value) => value is "zh" or "ja" or "en" ? value : "system";
    public static string Match(string? tag) => tag?.Split('-', '_')[0].ToLowerInvariant() switch { "zh" => "zh", "ja" => "ja", _ => "en" };
    public static string[] SystemLanguages
    {
        get
        {
            var locales = Android.Content.Res.Resources.System?.Configuration?.Locales;
            return locales is null || locales.IsEmpty ? [Java.Util.Locale.Default?.ToLanguageTag() ?? "en"] :
                Enumerable.Range(0, locales.Size()).Select(i => locales.Get(i)?.ToLanguageTag() ?? "en").ToArray();
        }
    }
    public static string Language => Preference == "system" ? Match(SystemLanguages.FirstOrDefault()) : Preference;
    public static bool Set(string? value)
    {
        if (value is not ("system" or "zh" or "ja" or "en")) return false;
        if (!Store.Edit()!.PutString(Key, value)!.Commit()) return false;
        Changed?.Invoke(); return true;
    }
    public static bool Handle(string? senderUrl, string? currentUrl, string? value) =>
        NativeCredentials.IsRoot(senderUrl) && NativeCredentials.IsRoot(currentUrl) && value is { Length: <= 6 } && Set(value);
    public static string Text(string source)
    {
        if (!Messages.TryGetValue(source, out var value)) return source;
        return Language switch { "zh" => value.Zh, "ja" => value.Ja, _ => value.En };
    }
    public static string Format(string source, params object[] values) => string.Format(Text(source), values);
    public static string Script => "window.BrsHostLanguage=" + JsonSerializer.Serialize(new { preference = Preference, language = Language, systemLanguages = SystemLanguages }) +
        ";window.BrsHost={setLanguage:function(p){return JSON.parse(window.prompt('BRSF_LANGUAGE_V1',p)||'{\"ok\":false}').ok;}};" +
        "window.dispatchEvent(new CustomEvent('brs-language-change',{detail:window.BrsHostLanguage}));";
    public static string Display(string source)
    {
        return string.Join("\n", source.Split('\n').Select(DisplayLine));
    }
    private static string DisplayLine(string source)
    {
        if (Messages.ContainsKey(source)) return Text(source);
        // Match only known complete messages and structured fields. Never rewrite
        // substrings in exception details, filenames, identifiers, or other data.
        foreach (var prefix in new[] { "启动服务失败：", "操作失败：", "未清理临时导入：" })
            if (source.StartsWith(prefix, StringComparison.Ordinal)) return Text(prefix) + source[prefix.Length..];
        const string ports = "游戏18445 / 管理18180；";
        if (source.StartsWith(ports, StringComparison.Ordinal)) return Text(ports) + Text(source[ports.Length..]);
        var separator = source.IndexOf(" / ", StringComparison.Ordinal);
        if (separator > 0 && Messages.ContainsKey(source[..separator])) return Text(source[..separator]) + " / " + Text(source[(separator + 3)..]);
        var patterns = new (string Pattern, string Template)[]
        {
            (@"^文件已保存（(\d+) 字节）。$", "文件已保存（{0} 字节）。"),
            (@"^资源导入/取消清理中，已读取(\d+) MiB。$", "资源导入/取消清理中，已读取{0} MiB。"),
            (@"^已清理(\d+)个未完成导入目录。$", "已清理{0}个未完成导入目录。"),
            (@"^保存失败（([^）]*)）；目标文件可能为空或不完整，请删除或另选文件重试。$", "保存失败（{0}）；目标文件可能为空或不完整，请删除或另选文件重试。")
        };
        foreach (var (pattern, template) in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(source, pattern);
            if (match.Success) return Format(template, match.Groups[1].Value);
        }
        var status = System.Text.RegularExpressions.Regex.Match(source, @"^API=(True|False)；资源=(True|False)；后端=(True|False)。$");
        if (status.Success) return Format("API={0}；资源={1}；后端={2}。", Enumerable.Range(1, 3).Select(i => (object)Text(status.Groups[i].Value == "True" ? "是" : "否")).ToArray());
        return source;
    }
    private static readonly Dictionary<string, (string Zh, string Ja, string En)> Messages = Build();
    private static Dictionary<string, (string Zh, string Ja, string En)> Build()
    {
        var result = new Dictionary<string, (string, string, string)>(StringComparer.Ordinal);
        foreach (var line in Catalog.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('|');
            if (fields.Length == 3) result.Add(fields[0], (fields[0], fields[1], fields[2]));
            else if (fields.Length == 4) result.Add(fields[0], (fields[1], fields[2], fields[3]));
            else throw new InvalidDataException("Invalid host language catalog.");
        }
        return result;
    }
    private const string Catalog = """
BRS 本机服务|BRS ローカルサービス|BRS Local Host
请显式启动。HTTP18445 / 管理18180；仅本机回环。|起動ボタンを押してください。HTTP18445 / 管理18180。端末内のみで接続します。|Press Start. HTTP18445 / Admin18180; connections stay on this device.
启动|起動|Start
停止|停止|Stop
状态|状態|Status
打开管理|管理画面|Management
导入资源包|リソースを取り込む|Import resources
清理未完成导入|未完了の取り込みを削除|Clean incomplete imports
打开管理后显示全屏管理页面；返回键回到此控制页。资源导入需先停止服务。|管理画面は全画面で開きます。戻るとこの画面に戻ります。リソースの取り込み前にサービスを停止してください。|Management opens full screen. Back returns here. Stop the service before importing resources.
该下载未通过本机保存入口；请使用管理页备份/导出按钮。|このダウンロードは保存できません。管理画面のバックアップ／書き出しボタンを使用してください。|Use the management Backup or Export button to save this file.
请等待当前操作或取消后的清理完成。|現在の操作またはキャンセル後のクリーンアップが終わるまでお待ちください。|Wait for the current operation or cancellation cleanup to finish.
服务已启动或正在停止，请稍后再试。|サービスは起動済みか停止中です。しばらくお待ちください。|The service is running or stopping. Please wait.
已请求启动；关闭页面不停止服务，请用停止按钮或通知结束。|起動を要求しました。画面を閉じても動作します。停止ボタンまたは通知から終了してください。|Start requested. Closing this page keeps the service running. Use Stop or the notification to end it.
启动服务失败：|サービスを起動できません：|Could not start service: 
服务当前未运行。|サービスは停止しています。|The service is not running.
正在停止监听并释放私库；完成前请勿导入资源。|接続待ち受けとデータベースを終了中です。完了までリソースを取り込まないでください。|Stopping listeners and closing the private database. Wait before importing resources.
已有操作，请等待完成。|操作の完了をお待ちください。|Wait for the current operation to finish.
操作已取消。|操作をキャンセルしました。|Operation canceled.
操作失败：|操作に失敗しました：|Operation failed: 
API={0}；资源={1}；后端={2}。|API={0}、リソース={1}、バックエンド={2}。|API={0}; resources={1}; backend={2}.
请先启动并等待本机API就绪。|サービスを起動し、APIの準備完了をお待ちください。|Start the service and wait for the local API to be ready.
页面已关闭。|画面は閉じられました。|The page has closed.
正在打开管理页面。|管理画面を開いています。|Opening management.
管理页面连接失败。请确认本机服务已启动，再点打开管理重试。|管理画面に接続できません。ローカルサービスを起動し、管理画面ボタンでやり直してください。|Could not connect to management. Start the local service, then tap Management to retry.
请先停止服务并等待释放，然后导入资源包。|サービスを停止して終了を待ってから、リソースパッケージを取り込んでください。|Stop the service and wait for it to close before importing the resource package.
请等待当前导入/清理或服务释放。|取り込み、クリーンアップ、またはサービスの終了をお待ちください。|Wait for import, cleanup, or service shutdown to finish.
无法打开系统文件选择器，本次未导入。|システムのファイル選択画面を開けません。取り込みは行われていません。|Could not open the system file picker. Nothing was imported.
操作已中断；本次未写入，请重新选择文件。|操作が中断されました。書き込みは行われていません。ファイルを選び直してください。|Operation interrupted. Nothing was written. Select the file again.
已取消保存，应用未写入文件。|保存をキャンセルしました。ファイルには書き込んでいません。|Save canceled. The app did not write a file.
已取消资源选择。|リソースの選択をキャンセルしました。|Resource selection canceled.
请选择系统文件选择器中的文件。|システムのファイル選択画面からファイルを選んでください。|Choose a file from the system file picker.
文件已保存（{0} 字节）。|ファイルを保存しました（{0} バイト）。|File saved ({0} bytes).
正在后台验证资源包；约8GB，请等待，验证通过后才发布。|リソースパッケージをバックグラウンドで検証中です（約8GB）。検証完了までお待ちください。|Validating the resource package in the background (about 8GB). Resources become available only after validation.
归档验证并导入完成，请显式启动后查看资源就绪状态。|検証と取り込みが完了しました。サービスを起動してリソースの状態を確認してください。|Archive validated and imported. Start the service to check resource readiness.
请先启动本机服务并等待当前操作完成。|ローカルサービスを起動し、現在の操作が終わるまでお待ちください。|Start the local service and wait for the current operation to finish.
无法打开系统保存窗口，应用未写入文件。|システムの保存画面を開けません。書き込みは行われていません。|Could not open the system save dialog. Nothing was written.
保存已取消；目标文件可能为空或不完整，请删除或另选文件重试。|保存をキャンセルしました。保存先は空または不完全な可能性があります。削除するか別のファイルでやり直してください。|Save canceled. The destination may be empty or incomplete. Delete it or retry with another file.
操作已取消，正在结束本次临时数据清理。|操作をキャンセルしました。一時データの削除を終了しています。|Operation canceled. Finishing temporary data cleanup.
保存失败（{0}）；目标文件可能为空或不完整，请删除或另选文件重试。|保存に失敗しました（{0}）。保存先は空または不完全な可能性があります。削除するか別のファイルでやり直してください。|Save failed ({0}). The destination may be empty or incomplete. Delete it or retry with another file.
请先停止服务并等待当前操作完成。|サービスを停止し、現在の操作が終わるまでお待ちください。|Stop the service and wait for the current operation to finish.
已有资源导入/清理或服务正在释放。|取り込み、クリーンアップ、またはサービスの終了が進行中です。|Import, cleanup, or service shutdown is already in progress.
没有可清理的未完成导入。|削除できる未完了の取り込みはありません。|There are no incomplete imports to clean.
仅删除{0}个未完成导入的临时目录，约{1} MiB。已导入资源和账号数据保留。|未完了の取り込みの一時フォルダー{0}件（約{1} MiB）のみを削除します。取り込み済みリソースとアカウントは保持されます。|Delete only {0} incomplete import folders (about {1} MiB). Imported resources and accounts are kept.
取消|キャンセル|Cancel
清理|削除|Clean
已清理{0}个未完成导入目录。|未完了の取り込みフォルダー{0}件を削除しました。|Cleaned {0} incomplete import folders.
未清理临时导入：|一時データを削除できません：|Could not clean temporary imports: 
引继资料|引き継ぎ情報|Transfer credentials
请通过管理页下载按钮打开Android保存窗口；本次未保存文件。|管理画面のダウンロードボタンからAndroidの保存画面を開いてください。ファイルは保存されていません。|Use a management download button to open the Android save dialog. No file was saved.
等待选择资源归档。|リソースアーカイブを選んでください。|Waiting for a resource archive selection.
资源导入/取消清理中，已读取{0} MiB。|取り込み／キャンセル後の削除中。{0} MiB読み込み済み。|Import or cancellation cleanup in progress. Read {0} MiB.
正在确认或清理未完成导入。|未完了の取り込みを確認または削除しています。|Confirming or cleaning incomplete imports.
等待选择导出保存位置。|書き出し先を選んでください。|Waiting for an export destination.
正在保存导出文件。|書き出したファイルを保存中です。|Saving the exported file.
服务未启动|サービスは停止しています|Service not started
正在验证本机数据；资源未验证前不可游玩|ローカルデータを検証中です。リソースの検証完了後にプレイできます|Verifying local data. Play is available after resource validation.
请等待资源导入/清理完成后再启动。|取り込み／クリーンアップの完了後に起動してください。|Wait for resource import or cleanup before starting.
API已启动：HTTP18445 / 管理18180|API起動済み：HTTP18445 / 管理18180|API ready: HTTP18445 / Admin18180
本机服务正在准备或关闭|ローカルサービスを準備または終了しています|Local service is preparing or stopping
停止服务|サービスを停止|Stop service
游戏18445 / 管理18180；|ゲーム18445 / 管理18180；|Game18445 / Admin18180; 
API已启动|API起動済み|API ready
API未就绪|API準備中|API not ready
语言|言語|Language
跟随系统|システム設定|System default
是|はい|Yes
否|いいえ|No
created|待命|待機中|Idle
verifying-fixed-inputs|正在验证固定数据|固定データを検証中|Verifying fixed data
validating-store-domain|正在验证存储数据|保存データを検証中|Validating stored data
loading-supported-cipher|正在准备加密模块|暗号化処理を準備中|Preparing encryption
composing-real-runtime|正在准备后端|バックエンドを準備中|Preparing backend
opening-imported-content|正在打开已导入资源|取り込み済みリソースを開いています|Opening imported resources
binding-explicit-loopback-ports|正在打开本机连接|端末内の接続を準備中|Opening local connections
api-ready-content-absent|API已就绪；尚未导入资源|API起動済み・リソース未取り込み|API ready; resources not imported
api-and-content-ready|API和资源已就绪|APIとリソースの準備完了|API and resources ready
stopping|正在停止|停止中|Stopping
stopped|已停止|停止済み|Stopped
failed|失败|失敗|Failed
cleanup-failed|清理失败|クリーンアップ失敗|Cleanup failed
context-failed|服务准备失败|サービスを準備できません|Service setup failed
store_domain_marker_required|缺少存储数据身份标记|保存データの識別情報がありません|Stored data identity is missing
store_domain_identity_mismatch|存储数据身份标记不匹配|保存データの識別情報が一致しません|Stored data identity does not match
store_domain_marker_invalid|存储数据身份标记无效|保存データの識別情報が無効です|Stored data identity is invalid
setup-required-resource-package|请先停止服务，导入完整资源包，再启动。|サービスを停止し、完全なリソースパッケージを取り込んでから起動してください。|Stop the service, import the complete resource package, then start.
""";
}
