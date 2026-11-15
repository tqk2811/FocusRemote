using System.Security.Cryptography;
using System.Text;

namespace FocusRemote.Host.Core.Settings;

/// <summary>Protects the 32-byte root key with DPAPI (CurrentUser scope) for storage in settings.json.</summary>
public static class RootKeyProtector
{
    private static readonly byte[] _entropy = System.Text.Encoding.UTF8.GetBytes("FocusRemote/v1/rootkey");

    public static string Protect(byte[] rootKey)
    {
        ArgumentNullException.ThrowIfNull(rootKey);
        return Convert.ToBase64String(ProtectedData.Protect(rootKey, _entropy, DataProtectionScope.CurrentUser));
    }

    /// <summary>Null when missing, malformed or not decryptable by this user.</summary>
    public static byte[]? TryUnprotect(string? protectedRootKey)
    {
        if (string.IsNullOrEmpty(protectedRootKey))
            return null;
        try
        {
            byte[] key = ProtectedData.Unprotect(Convert.FromBase64String(protectedRootKey), _entropy, DataProtectionScope.CurrentUser);
            return key.Length == Protocol.ProtocolConstants.KeyLength ? key : null;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
