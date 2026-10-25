# Windows 手工验收清单（PR #1、PR #2、PR #3、统一收件箱与本地时间修复）

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

## 11. 第四轮修复（删除账号 / Gmail 同步诊断 / 正文空白）

诊断日志：`%LOCALAPPDATA%\KeeprawMail\logs\diagnostics.log`（已脱敏，不含 Token、密码、授权码、正文）。

### A. 删除账号
- [ ] 删除一个**已有本地邮件**的 QQ 账号：不再出现 `SQLite Error 19`；提示删除了 N 封本地邮件；主界面邮件列表中该账号邮件消失，其它账号邮件保留。
- [ ] 删除一个刚添加、尚未同步的空账号：成功。
- [ ] 删除后重启：被删账号不再出现，其它账号可正常同步；QQ 网页版中服务器邮件仍在（未远程删除）。
- [ ] `%LOCALAPPDATA%\KeeprawMail\Emails` 下该账号的正文缓存已删除，其它账号的不受影响。
- [ ] 编辑某账号的代理后，该账号的本地邮件仍在。

### B. Gmail 同步
- [ ] 重新授权成功后出现“授权已更新……是否立即同步”对话框；点击“立即同步”后给出明确的成功 / 失败结果（失败时包含阶段）。
- [ ] 代理软件关闭（127.0.0.1:1080 未监听）时同步：提示“无法连接到 SOCKS5 代理 127.0.0.1:1080：端口未监听”，并弹出失败详情对话框，状态栏不被“已加载 N 封”覆盖。
- [ ] 把 HTTP 代理端口填成 SOCKS5（或反之）：提示代理类型可能不匹配。
- [ ] 代理正常时 Gmail 同步成功，邮件出现在列表中（即使 QQ 账号有大量新邮件，Gmail 邮件仍可见）。
- [ ] 若仍失败，把对话框中的“失败阶段 + 说明”和 diagnostics.log 末尾几行发回。

### C. 邮件正文
- [ ] 点击 QQ / Gmail 邮件（含 `[Gmail]/Sent Mail` 等带斜杠的文件夹）：正文正常显示，HTML 排版保留。
- [ ] 打开主题含 `<`、`&`、中文、超长主题的邮件：标题正确显示，无错位。
- [ ] 打开多封邮件（多个标签）均正常；关闭后再次打开同一封正常。
- [ ] 正文下载失败的邮件：显示“无法显示邮件正文”及原因，而不是空白。
- [ ] 邮件中的链接点击后在系统浏览器打开，阅读器本身不跳转；含脚本的营销邮件不执行脚本。

## 12. PR #2：IMAP 增量同步与按需正文

设计说明见 `docs/SyncDesign.md`。请先备份 `%LOCALAPPDATA%\KeeprawMail\keepraw.db` 与 `Emails` 目录。

### A. 升级与首次同步
- [ ] 用 PR1 版本的数据库首次启动新版：账号、已有邮件、已缓存的正文都还在；已缓存正文的邮件打开时不联网（可断开代理验证）。
- [ ] 首次点击同步：状态栏显示“首次同步邮件头 N/M”的进度；邮箱较大时可分批完成；期间界面可操作。
- [ ] 首次同步后 `%LOCALAPPDATA%\KeeprawMail\Emails` 中**没有**新增大量正文文件（只有之前已缓存的）。
- [ ] 新邮件在列表中显示主题、发件人、时间；摘要先显示“正在加载预览…”，随后由后台预览填充（PR3，见第 13 节）。

### B. 增量同步
- [ ] 再次点击同步（无新邮件）：很快完成，状态栏为“同步完成……新增 0 封邮件”。diagnostics.log 中该文件夹为 `baseline=False new=0`。
- [ ] 从另一设备给自己发一封邮件后同步：只新增这一封（`new=1`）。
- [ ] 在网页版把一封邮件标为已读 / 加星标后同步：Gmail（CONDSTORE）立即一致；QQ 等最近 200 封内的邮件立即一致，更早的邮件在 6 小时内的下一次核对后一致。
- [ ] 在网页版删除一封邮件：Gmail / QQ 在 6 小时内的下一次核对后本地消失（diagnostics.log 中 `reconcile=True removed=1`），服务器其它邮件不受影响。
- [ ] 连续快速点击“同步”多次：不会出现重复邮件，diagnostics.log 中每个账号只有一组 `start`。
- [ ] 同步过程中断开代理：给出失败阶段；恢复后再次同步能从中断处继续，不出现重复。

