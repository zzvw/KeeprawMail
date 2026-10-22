# IMAP 增量同步、按需正文、列表摘要预览、统一收件箱与本地时间（PR #2 / PR #3 及后续修复设计说明）

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
| `PreviewPartSelector` / `TruncatedTransferDecoder` / `PreviewTextExtractor` / `PreviewDiagnostics` | `Core/Imap/PreviewText.cs` | PR3：预览部分选择（text/plain 优先，保留同一 alternative 的 html）、截断内容的传输解码、摘要可用性判断、脱敏诊断 |
| `HtmlTextStripper` | `Core/Imap/MimeBodyText.cs` | PR3：按 HTML 解析规则的有状态文本提取（不依赖 `</head>`，style / 注释 / 条件注释跨越下载边界） |
| `IImapMailSession.FetchPreviewsAsync` | `Core/Imap/MailKitImapSession.cs` | PR3：BODYSTRUCTURE 批量 + 有界部分下载 `BODY.PEEK[section]<offset.count>` |
| `EmailStore`（预览部分） | `Core/Storage/EmailStore.Preview.cs` | PR3：预览候选（与列表同一查询）与预览结果写入 |
| `MailPreviewService` | `Core/Preview/MailPreviewService.cs` | PR3：后台预览任务（按账号合并、复用会话、可取消、退避） |
| `EmailStore.DefaultListSourceSql` / `DefaultListFilterSql` / `ListOrderSql` | `Core/Storage/EmailStore.cs` | 默认首页（统一收件箱）与预览候选共用的筛选与排序（第 10 节） |
| `MailDateFormatter` | `Core/Formatting/MailDateFormatter.cs` | 用户可见邮件时间按 Windows 系统时区显示（第 11 节） |

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
5. **不下载任何正文或附件。** 新邮件写入时 `PreviewState = Pending`、摘要为空。同步结束后由独立的后台预览任务（第 9 节）为列表中的邮件生成摘要；未就绪时列表显示状态提示（提示文本不写数据库、不进 FTS）。

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
| CONDSTORE（无 QRESYNC，Gmail 属此类） | `CHANGEDSINCE` 只取变化；MODSEQ 未变则不发请求 | **EXISTS 少于本地时下一次同步立即发现**（移出检测，见下）；此外每 6 小时 UID-only 全量核对（`UID SEARCH ALL`） | 立即一致 |
| 都不支持（QQ / 163 等常见） | 只检查最近 200 封（按本地 UID）的 Flags | **EXISTS 少于本地时下一次同步立即发现**；此外每 6 小时 UID-only 全量核对 | 每 6 小时全量核对时校正（按 1000 个 UID 一批，只取 FLAGS） |

- 不支持增量变更通知的服务器**不能**保证每次刷新立即发现所有历史邮件的状态变化或删除；上表的间隔是保证的上限（以本机上次核对时间为准，持久化在 `LastFullReconcileAt`）。
- 只有确认 UID 已不在服务器上（`UID SEARCH ALL` 结果或 `VANISHED`）才删除本地记录；只检查游标以内的 UID；所有删除仅作用于本地，同步全程以 `EXAMINE` 只读打开文件夹，不会触发任何服务器端删除或标记变化。
- QRESYNC 的兜底全量核对间隔为 7 天；`ENABLE QRESYNC` 失败时自动退回 CONDSTORE / UID 核对策略。

### 5.1 移出检测（EXISTS 计数触发的 UID-only 核对）

Gmail 网页端**归档**一封邮件，只是去掉它的 `\Inbox` 标签：IMAP 上表现为该 UID 从 `INBOX` 中 EXPUNGE（邮件仍在 `[Gmail]/All Mail`）；**移入垃圾箱**则是从 `INBOX` 移出并在 `[Gmail]/Trash` 中以新 UID 出现。Gmail 支持 CONDSTORE 但不支持 QRESYNC，`CHANGEDSINCE` 不会报告被移出的 UID，因此 PR3 中本地 INBOX 记录要等 6 小时全量核对才删除。

做法（`MailboxSyncEngine`，选项 `MailboxSyncOptions.DetectRemovalsByCount`，默认开启）：

1. 每次增量同步都已发送 `EXAMINE`，响应中的 `EXISTS` 就是服务器该文件夹当前的邮件数，**不需要任何额外请求**；
2. 新邮件处理完之后，比较 `EXISTS` 与本地该文件夹 `UID ≤ 游标` 的行数（`EmailStore.CountLocalUidsAsync`，本地 SQLite 查询）；
3. 本地更多 ⇒ 服务器上有邮件离开了该文件夹（归档、移入垃圾箱、其它客户端移动或删除）⇒ 立即发送一次 `UID SEARCH ALL`（只返回 UID），删除本地游标以内、服务器上已不存在的 UID（与全量核对相同的删除规则与事务）；
4. 计数一致 ⇒ 什么都不发。

保证与边界：

