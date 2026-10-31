using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Brs.AndroidLocalHost;

[Service(Name = "org.brsfragment.localhost.LocalHostService", Exported = false)]
public sealed class LocalHostService : Service
{
    public const string StopAction = "org.brsfragment.localhost.STOP";
    private const int NotificationId = 34;
    private const string Channel = "brsf-local-host";
    private CancellationTokenSource? lifetime;
    private Task? hostTask;
    private IDisposable? hostLease;
    private Handler? handler;
    private Action? poll;
    private int destroyed;
    private string? notificationLanguage;
    private static HostEngine? active;
    private static int serviceAlive;
    private static string lastStatus = "服务未启动";
    public static bool IsActive => Volatile.Read(ref serviceAlive) != 0;
    public static bool IsReady => Volatile.Read(ref active)?.IsReady == true;
    public static string CurrentStatus => Volatile.Read(ref active)?.Status ?? Volatile.Read(ref lastStatus);
    public override IBinder? OnBind(Intent? intent) => null;
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == StopAction)
        {
            lifetime?.Cancel();
            if (hostTask is null) StopSelf();
            return StartCommandResult.NotSticky;
        }
        if (hostTask is not null) return StartCommandResult.NotSticky;
        try
        {
            var notification = BuildNotification("正在验证本机数据；资源未验证前不可游玩");
            if (OperatingSystem.IsAndroidVersionAtLeast(34)) StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
            else StartForeground(NotificationId, notification);
            hostLease = NativeOperations.TryAcquireHost();
            if (hostLease is null)
            {
                Volatile.Write(ref lastStatus, "请等待资源导入/清理完成后再启动。");
                StopSelf(); return StartCommandResult.NotSticky;
            }
            Volatile.Write(ref serviceAlive, 1);
            var files = FilesDir ?? throw new InvalidOperationException("private_context_unavailable");
            using var folder = new Java.IO.File(files, "local-host");
            var root = folder.CanonicalPath ?? throw new InvalidOperationException("private_context_unavailable");
            lifetime = new CancellationTokenSource();
            var cancellation = lifetime.Token;
            var engine = new HostEngine(root);
            Volatile.Write(ref active, engine);
            hostTask = Task.Run(() => engine.RunAsync(cancellation));
            _ = hostTask.ContinueWith(finished =>
            {
                _ = finished.Exception;
                Interlocked.Exchange(ref hostLease, null)?.Dispose();
                Volatile.Write(ref lastStatus, engine.Status);
                if (Volatile.Read(ref destroyed) != 0)
                {
                    Interlocked.CompareExchange(ref active, null, engine);
                    Volatile.Write(ref serviceAlive, 0);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            handler = new Handler(Looper.MainLooper!);
            var previous = "";
            poll = () =>
            {
                var status = hostTask.IsFaulted ? "cleanup-failed / " + hostTask.Exception!.GetBaseException().GetType().Name : engine.Status;
                if (status != previous || notificationLanguage != HostLanguage.Language)
                {
                    Android.Util.Log.Info("BRSLocalHost", "stage=" + status.Split('\n')[0]);
                    ((NotificationManager)GetSystemService(NotificationService)!).Notify(NotificationId,
                        BuildNotification(engine.IsReady ? "API已启动：HTTP18445 / 管理18180" : "本机服务正在准备或关闭"));
                    previous = status; notificationLanguage = HostLanguage.Language;
                }
                if (hostTask.IsCompleted)
                {
                    _ = hostTask.Exception; // Observe cleanup failure without exposing its text.
                    Volatile.Write(ref lastStatus, status);
                    StopSelf(); return;
                }
                handler?.PostDelayed(poll!, 500);
            };
            poll();
        }
        catch (Exception error)
        {
            Volatile.Write(ref lastStatus, "context-failed / " + error.GetType().Name);
            StopSelf();
        }
        return StartCommandResult.NotSticky;
    }
    private Notification BuildNotification(string text)
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) manager.CreateNotificationChannel(new NotificationChannel(Channel, HostLanguage.Text("BRS 本机服务"), NotificationImportance.Low));
        var launch = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        var stop = PendingIntent.GetService(this, 1, new Intent(this, typeof(LocalHostService)).SetAction(StopAction), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        var builder = OperatingSystem.IsAndroidVersionAtLeast(26) ? new Notification.Builder(this, Channel) : new Notification.Builder(this);
        return builder.SetContentTitle(HostLanguage.Text("BRS 本机服务"))!.SetContentText(HostLanguage.Display(text))!.SetSmallIcon(Android.Resource.Drawable.IcDialogInfo)!
            .SetContentIntent(launch)!.SetOngoing(true)!.AddAction(new Notification.Action.Builder(null, HostLanguage.Text("停止服务"), stop).Build())!.Build();
    }
    public override void OnDestroy()
    {
        lifetime?.Cancel();
        lifetime?.Dispose(); lifetime = null;
        Volatile.Write(ref destroyed, 1);
        if (hostTask is null || hostTask.IsCompleted)
        {
            Interlocked.Exchange(ref hostLease, null)?.Dispose();
            Volatile.Write(ref active, null);
            Volatile.Write(ref serviceAlive, 0);
        }
        handler?.RemoveCallbacksAndMessages(null); handler?.Dispose(); handler = null;
        if (OperatingSystem.IsAndroidVersionAtLeast(24)) StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }
}
