using KeeprawMail.Models;
using KeeprawMail.Core.Networking;
using KeeprawMail.Core.Sync;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace KeeprawMail.Services
{
    /// <summary>
    /// IMAP 服务
    /// 负责与邮件服务器通信，拉取邮件头和正文
    /// </summary>
    public class ImapService : IDisposable
    {
        private readonly ImapClient _client;
        private string _accountId = string.Empty;

        public ImapService()
        {
            _client = new ImapClient();
        }

        public ImapService(AccountProfile account) : this()
        {
            // 可以在这里初始化，或者调用 ConnectAsync
            // 目前 ConnectAsync 会处理连接，所以这里不需要做太多
        }

        /// <summary>
        /// 连接到 IMAP 服务器并认证。
        /// 代理由 ProxyResolver 统一解析（全局 / 继承 / 直连 / 覆盖），
        /// OAuth Token 由 TokenManager 统一提供与刷新，本类不再自行刷新。
        /// </summary>
        /// <param name="account">账号配置信息</param>
        public async Task ConnectAsync(AccountProfile account, CancellationToken cancellationToken = default)
        {
            if (_client.IsConnected)
                await _client.DisconnectAsync(true, cancellationToken);

            _accountId = account.Id;

            // 1. 解析代理（全局 / 继承 / 直连 / 覆盖）
            ProxyEndpoint proxy;
            try
            {
                proxy = await AppServices.ResolveProxyAsync(account);
            }
            catch (Exception ex)
            {
                throw new MailSyncException(SyncStage.ResolveProxy, $"{account.Email} 的代理配置无效：{ex.Message}", ex);
            }
            NetworkClientFactory.ConfigureMailClient(_client, proxy);
            _client.Timeout = 30000;
            DiagnosticLog.Write("imap", $"{account.Email} connect {account.ImapServer}:{account.ImapPort} via {proxy.Kind} {proxy.Host}:{proxy.Port}");

            // 2. TCP（经代理）+ TLS：993 隐式 TLS；其它端口强制 STARTTLS
            var sslOptions = account.ImapPort == 993
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;
            await MailConnectionDiagnostics.ConnectAsync(_client, account.ImapServer, account.ImapPort, sslOptions, proxy, cancellationToken);
            DiagnosticLog.Write("imap", $"{account.Email} connected, tls={_client.IsSecure}");

            // 3. 认证（OAuth XOAUTH2 或密码）
            await MailConnectionDiagnostics.AuthenticateAsync(
                () => AppServices.AuthenticateAsync(_client, account, proxy, cancellationToken),
                account.Email, account.IsOAuth, cancellationToken);
            DiagnosticLog.Write("imap", $"{account.Email} authenticated ({(account.IsOAuth ? "XOAUTH2" : "password")})");
        }

        /// <summary>
        /// 获取需要同步的常用邮件文件夹。
        /// </summary>
        public Task<List<string>> GetFoldersToSyncAsync()
        {
            var folders = new List<string>();
            if (_client.Inbox == null)
                throw new MailSyncException(SyncStage.ListFolders, "服务器没有返回收件箱（INBOX）。");

            AddFolderIfFound(folders, _client.Inbox);
            AddSpecialFolderIfFound(folders, SpecialFolder.Sent);
            AddSpecialFolderIfFound(folders, SpecialFolder.Drafts);
            AddSpecialFolderIfFound(folders, SpecialFolder.Trash);
            AddSpecialFolderIfFound(folders, SpecialFolder.Junk);
            AddSpecialFolderIfFound(folders, SpecialFolder.Archive);

            return Task.FromResult(folders);
        }

        /// <summary>
        /// 把找到的文件夹加入同步列表，并避免重复。
        /// </summary>
        private static void AddFolderIfFound(List<string> folders, IMailFolder? folder)
        {
            if (folder == null)
            {
                return;
            }

            if (!folders.Any(x => x.Equals(folder.FullName, StringComparison.OrdinalIgnoreCase)))
            {
                folders.Add(folder.FullName);
            }
        }

        /// <summary>
        /// 尝试读取服务商标记过的特殊文件夹。
        /// </summary>
        private void AddSpecialFolderIfFound(List<string> folders, SpecialFolder specialFolder)
        {
            try
            {
                AddFolderIfFound(folders, _client.GetFolder(specialFolder));
            }
            catch (FolderNotFoundException)
            {
                System.Diagnostics.Debug.WriteLine($"[ImapService] Special folder not found: {specialFolder}");
            }
            catch (NotSupportedException)
            {
                System.Diagnostics.Debug.WriteLine($"[ImapService] Special folder not supported: {specialFolder}");
            }
        }

        /// <summary>
        /// 同步指定文件夹的邮件头
        /// </summary>
        /// <param name="folderName">文件夹名称，默认为 Inbox</param>
        /// <param name="limit">拉取数量限制，默认最近 50 封</param>
        /// <returns>邮件列表</returns>
        public async Task<List<EmailMessage>> SyncHeadersAsync(string folderName = "Inbox", int limit = 50)
        {
            var results = new List<EmailMessage>();

            // 获取文件夹
            var folder = await ResolveFolderAsync(folderName);
            await folder.OpenAsync(FolderAccess.ReadOnly);

            // 计算拉取范围：从最新的开始
            int total = folder.Count;
            if (total == 0)
            {
                return results;
            }

            int start = Math.Max(0, total - limit);

            // 批量拉取摘要信息 (Envelope + BodyStructure + Flags)
            // SummaryItems.Envelope 包含：主题、发件人、日期、MessageId
            // SummaryItems.BodyStructure 用于后续判断正文结构
            var items = await folder.FetchAsync(start, -1, MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate | MessageSummaryItems.UniqueId | MessageSummaryItems.Flags);

            // 倒序遍历 (最新的在最前)
            foreach (var item in items.Reverse())
            {
                var email = new EmailMessage
                {
                    // 使用 UniqueId 作为本地唯一标识，或者结合 MessageId
                    Id = BuildLocalEmailId(folder.FullName, item.UniqueId),
                    Subject = item.Envelope.Subject ?? "(No Subject)",
                    Date = item.InternalDate ?? DateTimeOffset.Now,
                    SenderName = item.Envelope.From.Mailboxes.FirstOrDefault()?.Name ?? "",
                    SenderAddress = item.Envelope.From.Mailboxes.FirstOrDefault()?.Address ?? "",
                    Snippet = "(Loading snippet...)", // 摘要通常需要下载部分正文，这里先占位
                    IsRead = item.Flags.HasValue && item.Flags.Value.HasFlag(MessageFlags.Seen),
                    IsStarred = item.Flags.HasValue && item.Flags.Value.HasFlag(MessageFlags.Flagged),
                    Mailbox = folder.FullName
                };
                results.Add(email);
            }

            return results;
        }

        /// <summary>
        /// 获取邮件正文 (HTML 或 Text)
        /// </summary>
        /// <param name="folderName">文件夹名称</param>
        /// <param name="uid">邮件 UID</param>
        /// <returns>正文内容字符串</returns>
        public async Task<string> GetBodyAsync(string folderName, string uid)
        {
            var folder = await ResolveFolderAsync(folderName);
            await folder.OpenAsync(FolderAccess.ReadOnly);

            if (!UniqueId.TryParse(ExtractImapUid(uid), out var uniqueId))
                return "Invalid UID";

            // 简化逻辑：直接下载整封邮件并提取正文
            // 注意：对于非常大的邮件，这可能不是最高效的，但对于 MVP 是最稳健的
            // 获取消息
            var message = await folder.GetMessageAsync(uniqueId);
            
            // 尝试获取 HTML 部分
            var htmlPart = message.BodyParts.OfType<TextPart>().FirstOrDefault(x => x.IsHtml);
            if (htmlPart != null)
            {
                // 检查是否需要修复编码
                // 很多国内银行邮件(如工行)声明为 ISO-8859-1 但实际是 GBK/GB18030
                if (htmlPart.ContentType.Charset == null || 
                    htmlPart.ContentType.Charset.Equals("iso-8859-1", StringComparison.OrdinalIgnoreCase) ||
                    htmlPart.ContentType.Charset.Equals("us-ascii", StringComparison.OrdinalIgnoreCase))
                {
                    try 
                    {
                        // 尝试用 GB18030 解码
                        // 注意：我们需要先获取原始字节
                        using (var memory = new MemoryStream())
                        {
                            htmlPart.Content.DecodeTo(memory);
                            var bytes = memory.ToArray();
                            
                            // 简单的启发式检测：如果包含大量高位字节，可能是 GBK
                            if (bytes.Any(b => b > 127))
                            {
                                var gbk = System.Text.Encoding.GetEncoding("GB18030");
                                return gbk.GetString(bytes);
                            }
                        }
                    }
                    catch
                    {
                        // 如果失败，回退到默认处理
                    }
                }
                
                return htmlPart.Text;
            }

            // 如果没有 HTML，尝试纯文本
            return message.TextBody ?? "";
        }
        public void Dispose()
        {
            _client.Dispose();
        }

        /// <summary>
        /// 确保 IMAP 客户端已连接且已认证
        /// </summary>
        private void EnsureConnected()
        {
            if (!_client.IsConnected)
            {
                throw new InvalidOperationException("IMAP 客户端未连接。请先调用 ConnectAsync。");
            }
            if (!_client.IsAuthenticated)
            {
                throw new InvalidOperationException("IMAP 客户端未认证。请先调用 ConnectAsync。");
            }
        }
        /// <summary>
        /// 标记邮件为未读
        /// </summary>
        public async Task MarkAsUnreadAsync(string folderName, string uid)
        {
            System.Diagnostics.Debug.WriteLine($"[ImapService] 开始标记邮件为未读: Folder={folderName}, UID={uid}");
            EnsureConnected(); // 验证连接状态

            var folder = await ResolveFolderAsync(folderName);
            await folder.OpenAsync(FolderAccess.ReadWrite);

            if (UniqueId.TryParse(ExtractImapUid(uid), out var uniqueId))
            {
                await folder.RemoveFlagsAsync(uniqueId, MessageFlags.Seen, true);
                System.Diagnostics.Debug.WriteLine($"[ImapService] ✓ 成功标记邮件为未读: UID={uid}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[ImapService] ✗ 无效的 UID 格式: {uid}");
            }
        }

        /// <summary>
        /// 标记邮件为已读
        /// </summary>
        public async Task MarkAsReadAsync(string folderName, string uid)
        {
            System.Diagnostics.Debug.WriteLine($"[ImapService] 开始标记邮件为已读: Folder={folderName}, UID={uid}");
            EnsureConnected(); // 验证连接状态

            var folder = await ResolveFolderAsync(folderName);
            await folder.OpenAsync(FolderAccess.ReadWrite);

            if (UniqueId.TryParse(ExtractImapUid(uid), out var uniqueId))
            {
                await folder.AddFlagsAsync(uniqueId, MessageFlags.Seen, true);
                System.Diagnostics.Debug.WriteLine($"[ImapService] ✓ 成功标记邮件为已读: UID={uid}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[ImapService] ✗ 无效的 UID 格式: {uid}");
            }
        }

        /// <summary>
        /// 删除邮件 (移动到 Trash)
        /// </summary>
        public async Task DeleteMessageAsync(string folderName, string uid)
        {
            System.Diagnostics.Debug.WriteLine($"[ImapService] 开始删除邮件: Folder={folderName}, UID={uid}");
            EnsureConnected(); // 验证连接状态

            var folder = await ResolveFolderAsync(folderName);
            await folder.OpenAsync(FolderAccess.ReadWrite);

            if (UniqueId.TryParse(ExtractImapUid(uid), out var uniqueId))
            {
                // 尝试查找 Trash 文件夹
                var trash = _client.GetFolder(SpecialFolder.Trash);
                if (trash != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[ImapService] 移动邮件到 Trash 文件夹: {trash.FullName}");
                    await folder.MoveToAsync(uniqueId, trash);
                    System.Diagnostics.Debug.WriteLine($"[ImapService] ✓ 成功移动邮件到 Trash: UID={uid}");
                }
                else
                {
                    // 如果找不到 Trash，则标记为 Deleted (软删除)
                    System.Diagnostics.Debug.WriteLine($"[ImapService] Trash 文件夹未找到，标记为 Deleted");
                    await folder.AddFlagsAsync(uniqueId, MessageFlags.Deleted, true);
                    System.Diagnostics.Debug.WriteLine($"[ImapService] ✓ 成功标记邮件为 Deleted: UID={uid}");
                    // 注意：通常需要 Expunge 才能真正删除，但为了安全起见，这里先只标记
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[ImapService] ✗ 无效的 UID 格式: {uid}");
            }
        }

        /// <summary>
        /// 切换星标状态
        /// </summary>
        public async Task ToggleFlagAsync(string folderName, string uid, bool isStarred)
        {
            System.Diagnostics.Debug.WriteLine($"[ImapService] 开始切换星标: Folder={folderName}, UID={uid}, IsStarred={isStarred}");
            EnsureConnected(); // 验证连接状态

            var folder = await ResolveFolderAsync(folderName);
            await folder.OpenAsync(FolderAccess.ReadWrite);

            if (UniqueId.TryParse(ExtractImapUid(uid), out var uniqueId))
            {
                if (isStarred)
                {
                    await folder.AddFlagsAsync(uniqueId, MessageFlags.Flagged, true);
                    System.Diagnostics.Debug.WriteLine($"[ImapService] ✓ 成功添加星标: UID={uid}");
                }
                else
                {
                    await folder.RemoveFlagsAsync(uniqueId, MessageFlags.Flagged, true);
                    System.Diagnostics.Debug.WriteLine($"[ImapService] ✓ 成功移除星标: UID={uid}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[ImapService] ✗ 无效的 UID 格式: {uid}");
            }
        }

        /// <summary>
        /// 兼容 Inbox 和服务商真实文件夹路径。
        /// </summary>
        private async Task<IMailFolder> ResolveFolderAsync(string folderName)
        {
            if (folderName.Equals("Inbox", StringComparison.OrdinalIgnoreCase))
            {
                return _client.Inbox;
            }

            return await _client.GetFolderAsync(folderName);
        }

        /// <summary>
        /// 生成本地唯一 ID，避免不同账号或文件夹的 UID 互相覆盖。
        /// </summary>
        private string BuildLocalEmailId(string folderName, UniqueId uid)
        {
            return $"{_accountId}|{folderName}|{uid}";
        }

        /// <summary>
        /// 从本地组合 ID 中取回 IMAP 真正需要的 UID。
        /// </summary>
        private static string ExtractImapUid(string localEmailId)
        {
            var separatorIndex = localEmailId.LastIndexOf('|');
            return separatorIndex >= 0 ? localEmailId[(separatorIndex + 1)..] : localEmailId;
        }
    }
}
