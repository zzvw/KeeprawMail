using KeeprawMail.Models;
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
        /// 设置代理
        /// </summary>
        /// <param name="type">代理类型 (SOCKS5, HTTP)</param>
        /// <param name="host">主机地址</param>
        /// <param name="port">端口</param>
        public void SetProxy(string type, string host, int port)
        {
            if (string.IsNullOrEmpty(host)) return;

            // 严格按照用户选择的代理类型设置，不再自动切换
            // 用户应该根据代理软件的实际支持情况选择正确的类型
            if (type.Equals("SOCKS5", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[ImapService] Setting SOCKS5 proxy: {host}:{port}");
                _client.ProxyClient = new MailKit.Net.Proxy.Socks5Client(host, port);
            }
            else if (type.Equals("SOCKS4", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[ImapService] Setting SOCKS4 proxy: {host}:{port}");
                _client.ProxyClient = new MailKit.Net.Proxy.Socks4Client(host, port);
            }
            else if (type.Equals("SOCKS4A", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[ImapService] Setting SOCKS4A proxy: {host}:{port}");
                _client.ProxyClient = new MailKit.Net.Proxy.Socks4aClient(host, port);
            }
            else if (type.Equals("HTTP", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[ImapService] Setting HTTP proxy: {host}:{port}");
                _client.ProxyClient = new MailKit.Net.Proxy.HttpProxyClient(host, port);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[ImapService] Unknown proxy type '{type}', ignoring proxy settings");
            }
        }

        /// <summary>
        /// 连接到 IMAP 服务器
        /// </summary>
        /// <param name="account">账号配置信息</param>
        public async Task ConnectAsync(AccountProfile account)
        {
            if (_client.IsConnected)
                await _client.DisconnectAsync(true);

            _accountId = account.Id;

            // 优化：禁用证书吊销检查以提升连接速度（不影响安全性）
            _client.CheckCertificateRevocation = false;

            // 1. 配置代理
            if (!string.IsNullOrEmpty(account.ProxyType) && account.ProxyType != "NONE")
            {
                System.Diagnostics.Debug.WriteLine($"[ImapService] Setting proxy for {account.Email}: {account.ProxyType}://{account.ProxyHost}:{account.ProxyPort}");
                SetProxy(account.ProxyType, account.ProxyHost, account.ProxyPort);
            }
            else
            {
                _client.ProxyClient = null;
            }

            // 2. 连接
            System.Diagnostics.Debug.WriteLine($"[ImapService] Attempting to connect to {account.ImapServer}:{account.ImapPort}...");
            
            // 对于 993 端口（隐式 SSL），使用 SslOnConnect
            // 对于其他端口，使用 StartTls（显式 TLS）
            var sslOptions = account.ImapPort == 993 
                ? SecureSocketOptions.SslOnConnect 
                : SecureSocketOptions.StartTls;
            
            await _client.ConnectAsync(account.ImapServer, account.ImapPort, sslOptions);
            System.Diagnostics.Debug.WriteLine($"[ImapService] ✓ Successfully connected to {account.ImapServer}");

            // 3. 认证
            if (!string.IsNullOrEmpty(account.OAuthToken))
            {
                // 检查 Token 是否过期 (提前 5 分钟刷新)
                // 注意：为了稳健性，如果连接失败提示 invalid_grant，通常也需要刷新，但这里先只按时间判断
                if (account.TokenExpiration < DateTimeOffset.Now.AddMinutes(5) && !string.IsNullOrEmpty(account.RefreshToken))
                {
                    try
                    {
                        var oauthService = new OAuthService();
                        var (newAccessToken, expiresIn) = await oauthService.RefreshAccessTokenAsync(
                            account.RefreshToken, 
                            account.ProxyType,
                            account.ProxyType == "NONE" ? "" : account.ProxyHost, 
                            account.ProxyPort);
                        
                        // 更新内存中的账号信息
                        account.OAuthToken = newAccessToken;
                        account.TokenExpiration = DateTimeOffset.Now.AddSeconds(expiresIn);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Token Refresh Failed: {ex.Message}");
                    }
                }

                var oauth2 = new SaslMechanismOAuth2(account.Email, account.OAuthToken);
                await _client.AuthenticateAsync(oauth2);
            }
            else
            {
                // 传统密码认证
                await _client.AuthenticateAsync(account.Email, account.Password);
            }
        }

        /// <summary>
        /// 获取需要同步的常用邮件文件夹。
        /// </summary>
        public Task<List<string>> GetFoldersToSyncAsync()
        {
            var folders = new List<string>();

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