- 只删除 `UID SEARCH ALL` 确认不存在的 UID，删除只作用于该账号、该文件夹的本地记录（`(AccountId, Mailbox, UID)`）；同一封邮件在 Sent / All Mail 等其它文件夹中的记录、正文缓存、预览状态不受影响；
- 全程 `EXAMINE` 只读，不发送 STORE / COPY / MOVE / EXPUNGE，不改服务器端邮件、标签或已读状态；
- 不改 UIDVALIDITY、UID 游标或 MODSEQ，**不更新 `LastFullReconcileAt`**：无 CONDSTORE 服务器的全量 Flags 校正仍按 6 小时间隔进行；移出检测本身不取 Flags、正文或附件；
- 归档与新邮件同时发生时（本地 = 旧数 + 新邮件，服务器 = 旧数 − 移出 + 新邮件）同样能发现；`EXAMINE` 之后、`SEARCH` 之前到达的新邮件只会让本地多于 `EXISTS`，最多多发一次 `UID SEARCH ALL`（无副作用）；
- 本地缺少服务器上的某些行、且缺少数恰好等于被移出数时计数会相等——正常同步不会产生这种状态（游标只在批次持久化后前移）；6 小时全量核对仍是兜底。

是否需要区分手动同步与后台同步：目前所有同步都由用户触发（工具栏“同步”按钮，或重新授权后同步该账号），没有定时后台同步；移出检测只在确有邮件被移出时才产生请求，因此**不区分**手动 / 后台。将来增加定时后台同步时，开销同样与用户在服务器上的操作次数成正比，而不是与同步次数成正比；如仍需限制，可只在手动同步时开启 `DetectRemovalsByCount`（选项已存在，无需改引擎）。

流量（`ArchiveReconcileMetricsTests.GmailArchive_ManualSync_RequestCounts_Measured`，真实 MailKit ↔ 脚本化 IMAP 服务器，CONDSTORE、INBOX 5,000 封，结果写入 `archive-reconcile-metrics.txt` 并由 CI 打印上传）：

| 场景 | IMAP 命令 | 服务器发送字节 |
|---|---|---|
| 无变化手动同步 | 1（EXAMINE） | 495 |
| 网页端归档 1 封后手动同步 | 3（EXAMINE + CHANGEDSINCE + UID SEARCH ALL） | 24,444 |
| 随后再次同步 | 1 | 495 |
| 对照：关闭移出检测（PR3 行为） | 2 | 520（归档邮件仍在首页，最长 6 小时） |

`UID SEARCH ALL` 的响应约为每封 INBOX 邮件 5 字节（UID 十进制位数 + 空格）：INBOX 1 万封约 60 KB，5 万封约 300 KB，只在检测到移出的那次同步发生一次；不下载正文、附件、元数据或 Flags。大邮箱中 `SEARCH ALL` 的服务器耗时与一次全量核对相同（Gmail 对 INBOX 通常在 1 秒内返回）。

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

## 9. 列表摘要预览（PR #3）

目标：邮件在打开前就能在列表中显示最多 3 行摘要，同时保持 PR2 的核心设计——普通同步不下载完整正文和附件，完整正文只在打开时下载。
预览与打开正文是两条互不调用的流程：预览从不调用 `OnDemandBodyLoader.EnsureBodyAvailableAsync()`，打开正文也从不调用预览。

### 9.1 IMAP：有界部分下载

`IImapMailSession.FetchPreviewsAsync(mailbox, uids)`（`MailKitImapSession` 实现）对一批邮件：

1. 一条 `UID FETCH <set> (UID BODYSTRUCTURE)` 批量取得结构（不含任何正文内容）。服务器对整批回答 `NO`（Gmail 在集合中某一封无法取出时会对整条 FETCH 回答 `NO Some messages could not be FETCHed`，MailKit 随之丢弃整批结果）且连接仍可用时，**逐封重试 BODYSTRUCTURE**，只让被拒绝的那一封失败；
2. 按结构选择正文部分（9.2），对每封邮件发出 `UID FETCH <uid> (BODY.PEEK[<section>]<0.4096>)`；
3. 没有“可用”文本时至多再发一次请求（9.2 的回退策略），请求量为“8 KiB 减去已收到的字节”；
4. 每封邮件合计不超过 **8 KiB（8192 字节）** 传输内容、不超过 2 次部分下载请求（`PreviewFetchLimits`）；**绝不为了摘要下载整封邮件、完整正文或附件**；
5. 文件夹以 `EXAMINE` 打开、使用 `BODY.PEEK`，不改变服务器上的 `\Seen`；实际收到的字节数（MailKit 返回的流长度）逐封统计并返回；
6. 单封失败的隔离：`NO` / `BAD` ⇒ 只有这一封 Failed；本地解析 / 解码异常且连接仍可用 ⇒ 只有这一封 Failed（`local_error`）；断线、`ImapProtocolException`、超时等连接级错误 ⇒ 本封与本批之后的邮件 Failed（`connection`），已得到的结果照常写入。

**MailKit 4.18.1 `GetStreamAsync` 的实测结论**（`PreviewMailKitTests.MailKit_GetStreamAsync_PartialFetch_SemanticsVerified`，以服务器收到的命令与发送的字节为准）：

