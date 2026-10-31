using System.Security.Cryptography.X509Certificates;

namespace Brs.Portable.Transport;

/// <summary>Optional server TLS. The caller owns the certificate and must keep it alive until Stop/Dispose finishes.
/// AllowedHosts contains exact HTTP Host values, including a port when required; it is copied at server construction.
/// No client certificate is requested. This does not configure client trust, DNS, or routing.</summary>
public sealed record LoopbackTlsOptions(X509Certificate2 Certificate, IReadOnlyCollection<string> AllowedHosts,
    TimeSpan? HandshakeTimeout = null);
