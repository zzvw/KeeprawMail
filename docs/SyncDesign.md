# IMAP 增量同步、按需正文与列表摘要预览（PR #2 / PR #3 设计说明）

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
| `PreviewPartSelector` / `TruncatedTransferDecoder` / `PreviewTextExtractor` | `Core/Imap/PreviewText.cs` | PR3：预览部分选择（优先 text/plain）、截断内容的传输解码与摘要提取 |
| `IImapMailSession.FetchPreviewsAsync` | `Core/Imap/MailKitImapSession.cs` | PR3：BODYSTRUCTURE 批量 + 有界部分下载 `BODY.PEEK[section]<offset.count>` |
| `EmailStore`（预览部分） | `Core/Storage/EmailStore.Preview.cs` | PR3：预览候选（与列表同一查询）与预览结果写入 |
| `MailPreviewService` | `Core/Preview/MailPreviewService.cs` | PR3：后台预览任务（按账号合并、复用会话、可取消、退避） |

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

## 9. 列表摘要预览（PR #3）

目标：邮件在打开前就能在列表中显示最多 3 行摘要，同时保持 PR2 的核心设计——普通同步不下载完整正文和附件，完整正文只在打开时下载。
预览与打开正文是两条互不调用的流程：预览从不调用 `OnDemandBodyLoader.EnsureBodyAvailableAsync()`，打开正文也从不调用预览。

### 9.1 IMAP：有界部分下载

`IImapMailSession.FetchPreviewsAsync(mailbox, uids)`（`MailKitImapSession` 实现）对一批邮件：

1. 一条 `UID FETCH <set> (UID BODYSTRUCTURE)` 批量取得结构（不含任何正文内容）；
2. 按结构选择正文部分（9.2），对每封邮件发出 `UID FETCH <uid> (BODY.PEEK[<section>]<0.4096>)`；
3. 解码后没有有意义的文本（没有任何字母或数字）时，至多再发一次请求：同一部分续读 `<4096.4096>`，或（纯文本部分已完整读完但为空时）读同一 `multipart/alternative` 中的 html 部分 `<0.4096>`；
4. 每封邮件合计不超过 **8 KiB（8192 字节）** 传输内容、不超过 2 次部分下载请求（`PreviewFetchLimits`）；8 KiB 内仍没有文本 ⇒ `NoText`，**绝不为了摘要下载整封邮件或完整正文**；没有 BODYSTRUCTURE 时同样不下载、记为 `NoText`；
5. 文件夹以 `EXAMINE` 打开、使用 `BODY.PEEK`，不改变服务器上的 `\Seen`；实际收到的字节数（MailKit 返回的流长度）逐封统计并返回。

**MailKit 4.18.1 `GetStreamAsync` 的实测结论**（`PreviewMailKitTests.MailKit_GetStreamAsync_PartialFetch_SemanticsVerified`，以服务器收到的命令与发送的字节为准）：

- `IMailFolder.GetStreamAsync(UniqueId, string section, int offset, int count)` 发出 `UID FETCH uid (BODY.PEEK[section]<offset.count>)`，是服务器端的有界部分下载，不是下载完整正文后在客户端截断；
- `offset` / `count` 是该 MIME 部分**传输编码后内容**中的字节位置（RFC 3501 §6.4.5）；返回的流是服务器发送的原始字节，**MailKit 不做 base64 / quoted-printable / 字符集解码**；offset 超出末尾时返回空流；
- `GetStreamAsync(UniqueId, BodyPart, offset, count)` 直接使用 `BodyPart.PartSpecifier`。单部分（非 multipart）邮件的根部分 `PartSpecifier` 是空字符串，这个重载会变成 `BODY.PEEK[]<0.4096>`——整封邮件从邮件头开始的前 4 KiB（邮件头较大时一个正文字节都拿不到）。因此实现改用字符串重载，根部分使用 `TEXT`，其余使用部分编号（如 `1`、`1.1`、`1.1.1`）。
- 不规范的服务器若忽略 `<offset.count>` 发送完整内容，MailKit 会照收（协议层无法阻止）；实现只解码请求的前 count 字节，并如实统计实际收到的字节数。

### 9.2 MIME 选择与解码

- 结构选择（`PreviewPartSelector`）：优先 `text/plain`，没有时 `text/html`；递归 `multipart/alternative`（选纯文本，多个候选取最后一个）、`multipart/mixed` / `related` / `signed` 等（取第一个能提供正文的子部分，之后的部分视为附件 / 内嵌资源）；`Content-Disposition: attachment` 的文本、`message/rfc822` 附带的邮件、`text/calendar` 等其它文本类型、二进制部分一律不作为正文。
- 截断安全的传输解码（`TruncatedTransferDecoder`，不依赖任何假设内容完整的 API）：base64 只解码完整的 4 字符组（末尾不完整的组丢弃，忽略换行，支持中途填充）；quoted-printable 处理 `=XX` 与软换行，末尾不完整的 `=`、`=X`、`=\r` 丢弃，非法转义原样保留；7bit / 8bit / binary 原样；未知编码 ⇒ `NoText`。
- 截断安全的字符集解码（`BodyTextDecoder.DecodePrefix`）：声明字符集（UTF-8、GBK / GB2312 按代码页 936、GB18030 含四字节序列……）用解码器“不刷新”解码，末尾不完整的多字节字符不会变成替换字符；未声明或声明为 ISO-8859-1 时，先去掉末尾不完整序列再做严格 UTF-8 / GBK 探测（与打开正文的规则一致），避免被截断的 UTF-8 被误判为 Latin-1。
- 摘要（`SnippetBuilder.FromBody(text, isHtml, truncated)`，与打开正文共用）：HTML 去掉 `head` / `style` / `script` / `title` 与注释（包括 Outlook 条件注释），截断时从未闭合的这些块处截止、去掉末尾未闭合的标签与不完整的实体，再去标签、解码实体；去掉零宽字符、`&#847;` 等营销邮件预览填充与控制字符，压缩空白；上限从 200 提高到 **280 字符**（不拆开代理对）。结果只有纯文本，界面用 `TextBlock` 显示，从不把 HTML 绑定到 XAML，列表行中没有 WebView2。

