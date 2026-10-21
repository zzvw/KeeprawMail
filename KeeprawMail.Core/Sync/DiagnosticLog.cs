using System.Text.RegularExpressions;

namespace KeeprawMail.Core.Sync;

/// <summary>
/// 本地诊断日志（%LOCALAPPDATA%\KeeprawMail\logs\diagnostics.log，滚动保留最近约 1 MB）。
/// 写入前统一脱敏：Bearer / access_token / refresh_token / code / password 等值一律替换为 ***。
/// 调用方不得传入邮件正文。
/// </summary>
public static partial class DiagnosticLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1024 * 1024;

    /// <summary>日志文件路径；为 null 时只写 Debug 输出（测试 / 无目录时）。</summary>
    public static string? FilePath { get; set; }

    public static void Write(string category, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{category}] {Redact(message)}";
        System.Diagnostics.Debug.WriteLine(line);
        var path = FilePath;
        if (path == null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志失败不影响功能
        }
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var s = BearerRegex().Replace(text, "Bearer ***");
        s = KeyValueRegex().Replace(s, m => $"{m.Groups[1].Value}{m.Groups[2].Value}***");
        s = JsonValueRegex().Replace(s, m => $"\"{m.Groups[1].Value}\":\"***\"");
        return s;
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-\._~\+/=]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\b(access_token|refresh_token|id_token|code|password|client_secret|auth)(\s*[=:]\s*)[^\s&,;""]+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex(@"""(access_token|refresh_token|id_token|code|password|client_secret)""\s*:\s*""[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonValueRegex();
}
