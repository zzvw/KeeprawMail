# 代理配置指南

## 代理类型选择

### HTTP 代理（推荐）✓

- **适用场景**：v2rayN Mixed Port、Clash、大多数现代代理软件
- **优点**：兼容性最好，支持 SSL/TLS 连接
- **端口示例**：
  - 7890（Clash 默认 HTTP 端口）
  - 1080（v2rayN Mixed Port，同时支持 HTTP 和 SOCKS5）
  - 7890（v2rayN HTTP 端口）

### SOCKS5 代理

- **适用场景**：纯 SOCKS5 代理服务器
- **注意事项**：某些代理软件的 SOCKS5 实现可能在处理 SSL/TLS 流量时存在兼容性问题
- **端口示例**：
  - 1080（传统 SOCKS5 端口）
  - 1081（v2rayN 纯 SOCKS5 端口）

---

## 常见问题

### ❌ Gmail 连接失败：SslHandshakeException

**症状**：OAuth2 登录成功，但邮件同步失败，错误信息为 `MailKit.Security.SslHandshakeException`

**原因**：
- SOCKS5 代理在处理 IMAP 的 SSL/TLS 握手时可能存在兼容性问题
- 某些代理软件（如 v2rayN）的"流量嗅探"功能可能干扰 SSL 握手

**解决方案**：

1. **首选方案**：将代理类型从 SOCKS5 改为 HTTP
   - 打开账号设置
   - 将 Proxy Type 从 "SOCKS5" 改为 "HTTP"
   - 保持主机和端口不变（如 `127.0.0.1:1080`）
   - 保存并重新同步

2. **v2rayN 用户**：
   - 确保使用 Mixed Port（默认 1080）
   - 在 Keepraw Mail 中选择 "HTTP" 代理类型
   - 可选：关闭 v2rayN 的"流量嗅探"功能

3. **Clash 用户**：
   - 使用 HTTP 代理端口（默认 7890）
   - 在 Keepraw Mail 中选择 "HTTP" 代理类型

---

### 🤔 如何判断我的代理支持哪种协议？

| 代理软件 | 推荐类型 | 端口 | 说明 |
|---------|---------|------|------|
| **v2rayN** | HTTP | 1080 (Mixed Port) | Mixed Port 同时支持 HTTP 和 SOCKS5，选择 HTTP 兼容性更好 |
| **Clash** | HTTP | 7890 | Clash 默认 HTTP 代理端口 |
| **Shadowsocks** | SOCKS5 | 1080 | 传统 SOCKS5 代理，但可能遇到 SSL 问题 |
| **Clash Verge** | HTTP | 7890 | 与 Clash 相同 |
| **Qv2ray** | HTTP | 8889 (Mixed Port) | 优先使用 HTTP |

---

### 🔧 高级调试

如果更改代理类型后仍然失败，可以查看 Visual Studio 的 Debug 输出窗口：

1. 在 Visual Studio 中运行应用（Debug 模式）
2. 打开 "输出" 窗口（View → Output）
3. 尝试同步邮件
4. 查找以下关键日志：

```
[ImapService] Setting HTTP proxy: 127.0.0.1:1080
[ImapService] SSL Certificate Validation for xxx@gmail.com:
  Subject: CN=imap.gmail.com
  Issuer: CN=...
  SSL Errors: None
```

如果看到 SSL Errors 不为 None，说明证书验证失败，可能是代理软件的证书处理问题。

---

### 📝 为什么 OAuth2 成功但 IMAP 失败？

**技术原因**：

- **OAuth2 认证**：使用 `HttpClient`，通过 HTTP 协议与 Google 服务器通信
- **IMAP 连接**：使用 `MailKit.ImapClient`，通过 IMAP 协议（端口 993）建立 SSL/TLS 连接

当使用 SOCKS5 代理时：
- `HttpClient` 将 SOCKS5 当作 HTTP 代理使用（适用于 Mixed Port）→ ✅ 成功
- `ImapClient` 使用真正的 SOCKS5 协议建立隧道 → ❌ 可能失败

**解决方案**：统一使用 HTTP 代理类型，让两者都通过 HTTP 协议与代理通信。

---

## 最佳实践

1. **优先选择 HTTP**：除非你明确知道代理只支持 SOCKS5，否则始终选择 HTTP
2. **使用 Mixed Port**：如果代理软件支持 Mixed Port，优先使用（如 v2rayN 的 1080 端口）
3. **关闭流量嗅探**：某些代理软件的流量嗅探功能可能干扰 SSL 握手，建议关闭
4. **测试连接**：添加账号后，立即测试同步功能，确保配置正确

---

## 相关文档

- [如何获取 Gmail Client ID](HowToGetGmailClientId.md)
- [项目开发规范](../PROJECT_RULES.md)
- [当前开发状态](../CURRENT_STATUS.md)
