# Windows 手工验收清单（PR #1）

以下项目**需要真实 Windows 环境和你自己的 Google 账号 / OAuth Client ID**，CI 无法代替。
每一项完成后在方框里打勾，并在 PR 中回复结果。所有项目当前状态均为 **待人工验证**。

> 准备：从 PR 的 CI 运行页下载 `KeeprawMail-win-x64-aot` 产物（Native AOT、Windows App SDK 自包含），解压后直接运行 `KeeprawMail.exe`。
> 不要把 Client ID / Secret、Token、数据库或 `credentials` 目录上传到 PR 或 CI。

## 0. 备份

- [ ] 关闭旧版 Keepraw Mail，完整复制 `%LOCALAPPDATA%\KeeprawMail\` 到其它位置（含 `keepraw.db` 与 `Emails\`）。

## 1. 旧数据库首次启动与 DPAPI 迁移

- [ ] 用新版程序启动，原有账号全部出现在“设置 → Accounts”。
- [ ] `%LOCALAPPDATA%\KeeprawMail\credentials\` 下出现若干 `.bin` 文件（文件名是哈希，不含邮箱）。
- [ ] 用 SQLite 工具打开 `keepraw.db`：`SELECT Email, Password, OAuthToken, RefreshToken, CredentialVersion, AuthType FROM Accounts;`
      → 三个敏感列为空字符串，`CredentialVersion = 1`。
- [ ] 若状态栏提示“N 个账号的凭据未能迁移……”，记录提示内容；账号应仍能正常收信。

## 2. 重启后账号与凭据仍可用

- [ ] 完全退出并重新启动，点击同步：所有账号（密码类与 Gmail OAuth）都能收信。

## 3. Gmail 使用自己的 OAuth Client ID 登录

- [ ] 按 `docs/HowToGetGmailClientId.md` 配置自己的 Client ID（环境变量或 `oauth.credentials.json`），自己的邮箱已加入“测试用户”。
- [ ] 添加 Gmail 账号 → 浏览器打开 Google 授权页 → 授权后浏览器显示“登录成功”，应用中出现该账号。

## 4. SOCKS5 收信 + SMTP 发信

- [ ] 账号代理设为 `SOCKS5`，填写代理软件的 SOCKS5（或 Mixed）端口。
- [ ] 同步成功（IMAP 993，TLS）。
- [ ] 给自己发送一封测试邮件成功（SMTP 587，STARTTLS），在 Gmail 网页端收到。
- [ ] 关闭代理软件再同步：出现清晰的连接错误，而不是直连成功（确认没有静默绕过代理）。

## 5. HTTP 代理与强制直连

- [ ] 账号代理设为 `HTTP`（代理软件的 HTTP / Mixed 端口），同步与发信成功。
- [ ] 账号代理设为 `None (Direct Connection)`，在能直连 Gmail 的网络中同步成功；在不能直连的网络中失败（说明确实直连）。

## 6. 修改全局代理后，继承全局的账号跟随

- [ ] 账号代理设为 `Use Global Settings`。
- [ ] 全局代理设为可用代理 A → 同步成功；改为一个不可用的端口 → 同步失败；改回 A → 同步成功。（无需重新编辑账号）

## 7. Token 刷新后重启仍可用

- [ ] Gmail 账号保持登录超过 1 小时（Access Token 过期）后同步成功（触发刷新）。
- [ ] 退出并重启，再次同步成功（刷新后的 Token 已持久化）。

## 8. 已有 Gmail 账号重新授权

- [ ] 在 Google 账号“第三方应用访问权限”中移除本应用（撤销授权）。
- [ ] 同步：状态栏提示“Google 授权已失效，需要重新授权……”。
- [ ] 设置 → Accounts → 该账号的 `Re-authorize`：浏览器授权同一个邮箱 → 提示成功。
- [ ] 账号仍在原位置，代理设置未变，本地邮件与草稿都还在；同步恢复正常。
- [ ] 再次 `Re-authorize`，但在浏览器里选择**另一个** Google 账号 → 提示“账号不一致，已拒绝保存”，原账号不受影响。

## 9. 错误提示

- [ ] 添加 Gmail 时在浏览器点击“取消” → 应用提示“你在浏览器中取消了授权”。
- [ ] 添加 Gmail 时打开授权页后不操作，5 分钟后提示超时；或在等待中点“返回/取消”，登录流程结束。
- [ ] 代理不可用（端口错误）→ 提示连接失败。
- [ ] Token 被撤销 → 见第 8 项提示。

## 10. 回滚（可选）

- [ ] `KeeprawMail.exe --rollback-credential-migration` 后，旧版程序可以打开数据库并登录；再次用新版启动会重新迁移。
