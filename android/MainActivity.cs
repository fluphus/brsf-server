using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using Brs.Portable.Content;
using OperationCanceledException = System.OperationCanceledException;

namespace Brs.AndroidLocalHost;

[Activity(Name = "org.brsfragment.localhost.MainActivity", Label = "@string/app_name", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTask)]
public sealed class MainActivity : Activity
{
    private const int PickArchive = 34;
    private const int SaveExport = 35;
    private TextView? status;
    private WebView? web;
    private ScrollView? controlsPage;
    private FrameLayout? contentFrame;
    private bool showingManagement;
    private static readonly ManagementNavigation navigation = new();
    private Android.Window.IOnBackInvokedCallback? backCallback;
    private string? root;
    private Handler? handler;
    private Action? poll;
    private string message = "请显式启动。HTTP18445 / 管理18180；仅本机回环。";
    private int operation;
    private bool ended;
    private readonly List<(TextView View, string Source)> localizedViews = new();
    private string? displayedLanguage;
    private readonly CancellationTokenSource activityLifetime = new();
    private readonly object pageOwner = new();
    private static readonly MemoryPageSlot<WebView> managementPage = new();
    private static readonly NativeCredentials credentials = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // Android15+ forces edge-to-edge. Keep the existing in-content title,
        // rather than overlaying a framework ActionBar on our native controls.
        ActionBar?.Hide();
        HostLanguage.Changed += LanguageChanged;
        displayedLanguage = HostLanguage.Language;
        contentFrame = new FrameLayout(this);
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(35)) Window!.SetDecorFitsSystemWindows(false);
            contentFrame.SetOnApplyWindowInsetsListener(new SafeContentInsets());
        }
        SetContentView(contentFrame);
        using var folder = new Java.IO.File(FilesDir!, "local-host");
        root = folder.CanonicalPath;
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(16, 12, 16, 8);
        AddText(layout, "BRS 本机服务", 21);
        var first = Row(layout); Button(first, "启动", StartHost); Button(first, "停止", StopHost); Button(first, "状态", () => _ = ShowStatusAsync());
        var second = Row(layout); Button(second, "打开管理", () => _ = OpenManagementAsync()); Button(second, "导入资源包", PickContent);
        var maintenance = Row(layout); Button(maintenance, "清理未完成导入", () => _ = ConfirmImportCleanupAsync());
        var language = Row(layout); Button(language, "语言", ChooseLanguage);
        status = new TextView(this) { TextSize = 12 };
        layout.AddView(status);
        InitializeManagementView();
        AddText(layout, "打开管理后显示全屏管理页面；返回键回到此控制页。资源导入需先停止服务。", 14);
        controlsPage = new ScrollView(this) { FillViewport = true };
        controlsPage.AddView(layout, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        ShowContent(controlsPage);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            backCallback = new BackCallback(BackToControls);
            OnBackInvokedDispatcher!.RegisterOnBackInvokedCallback(0, backCallback);
        }
        handler = new Handler(Looper.MainLooper!);
        poll = () =>
        {
            RefreshLanguageIfNeeded();
            UpdateStatus();
            handler?.PostDelayed(poll!, 1000);
        };
        poll();
        if (managementPage.ShowingManagement) _ = OpenManagementAsync();
    }
    private void InitializeManagementView()
    {
        web = managementPage.Acquire(pageOwner, () => new WebView(new MutableContextWrapper(Android.App.Application.Context)), view =>
        {
            (view.Parent as ViewGroup)?.RemoveView(view);
            ((MutableContextWrapper)view.Context!).SetBaseContext(this);
            WebView.SetWebContentsDebuggingEnabled(false);
            view.SaveEnabled = false; view.SaveFromParentEnabled = false;
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) view.ImportantForAutofill = ImportantForAutofill.NoExcludeDescendants;
            var settings = view.Settings;
            settings.JavaScriptEnabled = true; settings.DomStorageEnabled = true;
            settings.AllowFileAccess = false; settings.AllowContentAccess = false;
            settings.MixedContentMode = MixedContentHandling.NeverAllow;
            settings.JavaScriptCanOpenWindowsAutomatically = false; settings.SetSupportMultipleWindows(false);
            settings.SetGeolocationEnabled(false); settings.MediaPlaybackRequiresUserGesture = true;
            view.SetWebViewClient(new SameOriginClient(this));
            view.SetWebChromeClient(new CredentialChromeClient());
            var weakOwner = new WeakReference<MainActivity>(this);
            view.SetDownloadListener(new UnsupportedDownloads(() =>
            {
                if (!weakOwner.TryGetTarget(out var owner) || owner.ended) return;
                const string notice = "该下载未通过本机保存入口；请使用管理页备份/导出按钮。";
                owner.ShowMessage(notice); Toast.MakeText(owner, HostLanguage.Display(notice), ToastLength.Long)?.Show();
            }));
            CookieManager.Instance!.SetAcceptCookie(true);
            CookieManager.Instance.SetAcceptThirdPartyCookies(view, false);
        });
    }
    private static void UnbindManagementView(WebView view, bool destroy)
    {
        (view.Parent as ViewGroup)?.RemoveView(view);
        view.OnPause();
        view.SetDownloadListener(null);
        view.SetWebViewClient(new SameOriginClient(null));
        view.SetWebChromeClient(new CredentialChromeClient());
        if (view.Context is MutableContextWrapper wrapper) wrapper.SetBaseContext(Android.App.Application.Context);
        if (destroy) { view.StopLoading(); view.Destroy(); view.Dispose(); }
    }
    private static void DestroyLostManagementView(WebView view, bool _)
    {
        (view.Parent as ViewGroup)?.RemoveView(view);
        if (view.Context is MutableContextWrapper wrapper) wrapper.SetBaseContext(Android.App.Application.Context);
        view.Destroy(); view.Dispose();
    }
    protected override void OnPause()
    {
        if (web is not null && managementPage.Owns(pageOwner, web)) web.OnPause();
        base.OnPause();
    }
    protected override void OnResume()
    {
        base.OnResume();
        RefreshLanguageIfNeeded();
        if (web is not null && managementPage.Owns(pageOwner, web)) { web.OnResume(); ApplyWebLanguage(); }
        if (showingManagement && Volatile.Read(ref operation) == 0) _ = OpenManagementAsync();
    }
    private void AddText(LinearLayout parent, string source, float size)
    {
        var view = new TextView(this) { Text = HostLanguage.Text(source), TextSize = size };
        localizedViews.Add((view, source)); parent.AddView(view);
    }
    private void UpdateStatus()
    {
        if (status is not null) status.Text = HostLanguage.Display(LocalHostService.CurrentStatus + "\n" + message + "\n" + NativeOperations.Summary +
            (NativeOperations.LastOutcome == message ? "" : "\n" + NativeOperations.LastOutcome));
    }
    private void ApplyWebLanguage()
    {
        if (web is not null && managementPage.Owns(pageOwner, web) && NativeCredentials.IsRoot(web.Url)) web.EvaluateJavascript(HostLanguage.Script, null);
    }
    private void RefreshLanguageIfNeeded()
    {
        if (displayedLanguage != HostLanguage.Language) ApplyLanguage();
    }
    private void LanguageChanged() => RunOnUiThread(ApplyLanguage);
    private void ApplyLanguage()
    {
        if (ended) return;
        displayedLanguage = HostLanguage.Language;
        Title = HostLanguage.Text("BRS 本机服务");
        foreach (var item in localizedViews) item.View.Text = HostLanguage.Text(item.Source);
        UpdateStatus(); ApplyWebLanguage();
    }
    private void ChooseLanguage()
    {
        string[] values = ["system", "zh", "ja", "en"];
        string[] labels = [HostLanguage.Text("跟随系统"), "中文", "日本語", "English"];
        new AlertDialog.Builder(this)!.SetTitle(HostLanguage.Text("语言"))!
            .SetSingleChoiceItems(labels, Array.IndexOf(values, HostLanguage.Preference), (sender, args) =>
            {
                HostLanguage.Set(values[args.Which]); (sender as Dialog)?.Dismiss();
            })!.SetNegativeButton(HostLanguage.Text("取消"), (_, _) => { })!.Show();
    }
    private LinearLayout Row(LinearLayout parent)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal }; parent.AddView(row); return row;
    }
    private void ShowContent(View content)
    {
        contentFrame!.RemoveAllViews();
        contentFrame.AddView(content, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        contentFrame.RequestApplyInsets();
    }
    private void Button(LinearLayout row, string text, Action action)
    {
        var button = new Button(this) { Text = HostLanguage.Text(text) }; localizedViews.Add((button, text)); button.Click += (_, _) => action();
        row.AddView(button, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
    }
    private void ShowMessage(string text) { if (ended) return; message = text; UpdateStatus(); }
    private void StartHost()
    {
        if (Volatile.Read(ref operation) != 0 || NativeOperations.IsBusy) { ShowMessage("请等待当前操作或取消后的清理完成。"); return; }
        if (LocalHostService.IsActive) { ShowMessage("服务已启动或正在停止，请稍后再试。"); return; }
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
            RequestPermissions([Manifest.Permission.PostNotifications], 34);
        var intent = new Intent(this, typeof(LocalHostService));
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) StartForegroundService(intent); else StartService(intent);
            ShowMessage("已请求启动；关闭页面不停止服务，请用停止按钮或通知结束。");
        }
        catch (Exception error) { ShowMessage("启动服务失败：" + error.GetType().Name); }
    }
    private void StopHost()
    {
        if (!LocalHostService.IsActive) { ShowMessage("服务当前未运行。"); return; }
        StartService(new Intent(this, typeof(LocalHostService)).SetAction(LocalHostService.StopAction));
        ShowMessage("正在停止监听并释放私库；完成前请勿导入资源。");
    }
    private async Task ExecuteAsync(Func<Task<string>> action)
    {
        if (Interlocked.CompareExchange(ref operation, 1, 0) != 0) { ShowMessage("已有操作，请等待完成。"); return; }
        try { ShowMessage(await action()); }
        catch (OperationCanceledException) { ShowMessage("操作已取消。"); }
        catch (Exception error) { ShowMessage("操作失败：" + error.GetType().Name); }
        finally { Interlocked.Exchange(ref operation, 0); }
    }
    private Task ShowStatusAsync() => ExecuteAsync(async () => await Task.Run(async () =>
    {
        using var client = HostEngine.NewClient();
        var json = await client.GetStringAsync(HostEngine.GameOrigin + "/.well-known/brsf-host", activityLifetime.Token);
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        var value = parsed.RootElement;
        return "API=" + value.GetProperty("apiReady").GetBoolean() + "；资源=" + value.GetProperty("contentReady").GetBoolean() + "；后端=" + value.GetProperty("backendReady").GetBoolean() + "。";
    }));
    private Task OpenManagementAsync() => ExecuteAsync(async () =>
    {
        var alreadyLoaded = web is not null && navigation.CanResume(web.Url);
        if (!LocalHostService.IsReady && !alreadyLoaded) return "请先启动并等待本机API就绪。";
        string? cookie = null;
        if (LocalHostService.IsReady)
        {
            var existingCookie = CookieManager.Instance!.GetCookie(HostEngine.AdminOrigin + "/management/v1/session");
            cookie = await Task.Run(() => HostEngine.OwnerCookieAsync(root!, existingCookie, activityLifetime.Token));
        }
        if (web is null || ended || IsFinishing || !managementPage.Owns(pageOwner, web)) return "页面已关闭。";
        if (cookie is not null)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CookieManager.Instance!.SetCookie(HostEngine.AdminOrigin, cookie, new CookieAccepted(completion));
            if (!await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), activityLifetime.Token)) throw new InvalidOperationException("cookie_rejected");
        }
        if (web is null || ended || !managementPage.Owns(pageOwner, web)) return "页面已关闭。";
        alreadyLoaded = navigation.CanResume(web.Url);
        if (!alreadyLoaded) { navigation.BeginLoad(); web.LoadUrl(NativeCredentials.RootUrl); }
        showingManagement = true;
        managementPage.ShowingManagement = true;
        ShowContent(web);
        web.OnResume();
        ApplyWebLanguage();
        if (alreadyLoaded) web.EvaluateJavascript("window.dispatchEvent(new Event('brsf-management-resume'));", null);
        return "正在打开管理页面。";
    });
    private void PickContent()
    {
        if (LocalHostService.IsActive || Volatile.Read(ref operation) != 0) { ShowMessage("请先停止服务并等待释放，然后导入资源包。"); return; }
        var work = NativeOperations.TryBegin(NativeWorkKind.Import);
        if (work is null) { ShowMessage("请等待当前导入/清理或服务释放。"); return; }
        var intent = new Intent(Intent.ActionOpenDocument); intent.AddCategory(Intent.CategoryOpenable); intent.SetType("*/*"); intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        try { StartActivityForResult(intent, PickArchive); }
        catch { work.Dispose(); ShowMessage("无法打开系统文件选择器，本次未导入。"); }
    }
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode is not (PickArchive or SaveExport)) return;
        var kind = requestCode == PickArchive ? NativeWorkKind.Import : NativeWorkKind.Export;
        var work = NativeOperations.ClaimPending(kind);
        if (work is null) { ReportNativeOutcome("操作已中断；本次未写入，请重新选择文件。"); return; }
        if (resultCode != Result.Ok || data?.Data is not { } uri)
        { work.Dispose(); ReportNativeOutcome(kind == NativeWorkKind.Export ? "已取消保存，应用未写入文件。" : "已取消资源选择。"); return; }
        if (uri.Scheme != "content") { work.Dispose(); ReportNativeOutcome("请选择系统文件选择器中的文件。"); return; }
        var resolver = ContentResolver!;
        if (kind == NativeWorkKind.Export)
        {
            _ = RunNativeWorkAsync(work, async () =>
            {
                if (!LocalHostService.IsReady || work.Export is null) throw new InvalidOperationException("host_not_ready");
                var cookie = CookieManager.Instance!.GetCookie(HostEngine.AdminOrigin + "/management/v1/session");
                var fresh = await Task.Run(() => HostEngine.OwnerCookieAsync(root!, cookie, activityLifetime.Token));
                var nativeCookie = fresh is null ? cookie : fresh.Split(';')[0];
                using var http = HostEngine.NewClient();
                // Large server backups stream with cancellation, not a 30s total deadline.
                http.Timeout = Timeout.InfiniteTimeSpan;
                var count = await Task.Run(() => NativeExportTransfer.SaveAsync(http, work.Export, nativeCookie!,
                    () => resolver.OpenOutputStream(uri, "wt") ?? throw new IOException("document_output_unavailable"), activityLifetime.Token));
                return "文件已保存（" + count + " 字节）。";
            });
            return;
        }
        _ = RunNativeWorkAsync(work, async () =>
        {
            ShowMessage("正在后台验证资源包；约8GB，请等待，验证通过后才发布。");
            await Task.Run(async () =>
            {
                using var input = resolver.OpenInputStream(uri) ?? throw new IOException("document_stream_unavailable");
                await ResourcePackage.ImportAsync(input, PrivateStore.ContentRoot(root!), activityLifetime.Token, new ByteProgress(bytes => work.Bytes = bytes));
            });
            return "归档验证并导入完成，请显式启动后查看资源就绪状态。";
        });
    }
    private void BeginExport(NativeExportSpec export)
    {
        if (!LocalHostService.IsReady || Volatile.Read(ref operation) != 0) { ReportNativeOutcome("请先启动本机服务并等待当前操作完成。"); return; }
        var work = NativeOperations.TryBegin(NativeWorkKind.Export, export);
        if (work is null) { ReportNativeOutcome("已有操作，请等待完成。"); return; }
        var intent = new Intent(Intent.ActionCreateDocument); intent.AddCategory(Intent.CategoryOpenable); intent.SetType(export.Mime);
        intent.PutExtra(Intent.ExtraTitle, export.FileName); intent.AddFlags(ActivityFlags.GrantWriteUriPermission);
        try { StartActivityForResult(intent, SaveExport); }
        catch { work.Dispose(); ReportNativeOutcome("无法打开系统保存窗口，应用未写入文件。"); }
    }
    private void ReportNativeOutcome(string outcome)
    {
        NativeOperations.LastOutcome = outcome; ShowMessage(outcome);
        if (!ended) Toast.MakeText(this, HostLanguage.Display(outcome), ToastLength.Long)?.Show();
    }
    private async Task RunNativeWorkAsync(NativeWork work, Func<Task<string>> action)
    {
        try { ReportNativeOutcome(await action()); }
        catch (OperationCanceledException)
        { ReportNativeOutcome(work.Kind == NativeWorkKind.Export ? "保存已取消；目标文件可能为空或不完整，请删除或另选文件重试。" : "操作已取消，正在结束本次临时数据清理。"); }
        catch (Exception error)
        { ReportNativeOutcome(work.Kind == NativeWorkKind.Export ? "保存失败（" + error.GetType().Name + "）；目标文件可能为空或不完整，请删除或另选文件重试。" : "操作失败：" + error.GetType().Name); }
        finally { work.Dispose(); }
    }
    private async Task ConfirmImportCleanupAsync()
    {
        if (LocalHostService.IsActive || Volatile.Read(ref operation) != 0) { ShowMessage("请先停止服务并等待当前操作完成。"); return; }
        var work = NativeOperations.TryBegin(NativeWorkKind.Cleanup);
        if (work is null) { ShowMessage("已有资源导入/清理或服务正在释放。"); return; }
        try
        {
            var contentRoot = PrivateStore.ContentRoot(root!);
            var stages = await Task.Run(() => ImportStageCleanup.Scan(contentRoot), activityLifetime.Token);
            if (ended || stages.Count == 0) { work.Dispose(); ReportNativeOutcome("没有可清理的未完成导入。"); return; }
            var dialog = new AlertDialog.Builder(this)!.SetTitle(HostLanguage.Text("清理未完成导入"))!
                .SetMessage(HostLanguage.Format("仅删除{0}个未完成导入的临时目录，约{1} MiB。已导入资源和账号数据保留。", stages.Count, stages.Sum(s => s.Bytes) / 1048576))!
                .SetNegativeButton(HostLanguage.Text("取消"), (_, _) => NativeOperations.CancelPending(work))!
                .SetPositiveButton(HostLanguage.Text("清理"), (_, _) =>
                {
                    if (!NativeOperations.Claim(work)) return;
                    _ = RunNativeWorkAsync(work, async () => "已清理" + await Task.Run(() => ImportStageCleanup.DeleteConfirmed(contentRoot, stages, activityLifetime.Token)) + "个未完成导入目录。");
                })!.Create()!;
            dialog.CancelEvent += (_, _) => NativeOperations.CancelPending(work);
            dialog.DismissEvent += (_, _) => NativeOperations.CancelPending(work); dialog.Show();
        }
        catch (Exception error) { work.Dispose(); ReportNativeOutcome("未清理临时导入：" + error.GetType().Name); }
    }
    protected override void OnDestroy()
    {
        HostLanguage.Changed -= LanguageChanged;
        activityLifetime.Cancel();
        NativeOperations.CancelPending(NativeWorkKind.Cleanup);
        if (IsFinishing) NativeOperations.CancelPending();
        ended = true; status = null;
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && backCallback is not null)
            OnBackInvokedDispatcher!.UnregisterOnBackInvokedCallback(backCallback);
        handler?.RemoveCallbacksAndMessages(null); handler?.Dispose(); handler = null;
        if (web is not null) managementPage.Release(pageOwner, web, keep: !IsFinishing, UnbindManagementView);
        web = null;
        base.OnDestroy();
    }
    private void BackToControls()
    {
        if (showingManagement && controlsPage is not null)
        {
            showingManagement = false; managementPage.ShowingManagement = false; web?.OnPause(); ShowContent(controlsPage);
        }
        else Finish();
    }
    public override void OnBackPressed() => BackToControls();
    [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    private sealed class SafeContentInsets : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
        {
            var safe = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout() | WindowInsets.Type.Ime());
            view.SetPadding(safe.Left, safe.Top, safe.Right, safe.Bottom);
            // The outer container handles the obstruction once for both views.
            return WindowInsets.Consumed!;
        }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private sealed class BackCallback(Action back) : Java.Lang.Object, Android.Window.IOnBackInvokedCallback
    {
        public void OnBackInvoked() => back();
    }
    private sealed class CookieAccepted(TaskCompletionSource<bool> completion) : Java.Lang.Object, IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value) => completion.TrySetResult(value is Java.Lang.Boolean answer && answer.BooleanValue());
    }
    private sealed class ByteProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
    private sealed class UnsupportedDownloads(Action reject) : Java.Lang.Object, IDownloadListener
    {
        public void OnDownloadStart(string? url, string? userAgent, string? contentDisposition, string? mimetype, long contentLength) => reject();
    }
    private void RendererGone(WebView view)
    {
        if (!ReferenceEquals(web, view)) return;
        managementPage.Forget(view, DestroyLostManagementView); web = null;
        if (ended) return;
        if (controlsPage is not null) ShowContent(controlsPage);
        InitializeManagementView();
        if (showingManagement) _ = OpenManagementAsync();
    }
    private sealed class CredentialChromeClient : WebChromeClient
    {
        public override bool OnJsPrompt(WebView? view, string? url, string? message, string? defaultValue, JsPromptResult? result)
        {
            if (message == "BRSF_LANGUAGE_V1")
            {
                var ok = HostLanguage.Handle(url, view?.Url, defaultValue);
                result?.Confirm(ok ? "{\"ok\":true}" : "{\"ok\":false}"); return true;
            }
            if (message != NativeCredentials.PromptName) return base.OnJsPrompt(view, url, message, defaultValue, result);
            var reply = credentials.Handle(url, view?.Url, defaultValue, text =>
            {
                if (view is null || !view.HasWindowFocus || !view.IsShown) throw new InvalidOperationException("clipboard_requires_foreground");
                var manager = (Android.Content.ClipboardManager?)Android.App.Application.Context.GetSystemService(ClipboardService) ?? throw new InvalidOperationException("clipboard_unavailable");
                var clip = ClipData.NewPlainText(HostLanguage.Text("引继资料"), text) ?? throw new InvalidOperationException("clipboard_unavailable");
                var extras = new PersistableBundle(); extras.PutBoolean("android.content.extra.IS_SENSITIVE", true);
                clip.Description!.Extras = extras; manager.PrimaryClip = clip;
            });
            result?.Confirm(reply); return true;
        }
    }
    private sealed class SameOriginClient : WebViewClient
    {
        private readonly WeakReference<MainActivity>? owner;
        public SameOriginClient(MainActivity? hostActivity) { if (hostActivity is not null) owner = new(hostActivity); }
        private static bool Allowed(string? url) => NativeExportPolicy.SameOrigin(url);
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            var url = request?.Url?.ToString();
            if (!Allowed(url)) return true;
            var export = NativeExportPolicy.Resolve(url, request?.Method ?? "");
            if (export is not null && request?.IsForMainFrame == true && Allowed(view?.Url))
            {
                if (owner is not null && owner.TryGetTarget(out var activity) && !activity.ended) activity.BeginExport(export);
                return true;
            }
            return false;
        }
        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            if (NativeCredentials.IsRoot(url) && NativeCredentials.IsRoot(view?.Url)) view!.EvaluateJavascript(HostLanguage.Script, null);
            if (Allowed(url) && Allowed(view?.Url)) view!.EvaluateJavascript("window.__BRSF_NATIVE_DOWNLOADS__=true;", null);
            if (NativeCredentials.IsRoot(url) && NativeCredentials.IsRoot(view?.Url)) view!.EvaluateJavascript("window.__BRSF_NATIVE_CREDENTIALS__=true;window.dispatchEvent(new Event('brsf-native-credentials-ready'));window.dispatchEvent(new Event('brsf-management-resume'));", null);
        }
        public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            base.OnReceivedError(view, request, error);
            if (owner is null || !owner.TryGetTarget(out var activity) || activity.ended) return;
            if (!navigation.OnError(request?.IsForMainFrame == true, request?.Url?.ToString())) return;
            activity.ShowMessage("管理页面连接失败。请确认本机服务已启动，再点打开管理重试。");
            if (activity.showingManagement) activity.BackToControls();
        }
        public override bool OnRenderProcessGone(WebView? view, RenderProcessGoneDetail? detail)
        {
            if (view is null) return true;
            if (owner is not null && owner.TryGetTarget(out var activity) && !activity.ended) activity.RendererGone(view);
            else managementPage.Forget(view, DestroyLostManagementView);
            return true;
        }
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            var url = request?.Url?.ToString();
            if (!Allowed(url)) return new WebResourceResponse("text/plain", "utf-8", 403, "Blocked", new Dictionary<string, string>(), new MemoryStream(Array.Empty<byte>(), false));
            if (NativeExportPolicy.Resolve(url, request?.Method ?? "") is not null)
            {
                // UI capability restriction only. Leave the real HTTP backend
                // untouched; reject before the original JS can claim a save.
                var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { error = new { code = "native_navigation_required", message = HostLanguage.Text("请通过管理页下载按钮打开Android保存窗口；本次未保存文件。") } });
                return new WebResourceResponse("application/json", "utf-8", 501, "Not Implemented",
                    new Dictionary<string, string> { ["Cache-Control"] = "no-store" }, new MemoryStream(bytes, false));
            }
            return null;
        }
    }
}