- `IMailFolder.GetStreamAsync(UniqueId, string section, int offset, int count)` 发出 `UID FETCH uid (BODY.PEEK[section]<offset.count>)`，是服务器端的有界部分下载，不是下载完整正文后在客户端截断；
- `offset` / `count` 是该 MIME 部分**传输编码后内容**中的字节位置（RFC 3501 §6.4.5）；返回的流是服务器发送的原始字节，**MailKit 不做 base64 / quoted-printable / 字符集解码**；offset 超出末尾时返回空流；
- `GetStreamAsync(UniqueId, BodyPart, offset, count)` 直接使用 `BodyPart.PartSpecifier`。单部分（非 multipart）邮件的根部分 `PartSpecifier` 是空字符串，这个重载会变成 `BODY.PEEK[]<0.4096>`——整封邮件从邮件头开始的前 4 KiB（邮件头较大时一个正文字节都拿不到）。因此实现改用字符串重载，根部分使用 `TEXT`，其余使用部分编号（如 `1`、`1.1`、`1.1.1`）。
- MailKit 在服务器没有返回所请求的 section（例如 UID 已不存在）时抛出 `MessageNotFoundException`；`NO` / `BAD` 抛出 `ImapCommandException`（不断开连接）；响应无法解析时抛出 `ImapProtocolException`（并断开连接）。
- 不规范的服务器若忽略 `<offset.count>` 发送完整内容，MailKit 会照收（协议层无法阻止）；实现只解码请求的前 count 字节，并如实统计实际收到的字节数。

### 9.2 MIME 选择、回退策略与解码

**结构选择**（`PreviewPartSelector`，只看 BODYSTRUCTURE）：在同一个“正文位置”（同一个 `multipart/alternative`，或单独的一个文本部分）同时记下 `text/plain` 与 `text/html`；`multipart/mixed` / `related` / `signed` 等取第一个能提供正文的子部分（之后的部分视为附件 / 内嵌资源）；`Content-Disposition: attachment` 的文本、`message/rfc822` 附带的邮件、`text/calendar` 等其它文本类型、二进制部分一律不作为正文。结构中**确实没有**文本正文部分时才是 NoText（`no_text_part`），且不发任何部分下载。

**回退策略**（每封 ≤ 2 次请求、≤ 8 KiB）：

| 步骤 | 做法 |
|---|---|
| 第 1 次 | 有非空 `text/plain`（BODYSTRUCTURE 报告 0 字节的跳过）⇒ 读它的 `<0.4096>`；否则读 `text/html` 的 `<0.4096>` |
| 结束条件 | 得到“可用”摘要（`PreviewTextExtractor.IsUseful`：去掉 URL、“在浏览器中查看 / View this email in your browser / Click here / 退订”等套话与少量功能词后仍有文字）即 Ready |
| 第 2 次（纯文本无用且同一 alternative 有 html） | 改读 html 的 `<0.(8192−已收字节)>`——空白 / 只有链接 / 只有套话的纯文本版本通常只有几十字节，因此 html 可得到约 8 KiB，足以越过常见的 5–7 KiB CSS 头部 |
| 第 2 次（其它情况） | 同一部分从第一次结束处续读 `<已收字节.(8192−已收字节)>`，两段**拼接后整体**做传输解码、字符集解码与 HTML 扫描（跨越两次下载边界的 `<style>`、base64 组、QP 转义、多字节字符都能正确处理） |
| 判定 | 有可用摘要 ⇒ Ready；所有文本部分都已完整读完 ⇒ 有文字则 Ready（邮件本身只有这些字）、否则 NoText（`empty_text`）；仍有未读完的文本 ⇒ **Unavailable（`beyond_budget`）**，不是 NoText |

传输编码名称容忍常见的非标准写法（`8-bit`、`7bits`、`base-64`、`quoted printable`、`qp`、大小写 / 引号 / 空白）；确实未知的编码（如 `x-uuencode`）不下载：另一个版本可用时改用它，否则 Unavailable（`unknown_cte`）。服务器不提供 BODYSTRUCTURE 时同样不下载，记为 Unavailable（`no_structure`，因为无法知道有没有文本）。

**截断安全的传输解码**（`TruncatedTransferDecoder`，不依赖任何假设内容完整的 API）：base64 只解码完整的 4 字符组（末尾不完整的组丢弃，忽略换行，支持中途填充）；quoted-printable 处理 `=XX` 与软换行，末尾不完整的 `=`、`=X`、`=\r` 丢弃，非法转义原样保留；7bit / 8bit / binary 原样。

**截断安全的字符集解码**（`BodyTextDecoder.DecodePrefix`）：声明字符集（UTF-8、GBK / GB2312 按代码页 936、GB18030 含四字节序列……）用解码器“不刷新”解码，末尾不完整的多字节字符不会变成替换字符；未声明或声明为 ISO-8859-1 时，先去掉末尾不完整序列再做严格 UTF-8 / GBK 探测（与打开正文的规则一致）。

**HTML 文本提取**（`HtmlTextStripper` + `SnippetBuilder.FromBody(text, isHtml, truncated)`，与打开正文、本地缓存重算共用）：按 HTML 解析规则做有状态扫描，取代旧的正则：

