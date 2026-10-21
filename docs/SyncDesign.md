# IMAP 增量同步与按需正文（PR #2 设计说明）

产品仍是 Windows 原生邮件客户端（WinUI 3 / Windows App SDK / WebView2 / MailKit / MimeKit / SQLite / Native AOT）。
同步与正文逻辑放在 `KeeprawMail.Core`（可在 CI 中测试），WinUI 层只负责连接（统一代理 + TokenManager）与界面。

| 组件 | 位置 | 职责 |
|---|---|---|
| `MailboxSyncState` / `MailboxSyncStateStore` | `Core/Storage/MailboxSyncStateStore.cs` | 每账号、每文件夹的同步状态 |
| `EmailStore`（同步部分） | `Core/Storage/EmailStore.Sync.cs` | 批次元数据 + 游标同事务写入；Flags / 删除；正文字段单独更新 |
| `MailboxSyncEngine` | `Core/Sync/MailboxSyncEngine.cs` | 单个文件夹的基线 / 增量 / Flags / 核对 |
| `AccountSyncService` | `Core/Sync/AccountSyncService.cs` | 账号级：按账号串行化、逐文件夹、分阶段报告 |
| `IImapMailSession` / `MailKitImapSession` | `Core/Imap/` | 协议边界（MailKit 4.18.1 实现，测试可替换） |
| `BodyPartSelector` / `BodyTextDecoder` / `SnippetBuilder` | `Core/Imap/MimeBodyText.cs` | 正文 MIME 部分选择、字符集解码、摘要 |
| `OnDemandBodyLoader` | `Core/Reading/OnDemandBodyLoader.cs` | 打开邮件时按需下载正文、合并并发请求 |

## 1. 数据库（结构版本 3）

`MailStoreSchema.SchemaVersion` 从 2 升到 3，迁移在每次启动时幂等执行（`IF NOT EXISTS`、列存在检查、只回填 NULL），不删除任何账号、邮件、正文缓存或凭据：

- `Emails` 增加 `ImapUid INTEGER`，从旧 `Id`（`AccountId|MailboxName|UID`）严格回填：只有 `Id` 恰好等于 `AccountId || '|' || Mailbox || '|' || 十进制 UID` 且在 uint32 范围内时才写入；无法解析的旧行保持 NULL。
- 新索引 `IX_Emails_Account_Mailbox_Uid (AccountId, Mailbox, ImapUid)`。
- 新表 `MailboxSyncState`，主键 `(AccountId, MailboxName)`，外键引用 `Accounts(Id)`：

| 列 | 类型 | 说明 |
|---|---|---|
| `AccountId` | TEXT | |
| `MailboxName` | TEXT | 服务器返回的完整名称（如 `[Gmail]/Sent Mail`），按 BINARY 比较，不分割、不做大小写折叠 |
| `UidValidity` | INTEGER | uint32，存 64 位整数无精度损失 |
| `LastProcessedUid` | INTEGER | 已成功持久化的 UID 游标 |
| `HighestModSeq` | TEXT | 63 位 MODSEQ 以十进制文本保存，按 ulong 解析 |
| `LastSuccessfulSyncAt` / `LastFullReconcileAt` | TEXT | ISO 8601 |

`AccountRepository.DeleteAsync()` 在原删除事务中、删除 `Accounts` 行之前删除该账号的 `MailboxSyncState`，失败整体回滚（PR1 语义不变）。

本地邮件 ID 保持 `AccountId|MailboxName|UID` 不变，因此两个账号 UID 相同不会串号，已有数据无需改 ID。

## 2. 首次同步（基线）

1. `EXAMINE`（只读）取得 `UIDVALIDITY`、`UIDNEXT`、`HIGHESTMODSEQ`（若支持）。
2. 空文件夹直接保存状态；否则 `UID SEARCH ALL` 取得 UID 列表（只有 UID）。
3. 旧版数据（无同步状态）：删除本地已确认不在服务器上的 UID 以及 `ImapUid` 为 NULL 的行；正文缓存在事务提交后删除。
4. 按 UID 升序每 500 个一批 `UID FETCH (UID FLAGS INTERNALDATE ENVELOPE)`，每批元数据与游标在同一个 SQLite 事务中提交，并报告进度；可取消。
5. **不下载任何正文或附件。** 新邮件摘要为空，列表显示占位文本“（打开后加载正文）”（占位文本不写数据库、不进 FTS）。

中断（网络错误、数据库错误、取消、退出）后，已提交批次保留，下次从游标处继续；UPSERT 保证重试不产生重复记录。

### 旧版数据库的保守处理

旧版从未记录 UIDVALIDITY，无法确认旧 UID 映射在此期间是否失效，因此不会假装“恢复”过去的 UIDVALIDITY，而是：

- 服务器上已不存在的 UID：删除本地行与正文缓存；
- 同一 UID 仍存在：比较主题、发件人地址与时间（秒级）。一致 ⇒ 保留正文缓存与摘要；不一致 ⇒ 视为映射不可信，清除正文路径与摘要并在提交后删除缓存文件（打开时按需重新下载）；
- 已读 / 星标一律以服务器为准；
- 基线完成后记录当前 UIDVALIDITY，此后按正常增量规则处理。

## 3. 增量同步

