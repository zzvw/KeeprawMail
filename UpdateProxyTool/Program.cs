using Microsoft.Data.Sqlite;

namespace UpdateProxy;

class Program
{
    static void Main()
    {
        // 修改 Gmail 账号的代理类型从 SOCKS5 改为 HTTP
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeeprawMail",
            "keepraw.db"
        );

        Console.WriteLine($"数据库路径: {dbPath}");

        if (!File.Exists(dbPath))
        {
            Console.WriteLine("错误：数据库文件不存在！");
            return;
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        // 1. 查看当前配置
        Console.WriteLine("\n=== 修改前的配置 ===");
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Email, ProxyType, ProxyHost, ProxyPort FROM Accounts WHERE Email='forgottenpedestrian@gmail.com'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                Console.WriteLine($"Email: {reader.GetString(0)}");
                Console.WriteLine($"ProxyType: {reader.GetString(1)}");
                Console.WriteLine($"ProxyHost: {reader.GetString(2)}");
                Console.WriteLine($"ProxyPort: {reader.GetInt32(3)}");
            }
        }

        // 2. 修改为 SOCKS4A + 端口 1080（实验性：SOCKS4A 可能有更好的兼容性）
        Console.WriteLine("\n=== 正在修改为 SOCKS4A 代理，端口 1080 ===");
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE Accounts SET ProxyType='SOCKS4A', ProxyPort=1080 WHERE Email='forgottenpedestrian@gmail.com'";
            var rowsAffected = cmd.ExecuteNonQuery();
            Console.WriteLine($"已修改 {rowsAffected} 条记录");
        }

        // 3. 验证修改结果
        Console.WriteLine("\n=== 修改后的配置 ===");
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Email, ProxyType, ProxyHost, ProxyPort FROM Accounts WHERE Email='forgottenpedestrian@gmail.com'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                Console.WriteLine($"Email: {reader.GetString(0)}");
                Console.WriteLine($"ProxyType: {reader.GetString(1)}");
                Console.WriteLine($"ProxyHost: {reader.GetString(2)}");
                Console.WriteLine($"ProxyPort: {reader.GetInt32(3)}");
            }
        }

        Console.WriteLine("\n✅ 修改完成！");
        Console.WriteLine("说明：已切换到 SOCKS4A 协议，端口 1080");
        Console.WriteLine("SOCKS4A 是 SOCKS4 的扩展，支持域名解析，可能与 v2rayN 有更好的兼容性。");
        Console.ReadKey();
    }
}
