using System.Security.Cryptography;
using System.Text;

namespace KeeprawMail.Core.Storage;

/// <summary>
/// 应用自己的本地文件缓存目录（正文缓存、阅读器页面）。
/// 文件名一律由 SHA-256 生成，与邮件 ID（含 | : / 中文等字符）完全分离，不存在非法字符与路径穿越。
/// 删除操作只允许作用于根目录之内的路径。
/// </summary>
public sealed class AppFileCache
{
    public AppFileCache(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    /// <summary>稳定的十六进制 SHA-256（小写，64 字符）。</summary>
    public static string Key(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>某账号的子目录（账号 ID 哈希前 32 位）。</summary>
    public string AccountDirectory(string accountId) => Path.Combine(Root, Key(accountId)[..32]);

    /// <summary>某封邮件正文的缓存路径。</summary>
    public string GetBodyPath(string accountId, string emailId) => Path.Combine(AccountDirectory(accountId), Key(emailId) + ".html");

    /// <summary>按任意 ID 生成根目录下的平铺文件路径（阅读器页面等）。</summary>
    public string GetFlatPath(string id, string extension = ".html") => Path.Combine(Root, Key(id) + extension);

    /// <summary>路径是否位于缓存根目录之内（规范化后比较，阻止 ..\ 与其它盘符）。</summary>
    public bool IsInside(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }
        var root = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(root, cmp) && full.Length > root.Length;
    }

    /// <summary>写入文本文件（自动建目录），返回完整路径。</summary>
    public async Task<string> WriteTextAsync(string path, string content, CancellationToken ct = default)
    {
        if (!IsInside(path)) throw new InvalidOperationException("拒绝写入缓存目录之外的路径。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>删除缓存内的文件；路径在缓存外时拒绝并返回 false。文件本不存在视为成功。</summary>
    public bool TryDeleteFile(string path, out string? error)
    {
        error = null;
        if (!IsInside(path)) { error = "路径不在应用缓存目录内，已拒绝删除"; return false; }
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
    }

    /// <summary>递归删除缓存内的目录；不存在视为成功。</summary>
    public bool TryDeleteDirectory(string path, out string? error)
    {
        error = null;
        if (!IsInside(path)) { error = "路径不在应用缓存目录内，已拒绝删除"; return false; }
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
    }
}
