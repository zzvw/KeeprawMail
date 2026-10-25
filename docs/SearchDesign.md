# 本地邮件搜索（PR #6）

KeeprawMail 的搜索**只在本机 SQLite 数据库中查找已经同步下来的邮件**：不连接邮件服务器、不发送 IMAP SEARCH、不为了搜索下载正文或附件。

## 1. 搜索范围

| 可以搜到 | 搜不到（当前版本的已知限制） |
| --- | --- |
| 主题 `Subject` | 尚未下载的完整正文 |
| 发件人姓名 `SenderName` | 已下载正文中没有进入摘要的部分（摘要约 200 字） |
| 发件人邮箱 `SenderAddress` | 附件名与附件内容 |
| 邮件摘要 `Snippet`：PR3 后台预览生成的摘要，以及打开邮件后由完整正文生成的摘要 | 收件人、抄送（数据库未保存） |
| 所有账号、所有已同步的文件夹（INBOX、已发送、垃圾箱、归档等） | 从未同步到本机的邮件 |

> 摘要可搜索 ≠ 完整正文可搜索。把已缓存的完整正文加入索引需要新的 FTS 表或列（数据库结构变更 + 一次性重建索引），留到独立 PR。

## 2. 搜索语法

| 输入 | 含义 |
| --- | --- |
| `invoice` | 在主题、发件人姓名、发件人邮箱、摘要中搜索；英文按**词前缀**匹配（`invoic` 也能找到 invoice / invoices），不区分大小写 |
| `invoice payment` | 空格分隔的多个词必须**全部**匹配（AND），可以分别出现在不同字段 |
| `"flight confirmation"` | 引号短语：词序完全一致才匹配（不做前缀扩展） |
| `subject:invoice` / `subject:"月度 账单"` | 只搜主题 |
| `from:amazon` / `from:noreply@amazon.com` / `from:京东` | 搜发件人姓名与发件人邮箱 |
| `发票` / `电子发票 已开` | 中文按**子串**匹配（见第 3 节） |

- 字段名只有 `subject:` 和 `from:`（不区分大小写，白名单）。其它 `xxx:yyy`（如 `to:bob`、`http://…`）按普通文字搜索。
- 没有 OR / NOT / 括号等运算符：用户输入里的 `AND`、`OR`、`NOT`、`*`、`^`、`(`、`)`、`:`、`%`、`_`、`'` 都只是普通文字。
- 缺少结束引号时，引号后的全部内容当作短语。控制字符（含 NUL）当作空格。
- 只有标点符号的词被忽略；整个输入都没有文字 / 数字时提示“请输入至少一个文字、字母或数字再搜索”。
- 输入超过 256 个字符或超过 16 个词时给出提示，不会悄悄截断。

## 3. 中文搜索

现有 `Emails_FTS` 使用 SQLite FTS5 默认的 **unicode61** 分词器，它按空格和标点切词，**连续的中文被当作一个词**：
“您的电子发票已开具”整体是一个 token，FTS5 MATCH `发票` 找不到它（测试 `Chinese_SubstringOfTitle_SnippetSenderAndFieldFilters` 中实际验证了这一点）。

本 PR 采用不改数据库结构的最小方案：