### C. 按需正文
- [ ] 打开一封从未打开过的邮件：先显示“正在加载正文…”，随后正文正常显示；列表摘要随之更新。
- [ ] 再次打开同一封：立即显示，不联网（可断开代理验证）。
- [ ] 打开带大附件（如 20 MB）的邮件：正文很快显示，任务管理器 / 代理软件流量没有出现附件大小的下载。
- [ ] 只有附件、没有正文的邮件：显示“没有可显示的文本正文，只包含附件：…”。
- [ ] 中文邮件（GB2312 / GBK / UTF-8 / 工行等声明 ISO-8859-1 的银行邮件）显示正常，无乱码。
- [ ] 断开代理后打开未缓存的邮件：标签页显示失败阶段与原因；恢复代理后点击“重试”成功显示（确实重新下载）。
- [ ] 手动删除 `Emails` 目录中某封邮件的缓存文件后再打开：自动重新下载。
- [ ] 两个账号都选中过时，打开 Gmail 邮件只连接 Gmail（diagnostics.log 中 `connect` 行的账号正确，经该账号的代理）。
- [ ] 打开邮件后迅速关闭标签页：不报错，再次打开正常。
- [ ] 回复 / 转发一封尚未打开过的邮件：引用中包含正文（会先按需下载）。

### D. UIDVALIDITY
- [ ] （可选，需测试邮箱）在服务器上删除并重建一个文件夹后同步：该文件夹本地记录重建（diagnostics.log 中 `uidvalidityReset=True`），其它文件夹与账号不受影响。

### E. 回归
- [ ] PR1 第 11 节的删除账号、Gmail 重新授权、代理诊断、正文错误页全部仍然正常；删除账号后 `MailboxSyncState` 中该账号的行也被删除。

## 13. PR #3：邮件列表摘要预览（有界 IMAP 部分下载）

设计说明见 `docs/SyncDesign.md` 第 9 节。请先备份 `%LOCALAPPDATA%\KeeprawMail\keepraw.db` 与 `Emails` 目录。

### A. 升级（结构版本 3 → 4）
- [ ] 用 PR2 版本的数据库首次启动新版：账号、邮件、已有摘要、已缓存的正文都还在；无需重新登录；已有摘要的邮件直接显示摘要，已缓存正文的邮件打开时不联网（可断开代理验证）。
- [ ] 重启一次：没有重复迁移或报错（数据库 `PRAGMA user_version` 为 5，见第 14 节）。

### B. 摘要显示
- [ ] 首次同步一个新账号：同步结束、状态栏显示同步结论后，列表中的摘要从“正在加载预览…”逐批变为正文开头的文字，**不需要点开邮件**；同步本身没有变慢（摘要在同步完成之后才开始加载）。
- [ ] 摘要最多 3 行，超出显示省略号；三栏布局、星标、时间、发件人、主题与之前一致；列表滚动流畅（列表行中没有 WebView2）。
- [ ] 中文邮件（GB2312 / GBK / GB18030 / UTF-8，base64 / quoted-printable，声明 ISO-8859-1 的银行邮件）摘要无乱码，结尾没有半个字或 `=E4` 之类的残留。
- [ ] 营销 HTML 邮件：摘要是正文文字，不出现 CSS、脚本、`<!-- -->`、`&nbsp;` 等内容，也不出现大段空白占位符。
- [ ] 只有附件的邮件显示“无文本预览”；摘要显示的是正文而不是附件中的文本、也不是转发附带的邮件（message/rfc822）的内容。
- [ ] 有正文但预览在 8 KiB 内取不到文字的邮件显示“打开邮件查看内容”（不是“无文本预览”）；打开后列表摘要更新为正文开头。
- [ ] 断开代理后同步 / 重启：摘要未加载的邮件显示“预览暂不可用”；恢复代理后数分钟内再次同步，摘要恢复（退避重试）。

