namespace Brs.AndroidLocalHost;

internal enum NativeWorkKind { Import, Cleanup, Export }

// Process scope: Activity recreation cannot start a second importer or bypass
// the old worker's cancellation/finally cleanup. Host ownership lasts through
// the engine's complete listener/runtime disposal, including Service destruction.
internal static class NativeOperations
{
    private static readonly object gate = new();
    private static NativeWork? current;
    private static HostLease? host;
    private static string lastOutcome = "";
    public static string LastOutcome { get => Volatile.Read(ref lastOutcome); set => Volatile.Write(ref lastOutcome, value); }
    public static bool IsBusy { get { lock (gate) return current is not null; } }
    public static string Summary
    {
        get { lock (gate) return current is null ? "" : current.Kind switch
        {
            NativeWorkKind.Import => current.Pending ? "等待选择资源归档。" : "资源导入/取消清理中，已读取" + current.Bytes / 1048576 + " MiB。",
            NativeWorkKind.Cleanup => "正在确认或清理未完成导入。",
            _ => current.Pending ? "等待选择导出保存位置。" : "正在保存导出文件。"
        }; }
    }
    public static NativeWork? TryBegin(NativeWorkKind kind, NativeExportSpec? export = null)
    {
        lock (gate)
        {
            if (current is not null || kind != NativeWorkKind.Export && host is not null) return null;
            return current = new NativeWork(kind, export);
        }
    }
    public static NativeWork? ClaimPending(NativeWorkKind kind)
    {
        lock (gate)
        {
            if (current is null || current.Kind != kind || !current.Pending) return null;
            current.Pending = false; return current;
        }
    }
    public static bool Claim(NativeWork work)
    { lock (gate) { if (!ReferenceEquals(current, work) || !work.Pending) return false; work.Pending = false; return true; } }
    public static void CancelPending()
    { lock (gate) { if (current?.Pending == true) current.Dispose(); } }
    public static bool CancelPending(NativeWork work)
    { lock (gate) { if (!ReferenceEquals(current, work) || !work.Pending) return false; current.Dispose(); return true; } }
    public static void CancelPending(NativeWorkKind kind)
    { lock (gate) { if (current?.Kind == kind && current.Pending) current.Dispose(); } }
    internal static void Finish(NativeWork work)
    { lock (gate) { if (ReferenceEquals(current, work)) current = null; } }
    public static IDisposable? TryAcquireHost()
    {
        lock (gate)
        {
            if (host is not null || current is { Kind: not NativeWorkKind.Export }) return null;
            return host = new HostLease();
        }
    }
    private sealed class HostLease : IDisposable
    {
        public void Dispose() { lock (gate) { if (ReferenceEquals(host, this)) host = null; } }
    }
}

internal sealed class NativeWork(NativeWorkKind kind, NativeExportSpec? export) : IDisposable
{
    private long bytes;
    public NativeWorkKind Kind { get; } = kind;
    public NativeExportSpec? Export { get; } = export;
    internal bool Pending { get; set; } = true;
    public long Bytes { get => Interlocked.Read(ref bytes); set => Interlocked.Exchange(ref bytes, value); }
    public void Dispose() => NativeOperations.Finish(this);
}
