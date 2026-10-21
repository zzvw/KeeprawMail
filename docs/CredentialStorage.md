# 凭据存储与迁移

## 存储方式

自本版本起，账号密码/授权码与 OAuth Token **不再以明文写入 SQLite**。

* 位置：`%LOCALAPPDATA%\KeeprawMail\credentials\<SHA256(键)>.bin`
* 加密：Windows **DPAPI，作用域 CurrentUser**（附加应用专属熵）。只有同一台电脑上的同一个 Windows 用户能解密；
  复制数据库或凭据文件到其他电脑/其他用户均无法解密。
* 每个 OAuth 账号的 Access Token、过期时间、Refresh Token 作为**一个整体**加密保存，刷新时原子替换。
* 数据库 `keepraw.db` 的 `Accounts` 表只保存邮箱、服务器、端口、代理等非敏感信息；
  `Password` / `OAuthToken` / `RefreshToken` 列保留（兼容旧表结构）但始终为空。
* 日志与异常信息不包含任何 Token 或密码。

## Token 管理

所有 OAuth Token 由唯一的 `TokenManager` 提供：

* IMAP 同步、SMTP 发送、标记已读等同时需要 Token 时，每个账号只会触发**一次**刷新，其余调用直接拿到新 Token；
* 服务器拒绝 Token 时会强制刷新一次再重试；
* Refresh Token 失效（`invalid_grant`）时提示“授权已失效，请删除后重新登录”，不会拿旧 Token 反复重试。

## 旧数据库迁移

应用启动时自动执行，`Accounts.CredentialVersion` 列标记状态（0 = 旧明文，1 = 安全存储）。每个账号独立进行：

1. 把明文凭据写入 DPAPI 安全存储；
2. 立即读回并逐字比对；
3. **比对通过后**才在事务中清空该行的明文列并把 `CredentialVersion` 置为 1；
4. 有账号迁移成功时执行 `VACUUM`，清除 SQLite 空闲页中残留的明文。

任何一步失败，该账号保持原样（仍可用旧明文连接），下次启动重试。迁移不会删除任何账号。

### 回滚

如需降级回旧版本程序（旧版本只认识明文列），可运行 `KeeprawMail.exe --rollback-credential-migration`（内部调用 `AccountCredentialMigrator.RollbackAsync()`）
把安全存储中的凭据写回明文列并置回 `CredentialVersion = 0`；安全存储中的副本保留，再次升级时会重新迁移。

### 已知限制

* 重装 Windows、更换 Windows 用户或在另一台电脑上使用同一数据库时，DPAPI 无法解密，需要重新输入密码 / 重新登录 Gmail。
* 旧版本程序打开已迁移的数据库时会看到空密码，需先执行回滚。
