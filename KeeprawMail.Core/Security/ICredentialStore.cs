namespace KeeprawMail.Core.Security;

/// <summary>
/// 安全凭据存储。实现必须保证：值加密落盘、写入原子化、不打印值。
/// </summary>
public interface ICredentialStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>凭据键命名。</summary>
public static class CredentialKeys
{
    public static string Password(string accountId) => $"account/{accountId}/password";
    public static string OAuthTokens(string accountId) => $"account/{accountId}/oauth";
}

/// <summary>把明文字节加密/解密。Windows 上为 DPAPI(CurrentUser)。</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}
