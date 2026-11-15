using System.Security.Cryptography;

namespace FocusRemote.Host.Core.Settings;

/// <summary>Random host id / password generator (CSPRNG). Output always passes CredentialValidator.</summary>
public static class CredentialGenerator
{
    private const string IdAlphabet = "abcdefghijkmnpqrstuvwxyz23456789";
    // No look-alike characters (0/O, 1/l/I) so it can be read out or typed on a phone.
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    public static string NewId(int length = 10) => new(RandomNumberGenerator.GetItems<char>(IdAlphabet, length));

    public static string NewPassword(int length = 16) => new(RandomNumberGenerator.GetItems<char>(PasswordAlphabet, length));
}
