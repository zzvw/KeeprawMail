using Microsoft.Data.Sqlite;

namespace UpdateProxy;

/// <summary>
/// 开发调试工具：修改本地数据库中某个账号的代理设置。
///
/// 用法：
///   UpdateProxyTool &lt;email&gt; &lt;GLOBAL|NONE|HTTP|SOCKS5&gt; [host] [port]
///
/// 不包含任何硬编码的邮箱地址或凭据；只修改代理列，不读取、不输出密码或 Token。
/// </summary>
class Program
{
    private static readonly string[] AllowedTypes = ["GLOBAL", "NONE", "HTTP", "SOCKS5"];

    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: UpdateProxyTool <email> <GLOBAL|NONE|HTTP|SOCKS5> [host] [port]");
            return 1;
        }

        var email = args[0].Trim();
        var type = args[1].Trim().ToUpperInvariant();
        var host = args.Length > 2 ? args[2].Trim() : string.Empty;
        var port = args.Length > 3 && int.TryParse(args[3], out var p) ? p : 0;

        if (Array.IndexOf(AllowedTypes, type) < 0)
        {
            Console.WriteLine($"不支持的代理类型: {type}（仅支持 GLOBAL / NONE / HTTP / SOCKS5）");
            return 1;
        }

        if ((type == "HTTP" || type == "SOCKS5") && (host.Length == 0 || port is < 1 or > 65535))
        {
            Console.WriteLine("HTTP / SOCKS5 需要合法的 host 和 port (1-65535)");
            return 1;
        }

        if (type is "GLOBAL" or "NONE")
        {
            host = string.Empty;
            port = 0;
        }

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeeprawMail",
            "keepraw.db");

        if (!File.Exists(dbPath))
        {
            Console.WriteLine($"数据库文件不存在: {dbPath}");
            return 1;
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Accounts SET ProxyType = $t, ProxyHost = $h, ProxyPort = $p WHERE Email = $e COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$p", port);
        cmd.Parameters.AddWithValue("$e", email);
        var rows = cmd.ExecuteNonQuery();

        Console.WriteLine(rows > 0 ? $"已更新 {rows} 个账号的代理设置为 {type}" : "未找到该邮箱对应的账号");
        return rows > 0 ? 0 : 2;
    }
}