- `style` / `script` / `title` 等原始文本元素一直跳到对应结束标记，中间的 `<!--`、`<p>`、`</div>`（如 CSS `content:"…"`）不会误判；
- **不依赖 `</head>`**：head 中的 CSS 作为 style 跳过；head 中出现非 head 元素（或 `<body>`、非空白文本）时按 HTML 规则隐式结束 head。旧实现在截断内容中遇到“未闭合的 `<head`”会从开头截断，省略 `</head>` 的模板无论读多少字节都是 NoText；
- 注释（含 `<!--[if mso]> … <![endif]-->`、空注释 `<!-->`）、`<!DOCTYPE>`、`<![if !mso]>` / `<![endif]>`、`<?xml?>`、CDATA 跳过；下层显示的条件注释（`<!--[if !mso]><!--> … <!--<![endif]-->`）中的内容照常显示；
- 属性值中的 `>`（只认紧跟 `=` 的引号，与浏览器一致）不会提前结束标签；
- 遇到未闭合的注释 / 原始文本元素 / 标签（截断处）即停止，绝不输出 CSS、脚本或标记；末尾不完整的实体丢弃；
- `display:none` 的预览文字（preheader）与其它邮件客户端一样作为摘要开头保留；其后的 `&zwnj;&nbsp;`、`&#847;`、`&#8199;`、零宽字符、BOM 等填充被去除并压缩空白；上限 **280 字符**（不拆开代理对）。结果只有纯文本，界面用 `TextBlock` 显示，列表行中没有 WebView2。

### 9.3 预览状态与数据库（结构版本 4 → 5）

`Emails` 的预览列（`MailStoreSchema.EnsureAsync` 中按列存在检查幂等添加；每次升级与 `user_version` 在**同一事务**中提交）：

| 列 | 类型 | 版本 | 说明 |
|---|---|---|---|
| `PreviewState` | INTEGER NOT NULL DEFAULT 0 | 4 | 0 Pending、1 Ready、2 NoText、3 Failed、**4 Unavailable**（v5 新增取值） |
| `PreviewAttempts` | INTEGER NOT NULL DEFAULT 0 | 4 | 连续失败次数 |
| `PreviewNextRetryAt` | TEXT | 4 | Failed 的下次重试时间（UTC ISO 8601，文本比较即时间比较） |
| `PreviewAlgo` | INTEGER NOT NULL DEFAULT 0 | 5 | 产生当前状态的预览算法版本：0 = v5 之前写入（PR3 第一版算法 / v4 回填），1 = 当前算法（`PreviewAlgorithm.Current`） |
| `PreviewDiag` | TEXT | 5 | 脱敏诊断（9.7），Ready 时为 NULL |

**状态机**

| 状态 | 含义 | 是否再取 | 列表显示 |
|---|---|---|---|
| Pending | 尚未生成 | 是（在列表前 100 封内时） | 正在加载预览… |
| Ready | 有摘要 | 否 | 摘要文本 |
| NoText | **确实没有文本**：结构中没有文本正文部分（`no_text_part`），或文本部分已完整读完但没有文字（`empty_text`） | 否（当前算法下） | 无文本预览 |
| Unavailable | **有文本部分，但预算内没有可用摘要**（`beyond_budget`、`unknown_cte`、`no_structure`、`cache_missing`） | 否（结果不会变）；算法升级时可凭 `PreviewAlgo` 有选择地重试；打开邮件后由完整正文生成摘要 | 打开邮件查看内容 |
| Failed | 本次失败（`uid_missing`、`server_no`、`server_no_structure`、`connection`、`local_error`、`structure_fetch`、`session`、`not_returned`） | 是：5 分钟 → 30 分钟 → 2 小时 → 12 小时退避 | 预览暂不可用 |

- v4 回填（只在从 < 4 升级时执行一次，只改 `PreviewState`）：已有摘要 ⇒ Ready；正文已缓存但摘要为空 ⇒ NoText；其余 ⇒ Pending。
- **v5 一次性重新尝试**（只在从 < 5 升级时执行一次，与 `user_version = 5` 同一事务，只改预览列，不改 `Snippet` / `BodyLocalPath` / 已读 / 星标 / UID / 同步状态，FTS 不受影响）：
  1. `PreviewState = 2 AND PreviewAlgo < 1` 且**没有正文缓存**、摘要为空的行 ⇒ Pending（下次预览用新算法重新判定一次；结果写入 `PreviewAlgo = 1`，之后不再重复）；
  2. `PreviewState = 3 AND PreviewAlgo < 1` 的行 ⇒ 清除退避（`PreviewAttempts = 0`、`PreviewNextRetryAt = NULL`），下次预览立即重试一次；
  3. **有正文缓存**的旧 NoText（多为 v4 回填）保持原状，由预览任务从本地缓存**零网络**重算（9.4），写入 `PreviewAlgo = 1`，每行只做一次。
