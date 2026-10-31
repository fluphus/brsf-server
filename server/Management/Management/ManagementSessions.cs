using System.Security.Cryptography;
using System.Text;

namespace Brs.Local.Management;

internal sealed record ManagementSession(string Identity, string CsrfToken, DateTimeOffset ExpiresAt);

internal sealed class ManagementSessions(byte[] ownerHash, Action<string> releaseOwner, TimeProvider clock) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, ManagementSession> sessions = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> attempts = new();

    public (ManagementSession? Session, string? Cookie, string? Error) Login(string key)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            Cleanup(now);
            while (attempts.TryPeek(out var attempt) && now - attempt >= ManagementHostOptions.LoginWindow) attempts.Dequeue();
            if (attempts.Count >= ManagementHostOptions.MaximumLoginAttempts) return (null, null, "rate_limited");
            attempts.Enqueue(now);
            var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            var valid = key.Length == 43 && CryptographicOperations.FixedTimeEquals(ownerHash, supplied);
            CryptographicOperations.ZeroMemory(supplied);
            if (!valid) return (null, null, "invalid_key");
            if (sessions.Count >= ManagementHostOptions.MaximumSessions) return (null, null, "session_capacity");
            var cookie = ManagementOwnerKey.Token();
            var session = new ManagementSession(ManagementOwnerKey.Token(), ManagementOwnerKey.Token(), now + ManagementHostOptions.SessionLifetime);
            sessions.Add(Hash(cookie), session);
            return (session, cookie, null);
        }
    }

    public ManagementSession? Find(string? cookie)
    {
        if (cookie is null || cookie.Length != 43) return null;
        lock (gate)
        {
            Cleanup(clock.GetUtcNow());
            return sessions.GetValueOrDefault(Hash(cookie));
        }
    }

    public void Remove(string? cookie)
    {
        if (cookie is null || cookie.Length != 43) return;
        lock (gate)
            if (sessions.Remove(Hash(cookie), out var session)) releaseOwner(session.Identity);
    }

    private void Cleanup(DateTimeOffset now)
    {
        foreach (var pair in sessions.Where(pair => pair.Value.ExpiresAt <= now).ToArray())
        {
            sessions.Remove(pair.Key);
            releaseOwner(pair.Value.Identity);
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var session in sessions.Values) releaseOwner(session.Identity);
            sessions.Clear();
            CryptographicOperations.ZeroMemory(ownerHash);
        }
    }
}
