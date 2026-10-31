namespace Brs.AndroidLocalHost;

// UI-thread owned, single bounded view slot. No Activity/context/delegate is
// stored here; binding and unbinding run immediately in the Android adapter.
internal sealed class MemoryPageSlot<T> where T : class
{
    private T? page;
    private object? owner;
    public bool ShowingManagement { get; set; }
    public T Acquire(object ownerToken, Func<T> create, Action<T> bind)
    {
        page ??= create(); owner = ownerToken; bind(page); return page;
    }
    public bool Owns(object ownerToken, T view) => ReferenceEquals(page, view) && ReferenceEquals(owner, ownerToken);
    public void Release(object ownerToken, T view, bool keep, Action<T, bool> unbind)
    {
        if (!Owns(ownerToken, view)) return;
        unbind(view, !keep);
        owner = null;
        if (!keep) page = null;
    }
    public void Forget(T view, Action<T, bool> unbind)
    { if (!ReferenceEquals(page, view)) return; unbind(view, true); page = null; owner = null; }
}
