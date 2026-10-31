// These storage tests never invoke Android inode identity APIs. A call here
// is a test failure; device identity behavior requires Android acceptance.
namespace Android.Systems;
internal static class Os
{
    public static StatResult? Stat(string _) => throw new NotSupportedException("Android inode APIs are outside these storage tests.");
}
internal sealed class StatResult : IDisposable
{
    public long StDev => throw new NotSupportedException();
    public long StIno => throw new NotSupportedException();
    public void Dispose() { }
}
