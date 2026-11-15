using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FocusRemote.Host.Core.Logging.Interfaces;

namespace FocusRemote.Host.Core.Lan;

/// <summary>
/// Self-signed TLS certificate for the LAN server, generated once and persisted as a PFX protected with
/// DPAPI (CurrentUser). Regenerated only when expiring or when a current local address is missing from the
/// SAN (the viewer then has to accept the browser warning again).
/// </summary>
public sealed class LanCertificateManager
{
    private static readonly byte[] _entropy = System.Text.Encoding.UTF8.GetBytes("FocusRemote/v1/lan-certificate");

    private readonly string _filePath;
    private readonly IAppLog _log;

    public LanCertificateManager(string filePath, IAppLog log)
    {
        _filePath = filePath;
        _log = log;
    }

    public X509Certificate2 GetOrCreate(string machineName, IReadOnlyList<IPAddress> addresses)
    {
        X509Certificate2? existing = TryLoad();
        if (existing is not null && IsUsable(existing, machineName, addresses))
            return existing;
        existing?.Dispose();

        byte[] pfx = CreatePfx(machineName, addresses);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllBytes(_filePath, ProtectedData.Protect(pfx, _entropy, DataProtectionScope.CurrentUser));
            _log.Info($"LAN: generated self-signed certificate for {machineName}, {string.Join(", ", addresses)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _log.Warn("LAN: could not persist certificate", ex);
        }
        return Load(pfx);
    }

    /// <summary>Builds a PFX (no password) for CN=FocusRemote, SAN = machine name, localhost and the given IPs.</summary>
    public static byte[] CreatePfx(string machineName, IReadOnlyList<IPAddress> addresses)
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=FocusRemote {machineName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(machineName.ToLowerInvariant());
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        foreach (IPAddress address in addresses)
            san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // 397 days: the longest validity browsers accept for TLS server certificates.
        using X509Certificate2 certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(397));
        return certificate.Export(X509ContentType.Pfx);
    }

    private X509Certificate2? TryLoad()
    {
        try
        {
            if (!File.Exists(_filePath))
                return null;
            return Load(ProtectedData.Unprotect(File.ReadAllBytes(_filePath), _entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _log.Warn("LAN: stored certificate unreadable, regenerating", ex);
            return null;
        }
    }

    // A CNG key created by CreateSelfSigned is ephemeral and SChannel cannot use it; loading from PFX bytes
    // imports a usable (user key set, deleted on dispose) key.
    private static X509Certificate2 Load(byte[] pfx) => new(pfx, (string?)null, X509KeyStorageFlags.UserKeySet);

    private static bool IsUsable(X509Certificate2 certificate, string machineName, IReadOnlyList<IPAddress> addresses)
    {
        if (certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(14) || !certificate.HasPrivateKey)
            return false;
        X509SubjectAlternativeNameExtension? san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null)
            return false;
        HashSet<IPAddress> ips = san.EnumerateIPAddresses().ToHashSet();
        if (!addresses.All(ips.Contains))
            return false;
        return san.EnumerateDnsNames().Contains(machineName.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
    }
}