- 预览写入（`ApplyPreviewResultsAsync`）只更新 `Snippet` 与预览列：不清空 `BodyLocalPath`，不改已读 / 星标 / 主题 / 发件人 / UID，不读写 `MailboxSyncState`；只更新 Pending / Failed 且未缓存正文的行；写入前确认本次 `EXAMINE` 的 `UIDVALIDITY` 与已保存的一致。Ready / NoText / Unavailable 写入当前算法版本。FTS5 由 `Emails_au`（`UPDATE OF Snippet`）触发器同步。
- 同步：新邮件以 Pending 插入；元数据 UPSERT 不触碰预览列；旧版映射无法确认而清除摘要时，预览状态一并重置为 Pending。
- 打开正文（`UpdateBodyAsync`）：完整正文生成的摘要覆盖预览摘要并设为 Ready；完整正文没有文本时保留已有预览摘要（没有时为 NoText，`PreviewDiag = r=body_empty`）；写入当前算法版本。
- 取消时未处理的邮件保持原状态（Pending），下次继续。

### 9.4 后台预览任务（`MailPreviewService`）

- 候选与列表使用同一查询（`EmailStore.DefaultListSourceSql`，第 10 节）：每个账号 **INBOX** 按真实时间倒序的前 100 封（`EmailStore.DefaultPerAccountLimit`，`PARTITION BY AccountId`；首页不显示的文件夹不是候选），只取 Pending 或已到重试时间的 Failed、`ImapUid` 已知、未缓存正文的邮件；不是整个邮箱历史。**没有候选时不建立任何连接。**
- **本地缓存重算**（构造时传入正文缓存目录时；应用中传入 `AppPaths.BodyCache`）：每轮开始先处理该账号“有正文缓存、`PreviewState = 2`、`PreviewAlgo < 1`”的行（不受前 100 封限制，每轮最多 500 封），只读应用缓存目录内的文件（最多 2 MB），用当前摘要算法生成摘要：有文字 ⇒ Ready；缓存内容是打开正文时写入的“没有可显示的文本正文…”说明 ⇒ NoText（`no_text_part`）；HTML / 文本中确实没有文字 ⇒ NoText（`empty_text`）；缓存文件丢失或不可读 ⇒ Unavailable（`cache_missing`，打开邮件时会重新下载）。零网络，结果写入 `PreviewAlgo = 1`，因此只做一次。
- 触发：每个账号的元数据同步完成后（同步报告不是 Failed 时）安排一次，不等待预览；应用启动后第一次加载列表时为列表中的**每个账号**安排一次（没有候选的账号不建立连接，只做上面的本地重算）。
- 每个账号一个会话（`ImapService.OpenSessionAsync`：该账号自己的代理与 TokenManager），按文件夹分组复用，不按邮件重连或重新认证；文件夹按其中最新邮件排序、组内按时间倒序；每 20 封一批，每批结果立即写库并通知界面。
- 同一账号重复请求合并：运行中再次请求只会在本轮结束后再跑一轮；不同账号可并行。
- 失败隔离：见 9.1 第 6 条；整批 BODYSTRUCTURE 失败（连接级）时本批与之后的批次记为 Failed（`structure_fetch`）；会话建立失败（离线、需要重新授权）时候选记为 Failed（`session`）。预览任何失败都不会让成功的同步变成失败，也不推进或修改 UID 游标。
- `Missing`（UID 已不在该文件夹）记为 Failed（`uid_missing`）并退避，不删除任何本地记录；本地陈旧记录由同步的 UID 核对删除（移出检测在下一次同步时处理，兜底全量核对每 6 小时一次，见第 5、5.1 节）。
- 界面：`PreviewsUpdated` 事件经 UI 线程的 `SynchronizationContext` 更新 `EmailMessage.Snippet` / `PreviewState`；列表重载与预览写入交错时，用最近收到的预览结果补齐新列表项。

### 9.5 列表显示

`MailPage.xaml` 摘要 `TextBlock` 绑定 `EmailMessage.SnippetDisplay`（`MaxLines="3"`、`TextWrapping="Wrap"`、`TextTrimming="CharacterEllipsis"`），三栏布局、星标、时间、发件人、主题不变；提示文本只用于显示，不写数据库、不进 FTS：

| 状态 | 显示 |
|---|---|
| Ready（或打开正文后已有摘要） | 摘要文本 |
| Pending | 正在加载预览…（淡色） |
| NoText | 无文本预览（淡色） |
| Unavailable | 打开邮件查看内容（淡色） |
| Failed | 预览暂不可用（淡色） |

### 9.6 实测请求数（`PreviewMetricsTests.Pr3_PreviewRequestCounts_Measured`）

真实 MailKit 4.18.1 ↔ 本地脚本化 IMAP 服务器；500 封邮件，每 5 封带 200 KB 附件，纯文本正文传输编码后超过 4 KiB；统计 LOGIN / LIST / CAPABILITY 以外的 IMAP 命令与服务器发送的全部字节。测试数据固定（含 Message-ID），结果在任何环境下可复现；CI 每次运行都会打印 `preview-request-metrics.txt` 并作为构件上传。结果：

