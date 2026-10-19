using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace KeeprawMail.Core.Security;

/// <summary>
/// Windows DPAPI，作用域 CurrentUser：只有同一 Windows 用户登录态下才能解密；
/// 拷走数据库或凭据文件到其他机器/其他用户均无法解密。
/// 额外熵（entropy）把密文绑定到本应用，避免被同用户下其它使用 DPAPI 的程序误解。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KeeprawMail.CredentialStore.v1");

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext) =>
        ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
}
