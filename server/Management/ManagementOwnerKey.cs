using System.Security.Cryptography;

namespace Brs.Local.Management;

// Portable sessions only: owner hash is injected by the platform. No file/ACL access.
public static class ManagementOwnerKey
{
    internal static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