### C. 流量与请求
- [ ] 首次同步后 `%LOCALAPPDATA%\KeeprawMail\Emails` 中**没有**新增正文文件（预览不写正文缓存）。
- [ ] 打开 `%LOCALAPPDATA%\KeeprawMail\logs\diagnostics.log`：每轮预览一行 `[preview] preview <账号 ID>: candidates=… ready=… noText=… failed=… structure=… partial=… bytes=…`；首次同步后 candidates 不超过 100（列表每账号 100 封），bytes 约为 partial × 4096 以内。
- [ ] 无新邮件再次同步：diagnostics.log 中没有新的 `preview` 行（没有候选，不建立连接）。
- [ ] 收到几封新邮件后同步：`preview` 行的 candidates 等于新邮件数。
- [ ] 带大附件（如 20 MB）的邮件：代理软件 / 任务管理器中没有出现附件大小的流量；摘要正常显示。

### D. 打开正文（PR2 行为不变）
- [ ] 点开未缓存的邮件：仍按需下载正文（只有正文部分，不含附件），在 WebView2 中显示；列表摘要更新为正文生成的摘要。
- [ ] 再次打开：立即显示，不联网。
- [ ] 打开邮件后其已读 / 星标、主题、发件人不被预览改动；预览也不会把邮件标为已读（网页版中仍为未读）。

### E. 多账号
- [ ] 两个账号（如 Gmail + QQ）同时同步：各自的摘要只出现在各自的邮件上；diagnostics.log 中预览连接使用各自账号的代理。
- [ ] 删除一个账号后：另一个账号的摘要与预览不受影响。

## 14. PR #3 修复：真实邮件预览误判与旧 NoText 的一次性恢复（结构版本 4 → 5）

设计说明见 `docs/SyncDesign.md` 9.2、9.3、9.7–9.9。请先**完全退出**旧版本，备份 `%LOCALAPPDATA%\KeeprawMail\keepraw.db` 与 `Emails` 目录。

### A. 升级前记录（用 SQLite 工具打开备份的 keepraw.db，只读）
- [ ] `PRAGMA user_version;` → 4（PR3 之前的构建）。
- [ ] 记录旧 NoText 的数量：
      `SELECT COUNT(*) FROM Emails WHERE PreviewState = 2 AND IFNULL(BodyLocalPath,'') = '';`（没有正文缓存，将重新预览一次）
      `SELECT COUNT(*) FROM Emails WHERE PreviewState = 2 AND IFNULL(BodyLocalPath,'') <> '';`（有正文缓存，将从本地缓存重算，零网络）
      `SELECT COUNT(*) FROM Emails WHERE PreviewState = 3;`（旧 Failed，将立即重试一次）

### B. 首次启动新版本
- [ ] 启动后（不必点同步）：American Airlines（如 “Your refund is complete”）、Tiptrans 等之前显示“无文本预览”的邮件，在后台预览完成后显示正文开头（AA 类通常以隐藏的预览文字开头）。
- [ ] 打开过的邮件（有正文缓存）原来显示“无文本预览”的：启动后很快显示摘要；此过程**不联网**（可在启动前断开代理验证：这类邮件仍会恢复，其它邮件显示“预览暂不可用”）。
- [ ] 只有附件的邮件仍显示“无文本预览”。
- [ ] HSBC、Instapaper 等原来就有摘要的邮件摘要不变；已读 / 星标、正文缓存（打开不联网）、搜索都正常。
- [ ] 数据库：`PRAGMA user_version;` → 5；
      `SELECT PreviewState, PreviewAlgo, COUNT(*) FROM Emails GROUP BY 1, 2;` → 预览完成后，列表中的邮件 `PreviewAlgo = 1`；不应再有 `PreviewState = 2 AND PreviewAlgo = 0 AND IFNULL(BodyLocalPath,'') <> ''` 的行。

### C. 只恢复一次
- [ ] 再次点击同步（无新邮件）：`diagnostics.log` 中没有新的 `[preview] preview <账号>: candidates=…` 行（没有候选，不建立预览连接），`cacheRecomputed` 不再出现。
- [ ] 完全退出并重新启动：不会再次把 NoText 重置为“正在加载预览…”；`PRAGMA user_version` 仍为 5。

