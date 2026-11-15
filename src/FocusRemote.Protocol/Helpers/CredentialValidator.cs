using System.Text.RegularExpressions;

namespace FocusRemote.Protocol.Helpers;

/// <summary>Validation rules for host id (protocol §1) and password (protocol §3).</summary>
public static partial class CredentialValidator
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{3,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    /// <summary>3–64 chars of <c>[A-Za-z0-9_-]</c>, case-sensitive.</summary>
    public static bool IsValidId(string? id) => id is not null && IdRegex().IsMatch(id);

    /// <summary>At least 10 characters (UTF-16 code units, same as JS <c>string.length</c>).</summary>
    public static bool IsValidPassword(string? password)
        => password is not null && password.Length >= ProtocolConstants.MinPasswordLength;
}