- 新邮件：只有 `UIDNEXT > 游标 + 1` 时才发送 `UID SEARCH UID <游标+1>:*`，并过滤掉 `n:*` 在 n 大于最大 UID 时返回的最大 UID（RFC 3501 中 `*` 表示最大 UID）。按批次获取，游标随批次在事务内前移；全部完成后游标可前移到 `UIDNEXT-1`（UID 空洞 / 末尾邮件已删除时，下次无需再 SEARCH）。
- 没有任何变化时：只有 `EXAMINE` +（无 CONDSTORE 时）一次最近 200 封的 `UID FETCH (UID FLAGS)`；CONDSTORE 且 `HIGHESTMODSEQ` 未变时只有 `EXAMINE`。
- 游标绝不会越过未成功持久化的数据；单个文件夹失败不影响其它文件夹，也不推进失败文件夹的游标。

## 4. UIDVALIDITY 变化

一个事务内只删除该账号、该文件夹的本地邮件与同步状态，然后重新基线；提交后只删除 `AppFileCache` 内的旧正文文件（失败写入 PendingCleanup，下次启动重试）。其它账号、其它文件夹不受影响；旧 UID 映射不再使用，UID 重用的新邮件不会挂上旧正文。

## 5. Flags 与远端删除：不同服务器能力下的保证

| 服务器能力 | 每次刷新 | 远端删除 | 旧邮件 Flags |
|---|---|---|---|
| QRESYNC（已 `ENABLE`） | `UID FETCH 1:游标 (FLAGS) (CHANGEDSINCE m)`，同时收到 `VANISHED` | 立即发现 | 立即一致 |
| CONDSTORE（无 QRESYNC，Gmail 属此类） | `CHANGEDSINCE` 只取变化；MODSEQ 未变则不发请求 | 每 6 小时 UID-only 全量核对（`UID SEARCH ALL`） | 立即一致 |
| 都不支持（QQ / 163 等常见） | 只检查最近 200 封（按本地 UID）的 Flags | 每 6 小时 UID-only 全量核对 | 每 6 小时全量核对时校正（按 1000 个 UID 一批，只取 FLAGS） |

- 不支持增量变更通知的服务器**不能**保证每次刷新立即发现所有历史邮件的状态变化或删除；上表的间隔是保证的上限（以本机上次核对时间为准，持久化在 `LastFullReconcileAt`）。
- 只有确认 UID 已不在服务器上（`UID SEARCH ALL` 结果或 `VANISHED`）才删除本地记录；只检查游标以内的 UID；所有删除仅作用于本地，同步全程以 `EXAMINE` 只读打开文件夹，不会触发任何服务器端删除或标记变化。
- QRESYNC 的兜底全量核对间隔为 7 天；`ENABLE QRESYNC` 失败时自动退回 CONDSTORE / UID 核对策略。

## 6. 按需正文

- 普通同步（含首次同步）绝不下载正文或附件。
- 打开邮件（`MailPage.LoadEmailIntoTabAsync` → `MainViewModel.EnsureBodyAvailableAsync`）：
  1. 缓存路径在应用缓存目录内且文件可读 ⇒ 直接使用，不联网；
  2. 否则按**邮件所属账号**（不是当前选中账号）走与同步相同的连接路径（`ProxyResolver` + 统一 `TokenManager`，不会绕过代理直连）；
  3. `UID FETCH (BODYSTRUCTURE RFC822.SIZE)` → 选择正文部分 → `BODY.PEEK[<section>]`（MailKit `GetBodyPartAsync`，同时取该部分的 `.MIME` 头），**只下载这一部分**，不设置 `\Seen`；
  4. 写入 `AppPaths.BodyCache`（SHA-256 文件名），只更新 `BodyLocalPath` 与 `Snippet`（不覆盖已读、星标、主题等）；FTS5 由触发器同步；
  5. 失败在标签页内显示阶段与原因，“重试”会真正重新下载；
  6. 同一封邮件的并发打开合并为一次下载；关闭标签页会取消不再需要的下载。
- 正文选择：`multipart/alternative` 优先 html，其次纯文本；`mixed` / `related` 取第一个能提供正文的子部分；`Content-Disposition: attachment` 的文本、附带的 `message/rfc822`、二进制部分都不作为正文。
- 字符集：先按 Content-Transfer-Encoding 还原字节，再按声明字符集解码（gb2312 / gbk 按代码页 936，gb18030 按 GB18030）；未声明或声明为 us-ascii / iso-8859-1 却含 8 位字节时，依次尝试严格 UTF-8、HTML `<meta charset>`、GBK 双字节规则（且 ≥70% 字节对落在 GB2312 区，避免把 Latin-1 误判为中文），否则按 Latin-1，不丢字节。
- 降级：只有附件 ⇒ 显示“没有可显示的文本正文，只包含附件：… 附件不会自动下载”；服务器不提供 BODYSTRUCTURE ⇒ 邮件 ≤ 2 MB 才下载整封并提取正文，否则给出明确提示。不会静默显示空白。

## 7. 并发

- `AccountSyncService` 用 `SingleFlight` 按账号串行化：同一账号同一时间只有一个同步，连续点击刷新会合并到正在进行的同步；不同账号仍并发。
- 正文下载与同步使用不同连接，正文写入只更新正文字段，不会与同步互相覆盖。

## 8. 可复现的请求计数

`MailKitSessionTests.RequestCount_LegacyVsIncremental_Measured` 用真实 MailKit 客户端连接本地脚本化 IMAP 服务器，统计 IMAP 命令数（不含 LOGIN / LIST / CAPABILITY / LOGOUT）和服务器发送字节数，结果写入测试输出与 `sync-request-comparison.txt`。