### D. 诊断（把结果发回时只需要这些，不含任何邮件内容）
- [ ] `diagnostics.log` 中查找 `preview-diag`：每行形如
      `preview-diag state=Unavailable folder=INBOX r=beyond_budget t=text/plain,text/html q=2 b=8192`。
      确认其中没有主题、地址、正文或 Token。
- [ ] 仍显示“无文本预览”的邮件：`SELECT Mailbox, PreviewDiag FROM Emails WHERE PreviewState = 2 LIMIT 20;` —— 应为 `r=no_text_part`（只有附件）或 `r=empty_text`。若有正文却是 NoText，请发回该行的 `PreviewDiag`。
- [ ] 显示“打开邮件查看内容”的邮件：`SELECT Mailbox, PreviewDiag FROM Emails WHERE PreviewState = 4 LIMIT 20;` —— 预期 `r=beyond_budget`（正文前的 CSS 等超过 8 KiB）、`r=unknown_cte` 或 `r=no_structure`。
- [ ] 显示“预览暂不可用”的 GitHub 通知：`SELECT Mailbox, PreviewDiag, PreviewAttempts FROM Emails WHERE PreviewState = 3 LIMIT 20;`
      —— `r=uid_missing` 表示该邮件已在服务器上被归档 / 删除 / 移走（统一收件箱修复之后，本地记录在下一次同步的移出检测中删除，见第 15 节）；
      `r=server_no` / `r=server_no_structure` 表示 Gmail 拒绝了该邮件的请求（带 `x=ImapCommandException`）；`r=connection` 表示断线。请把 `r=` 与 `x=` 发回。
- [ ] （PR3 当时的已知问题，已由第 15 节的修复解决：归档后的下一次同步即从首页消失，不再出现 `uid_missing`。）

### E. 流量
- [ ] `diagnostics.log` 的汇总行 `maxPerMessage=` 不超过 8192；`partial` 不超过 candidates × 2。
- [ ] 带大附件的邮件：没有附件大小的流量。

## 15. 统一收件箱、Gmail 重复 / 归档 / 垃圾箱与本地时间

设计说明见 `docs/SyncDesign.md` 第 5.1、10、11 节。本次修复**没有数据库迁移**（`PRAGMA user_version` 仍为 5）。请先完全退出旧版本，备份 `%LOCALAPPDATA%\KeeprawMail\keepraw.db` 与 `Emails` 目录。

### A. 升级前记录（SQLite 工具只读打开备份）
- [ ] `SELECT Mailbox, COUNT(*) FROM Emails GROUP BY Mailbox;` —— 记下每个文件夹的行数（升级后应完全相同，除了第 C 节中被归档 / 删除的邮件）。
- [ ] `SELECT COUNT(*) FROM Emails WHERE IFNULL(BodyLocalPath,'') <> '';` —— 记下正文缓存数。

### B. Gmail 重复邮件
- [ ] 启动新版本（不必同步）：之前显示两次的 Gmail 测试邮件（发给自己的邮件）在首页只显示一次；悬停 / 打开确认是收件箱中的那一份。
- [ ] 数据库：`SELECT Mailbox, ImapUid FROM Emails WHERE Subject = '<那封邮件的主题>';` —— 仍有 `INBOX` 与 `[Gmail]/Sent Mail` 各一行（数据没有被删除或合并）。
- [ ] 给自己再发一封新邮件，点击同步：首页只出现一次。
- [ ] 同一个发件人连续发两封主题、时间都相同的邮件（例如两封相同的通知）：首页显示两封，不会被合并。
- [ ] 首页只显示各账号的收件箱：Gmail 的已发送、草稿、垃圾邮件（Spam）、垃圾箱中的邮件不出现；QQ / 163 等账号的“已删除”“垃圾邮件”“已发送”也不出现。
- [ ] 多账号：两个账号都有收件箱邮件时，两个账号的邮件都在首页，按时间交错排列；邮件很多的账号不会把另一个账号挤出列表（每账号最多 100 封）。
- [ ] `SELECT Mailbox, COUNT(*) FROM Emails GROUP BY Mailbox;` —— 与第 A 节相同（其它文件夹的数据仍在本地）。

