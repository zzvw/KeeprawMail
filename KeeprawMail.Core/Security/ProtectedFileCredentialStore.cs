using System.Security.Cryptography;
using System.Text;

namespace KeeprawMail.Core.Security;

/// <summary>
/// 基于 <see cref="ISecretProtector"/>（生产环境为 DPAPI CurrentUser）的文件凭据存储。
///
/// 每个键一个文件：%LOCALAPPDATA%\KeeprawMail\credentials\{SHA256(key)}.bin
///  * 文件名是键的哈希，不暴露邮箱地址；
///  * 内容 = 魔数 + DPAPI 密文；
///  * 先写临时文件再原子替换，断电不会留下半个文件。
///
/// 选择 DPAPI 文件而不是 Credential Manager：Credential Manager 单条上限 2560 字节且需要额外 P/Invoke 封装，
/// 而 DPAPI 由 System.Security.Cryptography.ProtectedData 提供，Native AOT 友好。
/// </summary>
public sealed class ProtectedFileCredentialStore : ICredentialStore
{
    private static readonly byte[] Magic = "KRC1"u8.ToArray();
    private readonly string _directory;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProtectedFileCredentialStore(string directory, ISecretProtector protector)
    {
        _directory = directory;
        _protector = protector;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>默认位置：%LOCALAPPDATA%\KeeprawMail\credentials。</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeeprawMail", "credentials");

    private string PathFor(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(_directory, hash + ".bin");
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
                return null;
            var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (data.Length < Magic.Length || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                throw new CryptographicException("凭据文件格式无效。");
            var plain = _protector.Unprotect(data[Magic.Length..]);
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = PathFor(key);
        var plain = Encoding.UTF8.GetBytes(value);
        byte[] cipher;
        try { cipher = _protector.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }

        var payload = new byte[Magic.Length + cipher.Length];
        Magic.CopyTo(payload, 0);
        cipher.CopyTo(payload, Magic.Length);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tmp, payload, cancellationToken).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }
}
