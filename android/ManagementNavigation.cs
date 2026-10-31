namespace Brs.AndroidLocalHost;

// Retained beside the process-memory WebView. Only a failed main document
// disables resuming the retained page; subresource errors preserve its state.
internal sealed class ManagementNavigation
{
    private bool failed;
    public bool CanResume(string? url) => NativeCredentials.IsRoot(url) && !failed;
    public bool OnError(bool mainFrame, string? url)
    {
        if (!mainFrame || !NativeCredentials.IsRoot(url)) return false;
        failed = true; return true;
    }
    public void BeginLoad() => failed = false;
}