### C. Gmail 归档与垃圾箱
- [ ] 在 Gmail 网页端**归档**首页中的一封邮件，回到 KeeprawMail 点击“同步”一次：该邮件从首页消失（不再需要等 6 小时）。
- [ ] `diagnostics.log` 中该账号 INBOX 一行为 `removalCheck=True removed=1`，并有 `removal check INBOX: exists=… local=… removed=1`；同一次同步中没有正文下载（无 `[reader]` 行）。
- [ ] 再点一次同步：`removalCheck=False`（计数一致时不再发送 `UID SEARCH ALL`）。
- [ ] 在 Gmail 网页端把首页中的一封邮件**移入垃圾箱**，同步一次：从首页消失；`SELECT Mailbox FROM Emails WHERE Subject = '<主题>';` 出现 `[Gmail]/Trash` 的一行（数据保留，首页不显示）。
- [ ] 归档一封**发给自己**且两份都打开过的邮件：同步后首页不再显示；`[Gmail]/Sent Mail` 中的那一行仍在、`BodyLocalPath` 不变。
- [ ] 在 Gmail 网页端检查：KeeprawMail 同步没有改变任何邮件的标签、已读状态，也没有把邮件移回收件箱（同步只读）。
- [ ] 归档后不会再出现“预览暂不可用”（`r=uid_missing`）的残留项。
- [ ] 流量：`diagnostics.log` 中 `[preview] preview <账号>: candidates=…` 只统计首页（INBOX）邮件；垃圾箱 / 已发送中的新邮件不会触发预览。

### D. 本地时间
- [ ] Windows 时区为“(UTC+08:00) 北京，重庆，香港特别行政区，乌鲁木齐”：从另一个邮箱给 Gmail 发一封邮件，记下手机 / 网页上的收件时间（如 22:00）；首页列表显示 22:00（不是 14:00）；悬停时间显示 `yyyy-MM-dd 22:00:xx`；打开邮件，阅读器头部 `Date:` 同样是 22:00。
- [ ] QQ / 163 等账号的邮件时间同样是北京时间；多个账号的邮件按真实先后交错排列（不会因为 Gmail 以 UTC 保存而整体排到后面）。
- [ ] 回复 / 转发：引用头部的“日期:”是本地时间。
- [ ] 跨日：北京时间 0 点之后查看前一天 23 点多收到的邮件，显示“昨天”；当天 0 点之后的邮件显示时间。2–6 天前显示星期，更早显示 `MM-dd`，往年显示 `yyyy-MM-dd`。
- [ ] 切换到美国时区：在 Windows 设置 → 时间和语言 → 日期和时间中关闭“自动设置时区”，选择“(UTC-08:00) 太平洋时间(美国和加拿大)”，回到 KeeprawMail 点击“同步”（或重启应用）：同一封邮件显示为太平洋时间（10 月为夏令时 UTC-7，北京 22:00 ⇒ 07:00；11 月 1 日之后为 UTC-8 ⇒ 06:00）；“今天 / 昨天”按太平洋时间的日期判断；列表顺序不变。
- [ ] 再切换到“(UTC-07:00) 亚利桑那”：同一封邮件显示 07:00（全年无夏令时）。
- [ ] 切换回北京时间并同步：恢复 22:00。数据库中该邮件的 `Date` 字段在整个过程中没有变化（`SELECT Date FROM Emails WHERE Subject = '<主题>';`，Gmail 为 `…T14:00:00.0000000+00:00`）。

### E. 回归
- [ ] PR3 摘要预览：首页邮件仍显示最多 3 行摘要；“无文本预览”“打开邮件查看内容”“预览暂不可用”的含义不变；带大附件的邮件没有附件流量（`maxPerMessage=` ≤ 8192）。
- [ ] 打开未缓存的邮件按需下载正文；再次打开不联网；已读 / 星标正常。
- [ ] 账号、凭据、正文缓存数与第 A 节一致；完全退出后重启，`PRAGMA user_version` 仍为 5。