| 场景 | IMAP 命令 | 服务器发送字节 | 说明 |
|---|---|---|---|
| 首次同步 500 封 | 3 | 154,316 | EXAMINE + UID SEARCH ALL + 1 批元数据；正文 / 附件内容 0 字节 |
| 预览列表 100 封 | 106 | 446,208 | EXAMINE 1 + BODYSTRUCTURE 5 批 + 部分下载 100；部分内容 409,600 字节（每封 4,096，上限 8,192）；完整正文 0；附件 0 |
| 列表外的 400 封 | 0 | 0 | 保持 Pending |
| 无变化再同步 / 随后预览 | 2 / 0 | 6,868 / 0 | 没有候选，预览不建立连接 |
| 新增 10 封后同步 / 随后预览 | 4 / 12 | 9,709 / 45,032 | 只预览 UID 501–510（部分下载 10） |
| 打开未缓存的带附件邮件 | 3 | 1,174 | 只有 html 正文部分（MIME 头 + 内容），预览请求 0，附件 0 |
| 再次打开（已缓存）/ 随后预览 | 0 / 0 | 0 / 0 | |
| 真实结构混合 20 封（账号 rw）/ 随后再预览 | 31 / 0 | 106,018 / 0 | EXAMINE 1 + BODYSTRUCTURE 1 + 部分下载 29（AA 类 5×2、GitHub 类 5×1、Tiptrans 类 5×2、只有附件 3×0、CSS 头部 > 8 KiB 2×2）；部分内容 99,470 字节，单封最多 8,192；Ready 15 / NoText 3 / Unavailable 2 / Failed 0 |

第 1–6 行在本次修复前后完全相同（普通邮件纯文本可用时仍是每封一次 4 KiB 请求）。
统一收件箱（第 10 节）之后，账号 rw 的 20 封邮件放在它自己服务器的 `INBOX` 中（预览只针对首页可见的 INBOX）；命令数、部分下载次数与字节完全相同，总字节比之前（文件夹名 `RealWorld`）少 65 字节，只是 `EXAMINE` 等响应中的文件夹名与 UID 状态不同。

### 9.7 预览诊断（脱敏）

NoText、Unavailable、Failed 都记录诊断，写入 `Emails.PreviewDiag`，并在 `diagnostics.log` 中写一行 `[preview] preview-diag state=<状态> folder=<文件夹> r=… t=… q=… b=… [x=…]`：

```
r=<原因代码>;t=<MIME 类型,…>;q=<部分下载请求数>;b=<收到的字节数>[;x=<异常类型名>][;src=cache]
例：r=beyond_budget;t=text/plain,text/html;q=2;b=8192
    r=no_text_part;t=application/pdf;q=0;b=0
    r=uid_missing;q=0;b=0
    r=server_no;t=text/plain;q=1;b=0;x=ImapCommandException
```

只包含原因代码、MIME 类型（只保留 type/subtype 的合法字符，最多 4 个）、请求数、字节数与异常类型名；**不包含正文、主题、地址、凭据或服务器返回的文字**（`PreviewDiagnostics.Format` 会去掉 token 之外的字符）。文件夹不写入该列（同一行的 `Mailbox` 已是文件夹），只出现在日志行中。每轮的汇总行另有 `unavailable=`、`cacheRecomputed=`、`maxPerMessage=`。

### 9.8 真实邮件中的误判：根因（Windows 实测反馈）

| 现象 | 典型结构 | 根因（旧实现） | 修复 |
|---|---|---|---|
| American Airlines 等交易 / 营销邮件“无文本预览” | `multipart/alternative`：几乎为空、只有空白 / 链接 / “View in browser”的 `text/plain` + QP 编码的 `text/html`，head 中 5–8 KiB 的响应式 CSS、Outlook 条件注释、`display:none` 预览文字 + `&zwnj;&nbsp;` 填充 | ① 纯文本已完整读完却无文字时只给 html **4 KiB**，4 KiB 内全是 head ⇒ 永久 NoText；② 纯文本只有 URL / 套话也算“有意义”，直接拿链接当摘要或不再看 html；③ 纯文本前 4 KiB 是填充时继续读填充而不是 html；④ “预算内没读到文字”与“没有文本”都记为 NoText，且永不重试 | 剩余预算（约 8 KiB）从头读 html；`IsUseful` 识别链接 / 套话；Unavailable 与 NoText 分开；v5 一次性重新尝试 |
| Tiptrans 等单部分 HTML“无文本预览” | 单部分 `text/html`（base64 / QP），大段内联 CSS；部分模板省略 `</head>` | ⑤ 截断内容中“未闭合的 `<head`”从开头截断：省略 `</head>` 时无论读多少都是空；⑥ CSS 超过 8 KiB 时一律 NoText | 有状态 HTML 扫描，不依赖 `</head>`；CSS 超过 8 KiB 时如实记为 Unavailable（见 9.9） |
| 部分 GitHub 通知“预览暂不可用” | `multipart/alternative`：QP / 7bit 的 UTF-8 `text/plain` + `text/html`（结构本身没有问题，纯文本第一次 4 KiB 即可得到摘要，见 `GitHubNotificationLike_AlternativeQpUtf8_PlainReadyInOneRequest`） | ⑦ 同步之后、预览之前邮件在服务器上被归档 / 删除 / 移走（通知类邮件常被批量处理），本地记录要等 UID 核对（Gmail 6 小时）才清除；BODYSTRUCTURE 对该 UID 没有数据 ⇒ `Missing` ⇒ 显示“预览暂不可用”且无任何诊断；⑧ 服务器对整批 BODYSTRUCTURE 回答 `NO`（Gmail 的 “Some messages could not be FETCHed”）时，本批与之后**所有批次**都记为 Failed；⑨ 单封的本地解析异常被当作连接级错误，让本批其余邮件一起 Failed | `uid_missing` 诊断；整批 `NO` 时逐封重试结构，只让被拒绝的那一封失败；本地异常只影响一封；所有 Failed 带原因代码与异常类型 |

