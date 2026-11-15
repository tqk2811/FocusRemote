using System.Security.Cryptography;
using System.Text;
using FocusRemote.Protocol.Crypto.Models;

namespace FocusRemote.Protocol.Crypto;

/// <summary>Pure key-derivation functions of protocol §3.</summary>
public static class KeyDerivation
{
    /// <summary><c>SHA-256(UTF8("FocusRemote/v1/salt/" + id))[0..16]</c>.</summary>
    public static byte[] DeriveSalt(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(ProtocolConstants.SaltPrefix + id));
        return hash[..ProtocolConstants.SaltLength];
    }

    /// <summary>
    /// <c>PBKDF2-HMAC-SHA256(UTF8(password), salt(id), iterations, 32)</c>.
    /// Production code must use the default iteration count; a smaller value is only for fast tests.
    /// </summary>
    public static byte[] DeriveRootKey(string password, string id, int iterations = ProtocolConstants.Pbkdf2Iterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            DeriveSalt(id),
            iterations,
            HashAlgorithmName.SHA256,
            ProtocolConstants.KeyLength);
    }

    /// <summary><c>HKDF-SHA256(rootKey, salt = nonceV ‖ nonceH, info = "FocusRemote/v1/session", 64)</c>.</summary>
    public static SessionKeys DeriveSessionKeys(ReadOnlySpan<byte> rootKey, ReadOnlySpan<byte> nonceV, ReadOnlySpan<byte> nonceH)
    {
        if (rootKey.Length != ProtocolConstants.KeyLength)
            throw new ArgumentException("Root key must be 32 bytes.", nameof(rootKey));
        if (nonceV.Length != ProtocolConstants.NonceLength || nonceH.Length != ProtocolConstants.NonceLength)
            throw new ArgumentException("Nonces must be 32 bytes.");

        byte[] salt = new byte[ProtocolConstants.NonceLength * 2];
        nonceV.CopyTo(salt);
        nonceH.CopyTo(salt.AsSpan(ProtocolConstants.NonceLength));
        byte[] okm = new byte[ProtocolConstants.KeyLength * 2];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, rootKey, okm, salt, Encoding.UTF8.GetBytes(ProtocolConstants.SessionInfo));
        return new SessionKeys
        {
            KeyH2V = okm[..ProtocolConstants.KeyLength],
            KeyV2H = okm[ProtocolConstants.KeyLength..],
        };
    }
}
