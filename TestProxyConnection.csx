using System;
using System.Net.Sockets;
using System.Net;

// 测试通过 SOCKS5 代理连接到 Gmail IMAP
Console.WriteLine("测试 SOCKS5 代理连接到 imap.gmail.com:993...\n");

try
{
    // 创建 TCP 客户端
    var client = new TcpClient();
    
    // 连接到 SOCKS5 代理
    Console.WriteLine("1. 连接到 SOCKS5 代理 127.0.0.1:1080...");
    await client.ConnectAsync("127.0.0.1", 1080);
    Console.WriteLine("   ✓ 已连接到代理\n");
    
    // 发送 SOCKS5 握手请求
    Console.WriteLine("2. 发送 SOCKS5 握手...");
    var stream = client.GetStream();
    
    // SOCKS5 握手: 版本5, 1个认证方法, 无认证(0x00)
    byte[] handshake = { 0x05, 0x01, 0x00 };
    await stream.WriteAsync(handshake, 0, handshake.Length);
    
    // 读取响应
    byte[] response = new byte[2];
    await stream.ReadAsync(response, 0, 2);
    
    if (response[0] != 0x05 || response[1] != 0x00)
    {
        Console.WriteLine($"   ✗ SOCKS5 握手失败: {response[0]:X2} {response[1]:X2}");
        return;
    }
    Console.WriteLine("   ✓ SOCKS5 握手成功\n");
    
    // 发送连接请求
    Console.WriteLine("3. 请求连接到 imap.gmail.com:993...");
    var domain = "imap.gmail.com";
    var port = 993;
    
    // SOCKS5 连接请求: VER(5) CMD(1=CONNECT) RSV(0) ATYP(3=域名)
    var request = new byte[7 + domain.Length];
    request[0] = 0x05; // 版本
    request[1] = 0x01; // CONNECT 命令
    request[2] = 0x00; // 保留字段
    request[3] = 0x03; // 地址类型：域名
    request[4] = (byte)domain.Length;
    
    // 复制域名
    for (int i = 0; i < domain.Length; i++)
    {
        request[5 + i] = (byte)domain[i];
    }
    
    // 端口（大端序）
    request[5 + domain.Length] = (byte)(port >> 8);
    request[6 + domain.Length] = (byte)(port & 0xFF);
    
    await stream.WriteAsync(request, 0, request.Length);
    
    // 读取连接响应
    byte[] connectResponse = new byte[10 + domain.Length];
    int bytesRead = await stream.ReadAsync(connectResponse, 0, connectResponse.Length);
    
    if (connectResponse[1] != 0x00)
    {
        Console.WriteLine($"   ✗ SOCKS5 连接失败，错误码: {connectResponse[1]:X2}");
        Console.WriteLine("     错误码含义:");
        Console.WriteLine("     0x01 = 一般 SOCKS 服务器故障");
        Console.WriteLine("     0x02 = 规则不允许连接");
        Console.WriteLine("     0x03 = 网络不可达");
        Console.WriteLine("     0x04 = 主机不可达");
        Console.WriteLine("     0x05 = 连接被拒绝");
        Console.WriteLine("     0x06 = TTL 超时");
        Console.WriteLine("     0x07 = 不支持的命令");
        Console.WriteLine("     0x08 = 不支持的地址类型");
        return;
    }
    
    Console.WriteLine("   ✓ SOCKS5 隧道建立成功！\n");
    
    // 尝试读取一些数据（应该是 TLS 握手数据）
    Console.WriteLine("4. 尝试读取 TLS 握手数据...");
    byte[] buffer = new byte[1024];
    
    // 设置超时
    client.ReceiveTimeout = 3000;
    
    try
    {
        int received = await stream.ReadAsync(buffer, 0, buffer.Length);
        if (received > 0)
        {
            Console.WriteLine($"   ✓ 收到 {received} 字节数据（TLS 握手数据）");
            Console.WriteLine($"   前 16 字节: {BitConverter.ToString(buffer, 0, Math.Min(16, received))}");
            
            // 检查是否是 TLS 握手
            if (buffer[0] == 0x16)
            {
                Console.WriteLine("   ✓ 确认是 TLS 握手数据 (0x16 = Handshake)");
            }
        }
        else
        {
            Console.WriteLine("   ⚠️ 未收到数据");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   ⚠️ 读取超时或错误: {ex.Message}");
    }
    
    Console.WriteLine("\n✅ 测试完成：SOCKS5 代理可以连接到 Gmail IMAP！");
    Console.WriteLine("   这说明 v2rayN 代理本身是正常的。");
    Console.WriteLine("   问题可能在于 MailKit 的 SSL/TLS 握手实现。");
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ 测试失败: {ex.Message}");
    Console.WriteLine($"   异常类型: {ex.GetType().Name}");
    if (ex.InnerException != null)
    {
        Console.WriteLine($"   内部异常: {ex.InnerException.Message}");
    }
}