- **不含中日韩字符的词**（英文、数字、邮箱地址）：FTS5 MATCH，使用现有外部内容表和触发器。
- **含中日韩字符的词**（汉字、假名、谚文，包括“iPhone订单”这类中英紧连的词）：对同样四列做子串匹配
  `IFNULL(col,'') LIKE $p ESCAPE '\'`，`$p` 是参数，`\`、`%`、`_` 已转义。
- 同一个查询里两类词可以混用（`发票 invoice`）：FTS 条件先缩小范围，子串条件再过滤。

为什么不用 trigram 分词器：需要新建 FTS 表（结构版本 5 → 6）并对全部邮件重建一次索引；而且 trigram 对最常见的**两个汉字**的词（发票、订单、快递）无法用索引匹配，仍然要回退到扫描。
四个可搜索列都是短文本（摘要约 200 字），实测 2 万封邮件的中文子串搜索约 30 ms（见第 6 节），因此本 PR 不引入结构变更。

已知限制：英文单词与中文**无空格紧连**时（如主题“购买iPhone成功”），只搜英文 `iphone` 匹配不到（unicode61 把整段当作一个词）；搜索 `iPhone成功` 或中文部分可以找到。

## 4. 查询实现（`KeeprawMail.Core`）

- `Search/MailSearchQuery.cs`：解析与转义。每个 FTS 词生成为 FTS5 字符串 `"…"`（内部双引号写成 `""`），普通词加 `*` 做前缀；列过滤只使用白名单列名 `Subject`、`{SenderName SenderAddress}`。
- `Storage/EmailStore.Search.cs`：`SearchAsync(query, limit, offset, ct)`，**一条 SELECT 直接返回邮件记录**（无“先查 ID 再逐封读取”的 N+1）：

```sql
SELECT <列> FROM Emails
WHERE rowid IN (SELECT rowid FROM Emails_FTS WHERE Emails_FTS MATCH $Fts)
  AND (IFNULL(Subject,'') LIKE $L0 ESCAPE '\' OR …)
ORDER BY julianday(Date) DESC, Date DESC, Id DESC   -- 与首页相同的真实时间排序
LIMIT $Limit OFFSET $Offset
```

  另有一条相同 WHERE 的 `COUNT(*)` 返回总数。所有用户输入都通过参数绑定；SQL 文本中只有固定片段、白名单列名和参数名。
  SQLite 异常被转换为 `MailSearchException("搜索条件无法执行，请修改搜索内容后重试。")`，不向用户显示原始错误。
- `Id` 是主键、FTS 条件用 `rowid IN (…)`，同一封邮件不会重复返回；身份始终是 `(AccountId, Mailbox, UID)`，不按主题 / 发件人 / 日期推断或合并。
- `Search/LatestOnlyRunner.cs`：列表加载协调器。收件箱加载、搜索、加载更多都经过它；新请求取消并作废旧请求，旧结果晚到也会被丢弃。

## 5. 界面行为（`KeeprawMail`）

- 搜索框按 Enter 或点击放大镜才搜索（输入过程中不搜索）；清空搜索框（包括 × 按钮）或点击“清除搜索”恢复**统一收件箱**（仅 INBOX、每账号最近 100 封，PR5 规则不变）。
- 搜索结果不受首页 100 封限制；每次加载 200 封，列表上方显示“共 N 封，已显示最新的 M 封”，底部“加载更多结果”每次追加 200 封（按 Id 去重）。
- 每个搜索结果显示“账号邮箱 · 文件夹”（INBOX 显示为“收件箱”，其它文件夹显示服务器名称），区分跨账号 / 跨文件夹的邮件。
- 无匹配时显示空状态和搜索范围说明；输入无法搜索时显示提示并清空列表，不保留旧结果；搜索失败显示失败提示，不显示假成功。
- 查询在后台线程执行（`Task.Run`），界面保持响应；连续快速搜索时只采用最后一次的结果。
- 同步完成后：如果正在显示搜索结果，**保留关键词并重新执行本地搜索**（保留已加载的数量），不会跳回收件箱；后台预览更新按 Id 同时作用于搜索结果。
- 搜索结果像普通邮件一样打开：按邮件自己的 `AccountId / Mailbox / ImapUid` 读取或按需下载正文；已读、星标、标签页功能不变（星标改为按邮件所属账号同步到服务器）。
- `Ctrl+E` 聚焦搜索框。限制：焦点在邮件正文（WebView2）内部时，按键由 WebView2 处理，Ctrl+E 不生效，先点一下邮件列表即可。

## 6. 数据库与性能

- **没有数据库结构变更**：`PRAGMA user_version` 仍为 5；不清空数据库、不重建本地缓存、不触发全量同步；FTS 表与三个触发器不变。
- 新增、摘要更新（预览 / 打开正文）、归档、服务器删除、删除账号都通过现有触发器维护索引（集成测试逐一验证并执行 FTS5 `integrity-check`）。
- 实测（Linux CI 的数字由 `search-performance-metrics.txt` 打印）：2 万封邮件，英文 FTS 查询 0–9 ms，中文子串 9–33 ms，均含 COUNT 与 200 封结果读取。
- 网络：搜索的 IMAP 请求数 = 0、下载字节 = 0（`SyncedMail_ImmediatelySearchable_AndSearchCausesNoNetwork` 用可计数的假 IMAP 服务器验证）。