### 9.3 预览状态与数据库（结构版本 4）

`Emails` 新增三列（`MailStoreSchema.EnsureAsync` 中按列存在检查幂等添加，同一事务中把 `user_version` 从 3 升到 4）：

| 列 | 类型 | 说明 |
|---|---|---|
| `PreviewState` | INTEGER NOT NULL DEFAULT 0 | 0 Pending、1 Ready、2 NoText、3 Failed（不再用空字符串表示状态） |
| `PreviewAttempts` | INTEGER NOT NULL DEFAULT 0 | 连续失败次数 |
| `PreviewNextRetryAt` | TEXT | Failed 的下次重试时间（UTC ISO 8601，文本比较即时间比较） |

- 回填只在从 < 4 升级时执行一次，只改 `PreviewState`（不改 `Snippet`，FTS 不受影响）：已有摘要 ⇒ Ready；正文已缓存但摘要为空 ⇒ NoText；其余 ⇒ Pending。不删除、不改写任何账号、邮件、摘要、正文缓存、凭据或同步状态。
- 预览写入（`ApplyPreviewResultsAsync`）只更新 `Snippet` 与三列预览状态：不清空 `BodyLocalPath`，不改已读 / 星标 / 主题 / 发件人 / UID，不读写 `MailboxSyncState` 的游标；只更新 Pending / Failed 且未缓存正文的行；写入前确认本次 `EXAMINE` 的 `UIDVALIDITY` 与已保存的一致（防止文件夹重建后把摘要写到另一封邮件上）。FTS5 由 `Emails_au`（`UPDATE OF Snippet`）触发器同步。
- 同步：新邮件以 Pending 插入；元数据 UPSERT 不触碰预览列；旧版映射无法确认而清除摘要时，预览状态一并重置为 Pending。
- 打开正文（`UpdateBodyAsync`）：完整正文生成的摘要覆盖预览摘要并设为 Ready；完整正文没有文本时保留已有预览摘要（没有时为 NoText）。
- 状态机：Ready 与 NoText 永不再取；Failed 按 5 分钟 → 30 分钟 → 2 小时 → 12 小时退避；取消时未处理的邮件保持原状态（Pending），下次继续。

### 9.4 后台预览任务（`MailPreviewService`）

- 候选与列表使用同一查询：每个账号按 `Date` 倒序的前 100 封（`EmailStore.DefaultPerAccountLimit`，`PARTITION BY AccountId`），只取 Pending 或已到重试时间的 Failed、`ImapUid` 已知、未缓存正文的邮件；不是整个邮箱历史。**没有候选时不建立任何连接。**
- 触发：每个账号的元数据同步完成后（同步报告不是 Failed 时）安排一次，不等待预览，因此不阻塞、不影响同步完成与同步结果；应用启动后第一次加载列表时，为列表中仍是 Pending / Failed 的邮件所在账号安排一次。
- 每个账号一个会话（`ImapService.OpenSessionAsync`：该账号自己的代理与 TokenManager，与同步、打开正文同一连接路径），按文件夹分组复用，不按邮件重连或重新认证；文件夹按其中最新邮件排序、组内按时间倒序，新到的邮件优先；每 20 封一批（一条 BODYSTRUCTURE + 逐封部分下载），每批结果立即写库并通知界面。
- 同一账号重复请求合并：运行中再次请求只会在本轮结束后再跑一轮（覆盖刚同步到的新邮件）；不同账号可并行。
- 失败隔离：单封邮件的 `NO` 只把该封记为 Failed；断线等连接级错误时已得到的结果照常写入，其余记为 Failed（退避）；会话建立失败（离线、需要重新授权）时候选记为 Failed。预览任何失败都不会让成功的同步变成失败，也不推进或修改 UID 游标。
- 界面：`PreviewsUpdated` 事件经 UI 线程的 `SynchronizationContext` 更新 `EmailMessage.Snippet` / `PreviewState`，列表立即刷新、无需打开邮件；列表重载与预览写入交错时，用最近收到的预览结果补齐新列表项。

### 9.5 列表显示

`MailPage.xaml` 摘要 `TextBlock` 绑定 `EmailMessage.SnippetDisplay`（`MaxLines="3"`、`TextWrapping="Wrap"`、`TextTrimming="CharacterEllipsis"`），三栏布局、星标、时间、发件人、主题不变：

| 状态 | 显示 |
|---|---|
| Ready（或打开正文后已有摘要） | 摘要文本 |
| Pending | 正在加载预览…（淡色） |
| NoText | 无文本预览（淡色） |
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
