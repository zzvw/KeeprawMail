# 代理配置指南

## 生效范围

Keepraw Mail 所有出站连接都通过同一个代理解析器（`ProxyResolver`）和网络客户端工厂（`NetworkClientFactory`）：

| 请求 | 是否走代理 |
|---|---|
| IMAP 收信 | ✅ |
| SMTP 发信 | ✅ |
| OAuth Token 交换（登录） | ✅ |
| OAuth Token 刷新 | ✅ |
| Google 用户信息（获取邮箱地址） | ✅ |
| 系统浏览器中的 Google 登录页 | ❌ 由浏览器 / 系统代理决定，本应用无法控制 |
| 登录回调 `http://127.0.0.1:<端口>/` | ❌ 本机回环，不需要代理 |

## 全局代理与账号代理

* **全局代理**（设置 → Global Proxy）：`None` / `HTTP` / `SOCKS5`。
* **账号代理**（添加账号页 / 设置 → 账号 → Edit Proxy）：
  * `Use Global Settings`：**继承**全局代理。修改全局代理后，这些账号立即跟着变。新账号默认选这个。
  * `None (Direct Connection)`：该账号强制直连，忽略全局代理。
  * `HTTP` / `SOCKS5`：该账号使用自己的代理，覆盖全局。

> 旧版本在添加账号时会把当时的全局代理**拷贝**进账号。升级后，这些旧账号的设置按“覆盖”处理，行为不变；
> 如需改为继承，在 Edit Proxy 中选择 `Use Global Settings`。

代理配置不合法（选择了 HTTP/SOCKS5 却没填主机、端口不在 1–65535）时会**直接报错**，不会再静默改为直连。

## 协议说明

* **HTTP**：HTTP 代理。HTTPS、IMAP、SMTP 都通过 `CONNECT` 隧道转发，TLS 端到端，代理看不到内容。
* **SOCKS5**：真正的 SOCKS5（RFC 1928）协议，域名交给代理端解析。
  * HttpClient 使用 .NET 原生 `socks5://` 支持；IMAP/SMTP 使用 MailKit 的 `Socks5Client`。
  * 旧版本曾把 SOCKS5 配置当成 HTTP 代理发给 OAuth 请求（只在 Mixed 端口碰巧可用），本版本已移除。
* **SOCKS4 / SOCKS4A**：不再支持。若旧数据库里有这类配置，连接时会提示改为 SOCKS5 或 HTTP。
* 暂不支持需要用户名/密码认证的代理。

### 端口怎么选

| 代理软件 | 选 HTTP 时 | 选 SOCKS5 时 |
|---|---|---|
| Clash / Clash Verge / Mihomo | `7890`（HTTP 或 mixed-port） | `7891`（socks-port）或 mixed-port |
| v2rayN | HTTP 端口（默认 SOCKS+1）或 Mixed 端口 | SOCKS 端口（默认 10808）或 Mixed 端口 |
| Shadowsocks 客户端 | 视客户端是否提供 HTTP 端口 | 本地 SOCKS5 端口（常见 1080） |

规则很简单：**端口是什么协议就选什么协议**。Mixed 端口两种都可以；纯 HTTP 端口不能选 SOCKS5，纯 SOCKS5 端口不能选 HTTP。

## TLS 证书校验

IMAP、SMTP、HTTPS 共用同一套校验策略（`TlsPolicy`）：

* 开启证书吊销检查（旧版本为“提速”关闭了 IMAP 的吊销检查，已恢复）；
* 证书不受信任、主机名不匹配、过期、**已被吊销** → 拒绝连接；
* 仅当唯一的问题是“吊销状态未知 / 吊销服务器不可达”时放行（与主流浏览器的软失败策略一致），
  因为在需要代理的网络中 CRL/OCSP 服务器常常无法直连。

如果出现 `SslHandshakeException`，常见原因是代理软件开启了 TLS 解密 / 流量嗅探并替换了证书，
请关闭相应功能，不要尝试在本应用里关闭证书校验。

## 相关文档

- [如何获取 Gmail Client ID](HowToGetGmailClientId.md)
- [凭据存储与迁移](CredentialStorage.md)
