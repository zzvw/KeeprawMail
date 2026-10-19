using System.Security.Cryptography;
using System.Text;

namespace KeeprawMail.Core.Auth;

/// <summary>
/// RFC 7636 PKCE 工具（只实现 S256，不提供 plain）。
/// </summary>
public static class Pkce
{
    /// <summary>生成 code_verifier：32 字节随机数的 base64url（43 字符，满足 43..128 要求）。</summary>
    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>code_challenge = BASE64URL(SHA256(ASCII(code_verifier)))。</summary>
    public static string CreateChallenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifier);
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    /// <summary>随机 state：32 字节随机数的 base64url。</summary>
    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>定长比较，避免时序侧信道。</summary>
    public static bool StateEquals(string? expected, string? actual)
    {
        if (expected is null || actual is null)
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    }

    internal static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