GitHub 一行中的 ⑦⑧⑨ 是根据代码路径与 Gmail 的已知行为推断、并在脚本化服务器中复现的；Windows 实测中具体是哪一种，请按 `docs/WindowsManualVerification.md` 第 14 节查看 `PreviewDiag` / `preview-diag` 日志中的 `r=` 确认。

### 9.9 8 KiB 上限下仍然存在的限制

- 邮件中正文文字之前的内容（HTML head 中的 CSS、条件注释、导航表格等，按**传输编码后**的字节计；base64 膨胀约 33%，QP 中每个 `=` 变为 `=3D`）超过约 8 KiB 时，预览得不到文字 ⇒ Unavailable（“打开邮件查看内容”），不会为了摘要下载更多；打开邮件后由完整正文生成摘要。
- IMAP 只能按字节区间部分下载，无法在服务器端搜索 `<body>` 的位置；实现不做“跳过一段再读”的猜测（可能从正文中间开始、或落在 base64 / QP / 标签中间），因此第二次请求总是与第一次连续，或从 html 版本的开头读。
- `multipart/mixed` 中第一个文本部分为空、真正的正文在后面的兄弟部分时，只看第一个正文位置（与打开正文的选择规则一致）。
- 有用性判断（`IsUseful`）是保守的套话 / 链接识别，只决定是否值得用第二次请求；完整读完后只剩套话的邮件仍会显示这些文字。
- 摘要中保留隐藏的 preheader（与 Gmail 等客户端一致）；若发件方把无关文字放在隐藏元素中，摘要也会显示它。

## 10. 默认首页：统一收件箱（Unified Inbox）

### 10.1 问题与根因（Windows 实测反馈，issue [#4](https://github.com/zzvw/KeeprawMail/issues/4)）

- **Gmail 同一邮件显示两次**：同步覆盖 `INBOX`、Sent、Drafts、Trash、Junk、Archive（`ImapService.GetFoldersToSyncAsync`，按 SPECIAL-USE 查找；Gmail 不标记 `\Archive`，`[Gmail]/All Mail` 不在同步列表中）。本地每个文件夹的每个 UID 是一行，主键 `Id = AccountId|Mailbox|UID`（`EmailIdentity`），同一文件夹内同一 UID 不可能写入两行（UPSERT）。而 `EmailStore.GetRecentAsync()`（`DatabaseContext.GetEmailsAsync()` → `MainViewModel.LoadLocalEmailsAsync()`）**不按文件夹过滤**，因此用户发给自己的测试邮件（`INBOX` 与 `[Gmail]/Sent Mail` 各一份，主题 / 发件人 / 时间 / 摘要相同，UID 与 Id 不同）显示两次；如果 All Mail 等其它文件夹的数据存在，也会以同样方式重复。这不是同步重复写入，也不是同一文件夹内的重复邮件。
- **归档 / 垃圾箱邮件出现在首页**：同一原因（Trash / Junk 等文件夹混在列表中），加上归档后 INBOX 记录滞留到 6 小时全量核对（第 5.1 节已解决）。
- **预览浪费**：PR3 的预览候选使用同一个不过滤的查询，会为 Trash / Sent 等不应显示的邮件生成预览。

### 10.2 规则

