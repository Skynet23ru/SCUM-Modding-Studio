using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace ScumStudio.Core.Security;

/// <summary>
/// Keeps a short secret (a server password) in the settings file protected for the current Windows user (DPAPI, like the AES key).
/// Other systems cannot protect it, so nothing is stored there.
/// </summary>
public static class SecretText
{
    private static readonly byte[] Entropy = "ScumStudio.ServerPassword.v1"u8.ToArray();

    /// <summary>True when the platform can protect a secret for the current user.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>The protected form of <paramref name="secret"/> (text safe to store), or null for an empty secret or on a system that cannot protect it.</summary>
    public static string? Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || !OperatingSystem.IsWindows())
        {
            return null;
        }

        return ProtectWindows(secret);
    }

    /// <summary>The secret behind <paramref name="protectedText"/>, or null when it is empty or was made for another Windows user.</summary>
    public static string? Reveal(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText) || !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return RevealWindows(protectedText);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string ProtectWindows(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    [SupportedOSPlatform("windows")]
    private static string RevealWindows(string text) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(text), Entropy, DataProtectionScope.CurrentUser));
}
