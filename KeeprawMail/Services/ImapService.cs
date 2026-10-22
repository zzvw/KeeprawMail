using KeeprawMail.Models;
using KeeprawMail.Core.Imap;
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
        /// 为同步建立连接：连接 + TLS + 认证（统一代理与 TokenManager）→ 列举需要同步的文件夹 → 创建 UID 增量同步会话。
        /// 返回的会话释放时一并断开本连接。旧的 SyncHeadersAsync（按序号取最近 50 封）与 GetBodyAsync（整封下载）已移除。
        /// </summary>
        public static async Task<AccountSyncConnection> ConnectForSyncAsync(AccountProfile account, CancellationToken cancellationToken = default)
        {
            var service = new ImapService();
            try
            {
                await service.ConnectAsync(account, cancellationToken);

                List<string> folders;
                try
                {
                    folders = await service.GetFoldersToSyncAsync();
                }
                catch (MailSyncException) { throw; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new MailSyncException(SyncStage.ListFolders, $"列举 {account.Email} 的文件夹失败：{ex.Message}", ex);
                }

                var session = await service.CreateSessionAsync(cancellationToken);
                return new AccountSyncConnection(session, folders);
            }
            catch
            {
                service.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 为按需读取正文建立会话：使用邮件所属账号自己的代理配置与 TokenManager（与同步完全相同的连接路径），
        /// 缓存缺失时也不会绕过代理直连。返回的会话释放时一并断开连接。
        /// </summary>
        public static async Task<IImapMailSession> OpenSessionAsync(AccountProfile account, CancellationToken cancellationToken = default)
        {
            var service = new ImapService();
            try
            {
                await service.ConnectAsync(account, cancellationToken);
                return await service.CreateSessionAsync(cancellationToken);
            }
            catch
            {
                service.Dispose();
                throw;
            }
        }

        private async Task<IImapMailSession> CreateSessionAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await MailKitImapSession.CreateAsync(_client, this, enableQResync: true, cancellationToken);
            }
            catch (ImapCommandException ex)
            {
                // 服务器宣称支持 QRESYNC 但 ENABLE 失败：退回 CONDSTORE / UID 核对策略，不影响同步正确性
                DiagnosticLog.Write("imap", $"ENABLE QRESYNC failed ({ex.Response}), falling back");
                return await MailKitImapSession.CreateAsync(_client, this, enableQResync: false, cancellationToken);
            }
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
        /// 从本地组合 ID 中取回 IMAP 真正需要的 UID。
        /// </summary>
        private static string ExtractImapUid(string localEmailId)
        {
            var separatorIndex = localEmailId.LastIndexOf('|');
            return separatorIndex >= 0 ? localEmailId[(separatorIndex + 1)..] : localEmailId;
        }
    }
}