- 首页 = 所有已配置账号的服务器收件箱 `INBOX`（`EmailStore.DefaultListFilterSql`：`Mailbox = 'INBOX' COLLATE NOCASE`）。RFC 3501 规定 `INBOX` 不区分大小写，因此 `Inbox` / `inbox` 也匹配；条件只与字面量 `INBOX` 比较，`INBOX/子文件夹`、`[Gmail]/All Mail` 等其它名称**不做大小写折叠**；`MailboxSyncState.MailboxName` 仍按 BINARY 比较。
- 每账号最多最近 100 封（`ROW_NUMBER() OVER (PARTITION BY AccountId …)` 在 INBOX 过滤**之后**编号，其它文件夹的新邮件不会把 INBOX 挤出前 100，小账号也不会被大账号挤掉）。
- 首页列表（`GetRecentAsync`）与预览候选（`GetPreviewCandidatesAsync`）共用同一个数据源 `EmailStore.DefaultListSourceSql`，外层只追加各自条件；隐藏文件夹的邮件永远不是预览候选，不产生预览流量（只有 Pending 的隐藏邮件时不会建立预览连接）。
- 排序按**真实时刻**：`ORDER BY julianday(Date) DESC, Date DESC, Id DESC`。`Date` 以 ISO 8601 往返格式保存并保留原始偏移（Gmail INTERNALDATE 为 `+00:00`，QQ 等为 `+08:00`），按文本排序时不同偏移的邮件会错位（`21:30+08:00` 实际早于 `14:00+00:00`）；`julianday()` 把它们换算到同一时间轴。存储格式、解析与游标不变。
- 不删除、不合并任何记录：Sent / Drafts / Trash / Junk / Archive 的邮件继续同步并保存（游标、正文缓存、预览状态、FTS 都不变），将来通过文件夹导航访问。不做基于主题 / 时间 / 发件人的去重：同一文件夹内主题、发件人、时间都相同的不同 UID 是不同邮件，照常分别显示。
- 不需要数据库迁移（结构版本仍为 5）；已有数据库直接打开即按新规则显示。

### 10.3 测试

`UnifiedInboxTests`（Gmail 同一邮件在 INBOX / Sent Mail / All Mail 只显示一次且三行都保留；两账号 INBOX 各自 100 封、不被 Sent 挤出；Trash / Spam / Archive / Junk / Drafts 不进首页但行、正文缓存、FTS、游标保留；INBOX 大小写；内容相同的不同邮件不合并；列表与预览候选同一集合且隐藏文件夹不预览；混合偏移按真实时刻排序；已有数据库重新打开不丢数据），`InboxArchiveReconcileTests`（归档 / 移入垃圾箱后一次手动同步即更新首页、请求数、自发邮件只删 INBOX 行、归档与新邮件同时发生、无 CONDSTORE 服务器、对照 PR3 行为、归档后不再预览），`ArchiveReconcileMetricsTests`（实测流量）。

## 11. 本地时间显示

- **问题**：Windows 时区为北京时间时，北京时间 22:00 收到的 Gmail 邮件在列表中显示 14:00。Gmail 的 INTERNALDATE 带 `+0000`，原样以 `+00:00` 保存；`EmailMessage.DisplayDate` / `FullDateTooltip` 直接对保存的 `DateTimeOffset` 调用 `ToString("HH:mm")`，显示的是邮件自带偏移下的钟点，而不是本地时间。
- **做法**：所有用户可见的邮件时间统一经过 `Core.Formatting.MailDateFormatter`：`TimeZoneInfo.ConvertTime(value, zone)` 换算到本地时区后再格式化。默认实例 `MailDateFormatter.System` 每次读取 `TimeZoneInfo.Local`（Windows 当前系统时区，不硬编码 UTC+8），测试注入 `TimeZoneInfo` 与 `TimeProvider`。
  - 列表 `DisplayDate`：本地今天 ⇒ `HH:mm`；本地昨天 ⇒ “昨天”；2–6 天前 ⇒ 星期（按区域格式）；同一年 ⇒ `MM-dd`；更早 ⇒ `yyyy-MM-dd`。“今天 / 昨天”按**本地日历日期**判断（午夜跨日、夏令时 23 / 25 小时的日子都正确），不再按相差小时数。
  - 完整时间 `FullDateTooltip`（列表 ToolTip）、阅读器头部 `Date:`、回复 / 转发引用中的“日期:”：`yyyy-MM-dd HH:mm:ss` 本地时间。
  - `DateTimeToStringConverter`（App.xaml 中注册的资源）与列表使用同一规则。
- **切换时区**：.NET 在进程内缓存 `TimeZoneInfo.Local`；每次重新加载列表（启动、同步完成）前调用 `MailDateFormatter.RefreshSystemTimeZone()`（`TimeZoneInfo.ClearCachedData()`），因此在 Windows 中切换时区后点一次“同步”即按新时区显示。
- **不变**：数据库中的绝对时间与偏移、IMAP 日期解析（INTERNALDATE 优先、ENVELOPE Date 回退）、同步游标、存储格式；列表排序按真实时刻（第 10.2 节），与显示时区无关。
- **已知限制**：列表项的显示文本在绑定时计算，应用一直开着跨过本地午夜时，“今天 / 昨天”要到下一次刷新列表（同步）才更新。
- **测试**：`MailDateFormatterTests`（报告中的 22:00 / 14:00 问题、UTC、UTC+8、切换到美国时区 UTC-7 / UTC-8 / UTC-4、洛杉矶春秋两次夏令时切换与自定义规则、北京与洛杉矶午夜跨日、秋季回拨当天的“昨天”、星期 / 月日 / 跨年、未知日期不抛异常、排序与显示时区无关、系统时区）。

## 12. 后续事项

- 文件夹导航 / 筛选界面（访问 Sent、Trash、Archive 等已同步的数据）不在本 PR 中实现；数据已在本地保留，`DefaultListSourceSql` 之外的查询可以直接复用 `ListOrderSql`。
- FTS5 搜索界面仍未接入（`SearchAsync` 仍是 TODO），本 PR 不修改。
