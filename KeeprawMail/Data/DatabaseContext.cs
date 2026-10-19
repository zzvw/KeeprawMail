using KeeprawMail.Core.Auth;
using KeeprawMail.Core.Security;
using KeeprawMail.Core.Storage;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Threading.Tasks;

namespace KeeprawMail.Data
{
    /// <summary>
    /// 数据库上下文
    /// 负责 SQLite 数据库的初始化、连接和基础操作
    /// </summary>
    public class DatabaseContext : IDisposable
    {
        private const string DbName = "keepraw.db";
        private string _dbPath = string.Empty;
        private readonly ICredentialStore? _credentialStoreOverride;

        /// <summary>
        /// 凭据（密码 / OAuth Token）存储。数据库中只保存账号的非敏感信息。
        /// </summary>
        private ICredentialStore CredentialStore => _credentialStoreOverride ?? Services.AppServices.CredentialStore;

        public DatabaseContext(ICredentialStore? credentialStore = null) : this()
        {
            _credentialStoreOverride = credentialStore;
        }

        public DatabaseContext()
        {
            // 获取应用本地数据文件夹路径
            // 对于未打包应用，使用 LocalApplicationData 文件夹
            var localFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appFolder = Path.Combine(localFolder, "KeeprawMail");
            
            // 确保文件夹存在
            if (!Directory.Exists(appFolder))
                Directory.CreateDirectory(appFolder);
                
            _dbPath = Path.Combine(appFolder, DbName);
        }

        /// <summary>
        /// 获取数据库连接字符串
        /// </summary>
        private string ConnectionString => $"Data Source={_dbPath};Cache=Shared";

        /// <summary>
        /// 初始化数据库
        /// 创建表和 FTS5 虚拟表
        /// </summary>
        public async Task InitializeAsync()
        {
            await InitializeSchemaAsync();

            // 8. 凭据迁移：把旧版明文密码 / Token 迁到 DPAPI 安全存储。
            //    每个账号"写入 → 读回校验 → 再清明文"，失败的账号保持原样，下次启动重试。
            try
            {
                var result = await new AccountCredentialMigrator(ConnectionString, CredentialStore).MigrateAsync();
                System.Diagnostics.Debug.WriteLine(
                    $"[DatabaseContext] Credential migration: migrated={result.Migrated}, alreadySecure={result.AlreadySecure}, failed={result.FailedAccountIds.Count}");
            }
            catch (Exception ex)
            {
                // 迁移失败不影响启动：旧账号仍可用明文兜底
                System.Diagnostics.Debug.WriteLine($"[DatabaseContext] Credential migration skipped: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// 回滚凭据迁移（仅用于降级到旧版程序）：把安全存储中的凭据写回明文列。
        /// 通过命令行参数 --rollback-credential-migration 触发。
        /// </summary>
        public async Task<int> RollbackCredentialMigrationAsync()
        {
            await InitializeSchemaAsync();
            return await new AccountCredentialMigrator(ConnectionString, CredentialStore).RollbackAsync();
        }

        private async Task InitializeSchemaAsync()
        {
            using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            // 1. 创建 Accounts 表
            var createAccountsTable = @"
                CREATE TABLE IF NOT EXISTS Accounts (
                    Id TEXT PRIMARY KEY,
                    Email TEXT NOT NULL,
                    Provider INTEGER,
                    ImapServer TEXT,
                    ImapPort INTEGER,
                    SmtpServer TEXT,
                    SmtpPort INTEGER,
                    Password TEXT,
                    OAuthToken TEXT,
                    RefreshToken TEXT,
                    TokenExpiration TEXT,
                    ProxyType TEXT,
                    ProxyHost TEXT,
                    ProxyPort INTEGER
                );";
            await ExecuteNonQueryAsync(connection, createAccountsTable);

            // 2. 创建 Emails 表 (主表)
            var createEmailsTable = @"
                CREATE TABLE IF NOT EXISTS Emails (
                    Id TEXT PRIMARY KEY,
                    Subject TEXT,
                    SenderName TEXT,
                    SenderAddress TEXT,
                    Date TEXT,
                    Snippet TEXT,
                    BodyLocalPath TEXT,
                    IsRead INTEGER,
                    Mailbox TEXT,
                    AccountId TEXT,
                    IsStarred INTEGER DEFAULT 0,
                    FOREIGN KEY(AccountId) REFERENCES Accounts(Id)
                );";
            await ExecuteNonQueryAsync(connection, createEmailsTable);

            // 3. 创建 FTS5 虚拟表 (用于全文搜索)
            // 我们只索引需要搜索的字段：Subject, SenderName, SenderAddress, Snippet
            // 注意：FTS5 表是独立的，我们需要维护它与主表的数据同步 (或使用触发器)
            var createFtsTable = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS Emails_FTS USING fts5(
                    Subject, 
                    SenderName, 
                    SenderAddress, 
                    Snippet, 
                    content='Emails', 
                    content_rowid='rowid'
                );";
            await ExecuteNonQueryAsync(connection, createFtsTable);

            // 4. 创建触发器：当 Emails 表插入数据时，自动同步到 FTS 表
            var createInsertTrigger = @"
                CREATE TRIGGER IF NOT EXISTS Emails_ai AFTER INSERT ON Emails BEGIN
                    INSERT INTO Emails_FTS(rowid, Subject, SenderName, SenderAddress, Snippet) 
                    VALUES (new.rowid, new.Subject, new.SenderName, new.SenderAddress, new.Snippet);
                END;";
            await ExecuteNonQueryAsync(connection, createInsertTrigger);

            // 5. 创建触发器：当 Emails 表删除数据时，自动从 FTS 表删除
            var createDeleteTrigger = @"
                CREATE TRIGGER IF NOT EXISTS Emails_ad AFTER DELETE ON Emails BEGIN
                    INSERT INTO Emails_FTS(Emails_FTS, rowid, Subject, SenderName, SenderAddress, Snippet) 
                    VALUES('delete', old.rowid, old.Subject, old.SenderName, old.SenderAddress, old.Snippet);
                END;";
            await ExecuteNonQueryAsync(connection, createDeleteTrigger);
            
            // Update 触发器略过，MVP 阶段邮件内容通常不变，状态改变不影响搜索

            // 6. 创建 AppSettings 表 (应用级设置存储)
            // 使用 Key-Value 结构，灵活易扩展
            var createAppSettingsTable = @"
                CREATE TABLE IF NOT EXISTS AppSettings (
                    SettingKey TEXT PRIMARY KEY,
                    SettingValue TEXT
                );";
            await ExecuteNonQueryAsync(connection, createAppSettingsTable);

            // 7. 数据库迁移 (检查并添加新字段)
            await MigrateDatabaseAsync(connection);
            await AccountCredentialMigrator.EnsureSchemaAsync(connection);
        }

        private async Task MigrateDatabaseAsync(SqliteConnection connection)
        {
            // 获取 Accounts 表的所有列信息
            var columns = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(Accounts)";
            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(1)); // name column
                }
            }

            // 检查并添加 RefreshToken
            if (!columns.Contains("RefreshToken"))
            {
                var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE Accounts ADD COLUMN RefreshToken TEXT";
                await alterCmd.ExecuteNonQueryAsync();
            }

            // 检查并添加 TokenExpiration
            if (!columns.Contains("TokenExpiration"))
            {
                var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE Accounts ADD COLUMN TokenExpiration TEXT";
                await alterCmd.ExecuteNonQueryAsync();
            }

            // 检查并添加 ProxyType
            if (!columns.Contains("ProxyType"))
            {
                var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE Accounts ADD COLUMN ProxyType TEXT DEFAULT 'NONE'";
                await alterCmd.ExecuteNonQueryAsync();
            }

            // 检查并添加 ProxyHost
            if (!columns.Contains("ProxyHost"))
            {
                var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE Accounts ADD COLUMN ProxyHost TEXT";
                await alterCmd.ExecuteNonQueryAsync();
            }

            // 检查并添加 ProxyPort
            if (!columns.Contains("ProxyPort"))
            {
                var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE Accounts ADD COLUMN ProxyPort INTEGER DEFAULT 0";
                await alterCmd.ExecuteNonQueryAsync();
            }

            // 检查 Emails 表并添加 IsStarred
            var emailColumns = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            command.CommandText = "PRAGMA table_info(Emails)";
            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    emailColumns.Add(reader.GetString(1));
                }
            }

            if (!emailColumns.Contains("IsStarred"))
            {
                var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE Emails ADD COLUMN IsStarred INTEGER DEFAULT 0";
                await alterCmd.ExecuteNonQueryAsync();
            }
        }

        /// <summary>
        /// 执行非查询 SQL 命令
        /// </summary>
        private async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText)
        {
            using var command = connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// 批量保存邮件到数据库
        /// 如果已存在则忽略 (INSERT OR IGNORE) 或 更新
        /// </summary>
        public async Task SaveEmailsAsync(IEnumerable<Models.EmailMessage> emails)
        {
            using var connection = GetConnection();
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT OR REPLACE INTO Emails (Id, Subject, SenderName, SenderAddress, Date, Snippet, BodyLocalPath, IsRead, Mailbox, AccountId, IsStarred)
                VALUES ($Id, $Subject, $SenderName, $SenderAddress, $Date, $Snippet, $BodyLocalPath, $IsRead, $Mailbox, $AccountId, $IsStarred)";

            // 预定义参数
            var pId = command.Parameters.Add("$Id", SqliteType.Text);
            var pSubject = command.Parameters.Add("$Subject", SqliteType.Text);
            var pSenderName = command.Parameters.Add("$SenderName", SqliteType.Text);
            var pSenderAddress = command.Parameters.Add("$SenderAddress", SqliteType.Text);
            var pDate = command.Parameters.Add("$Date", SqliteType.Text);
            var pSnippet = command.Parameters.Add("$Snippet", SqliteType.Text);
            var pBodyLocalPath = command.Parameters.Add("$BodyLocalPath", SqliteType.Text);
            var pIsRead = command.Parameters.Add("$IsRead", SqliteType.Integer);
            var pMailbox = command.Parameters.Add("$Mailbox", SqliteType.Text);
            var pAccountId = command.Parameters.Add("$AccountId", SqliteType.Text);
            var pIsStarred = command.Parameters.Add("$IsStarred", SqliteType.Integer);

            foreach (var email in emails)
            {
                pId.Value = email.Id;
                pSubject.Value = email.Subject ?? "";
                pSenderName.Value = email.SenderName ?? "";
                pSenderAddress.Value = email.SenderAddress ?? "";
                pDate.Value = email.Date.ToString("o"); // ISO 8601
                pSnippet.Value = email.Snippet ?? "";
                pBodyLocalPath.Value = email.BodyLocalPath ?? "";
                pIsRead.Value = email.IsRead ? 1 : 0;
                pMailbox.Value = email.Mailbox ?? "Inbox";
                pAccountId.Value = email.AccountId ?? "";
                command.Parameters["$IsStarred"].Value = email.IsStarred ? 1 : 0;

                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        /// <summary>
        /// 获取所有邮件 (按日期倒序)
        /// </summary>
        public async Task<System.Collections.Generic.List<Models.EmailMessage>> GetEmailsAsync()
        {
            var results = new System.Collections.Generic.List<Models.EmailMessage>();
            
            using var connection = GetConnection();
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM Emails ORDER BY Date DESC LIMIT 100";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new Models.EmailMessage
                {
                    Id = reader.GetString(0),
                    Subject = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    SenderName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    SenderAddress = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Date = DateTimeOffset.Parse(reader.GetString(4)),
                    Snippet = reader.IsDBNull(5) ? "" : reader.GetString(5),
                    BodyLocalPath = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    IsRead = reader.GetInt32(7) == 1,
                    Mailbox = reader.IsDBNull(8) ? "" : reader.GetString(8),
                    AccountId = reader.IsDBNull(9) ? "" : reader.GetString(9),
                    IsStarred = !reader.IsDBNull(10) && reader.GetInt32(10) == 1
                });
            }

            return results;
        }

        /// <summary>
        /// 保存账号。
        /// 顺序：先把密码 / OAuth Token 写入安全存储，成功后再写数据库行（敏感列始终为空）。
        /// 只写入非空的凭据：编辑代理等操作不会误删已保存的凭据。
        /// </summary>
        public async Task SaveAccountAsync(Models.AccountProfile account)
        {
            // 1. 凭据 → 安全存储
            if (!string.IsNullOrEmpty(account.Password))
            {
                await CredentialStore.SetAsync(CredentialKeys.Password(account.Id), account.Password);
            }

            if (!string.IsNullOrEmpty(account.RefreshToken))
            {
                account.AuthType = AccountAuthTypes.OAuth2;
                var tokens = new OAuthTokenSet(account.RefreshToken!, account.OAuthToken, account.TokenExpiration);
                await Services.AppServices.Tokens.SaveAsync(account.Id, tokens);
                // 交给 TokenManager 之后，内存中不再保留
                account.OAuthToken = null;
                account.RefreshToken = null;
            }

            // 2. 非敏感信息 → 数据库
            using var connection = GetConnection();
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT OR REPLACE INTO Accounts (Id, Email, Provider, ImapServer, ImapPort, SmtpServer, SmtpPort, Password, OAuthToken, RefreshToken, TokenExpiration, ProxyType, ProxyHost, ProxyPort, CredentialVersion, AuthType)
                VALUES ($Id, $Email, $Provider, $ImapServer, $ImapPort, $SmtpServer, $SmtpPort, '', '', '', $TokenExpiration, $ProxyType, $ProxyHost, $ProxyPort, $CredentialVersion, $AuthType)";

            command.Parameters.AddWithValue("$Id", account.Id);
            command.Parameters.AddWithValue("$Email", account.Email);
            command.Parameters.AddWithValue("$Provider", (int)account.Provider);
            command.Parameters.AddWithValue("$ImapServer", account.ImapServer);
            command.Parameters.AddWithValue("$ImapPort", account.ImapPort);
            command.Parameters.AddWithValue("$SmtpServer", account.SmtpServer);
            command.Parameters.AddWithValue("$SmtpPort", account.SmtpPort);
            command.Parameters.AddWithValue("$TokenExpiration", DateTimeOffset.MinValue.ToString("o"));
            command.Parameters.AddWithValue("$ProxyType", account.ProxyType);
            command.Parameters.AddWithValue("$ProxyHost", account.ProxyHost);
            command.Parameters.AddWithValue("$ProxyPort", account.ProxyPort);
            command.Parameters.AddWithValue("$CredentialVersion", AccountCredentialMigrator.SecureVersion);
            command.Parameters.AddWithValue("$AuthType", account.AuthType);

            await command.ExecuteNonQueryAsync();
            account.CredentialVersion = AccountCredentialMigrator.SecureVersion;
        }

        /// <summary>
        /// 获取所有账号。
        /// CredentialVersion = 1：密码从安全存储读取；OAuth Token 不加载（运行时由 TokenManager 提供）。
        /// CredentialVersion = 0：迁移尚未成功的旧账号，暂用数据库中的明文兜底。
        /// </summary>
        public async Task<System.Collections.Generic.List<Models.AccountProfile>> GetAccountsAsync()
        {
            var results = new System.Collections.Generic.List<Models.AccountProfile>();

            using var connection = GetConnection();
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Id, Email, Provider, ImapServer, ImapPort, SmtpServer, SmtpPort,
                       Password, OAuthToken, RefreshToken, TokenExpiration,
                       ProxyType, ProxyHost, ProxyPort,
                       IFNULL(CredentialVersion, 0), AuthType
                FROM Accounts";

            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var version = reader.GetInt32(14);
                    var legacyPassword = reader.IsDBNull(7) ? "" : reader.GetString(7);
                    var legacyAccess = reader.IsDBNull(8) ? "" : reader.GetString(8);
                    var legacyRefresh = reader.IsDBNull(9) ? "" : reader.GetString(9);
                    var authType = reader.IsDBNull(15) || string.IsNullOrEmpty(reader.GetString(15))
                        ? (legacyRefresh.Length > 0 || legacyAccess.Length > 0 ? AccountAuthTypes.OAuth2 : AccountAuthTypes.Password)
                        : reader.GetString(15);

                    var account = new Models.AccountProfile
                    {
                        Id = reader.GetString(0),
                        Email = reader.GetString(1),
                        Provider = (Models.EmailProvider)reader.GetInt32(2),
                        ImapServer = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        ImapPort = reader.IsDBNull(4) ? 993 : reader.GetInt32(4),
                        SmtpServer = reader.IsDBNull(5) ? "" : reader.GetString(5),
                        SmtpPort = reader.IsDBNull(6) ? 465 : reader.GetInt32(6),
                        ProxyType = reader.IsDBNull(11) ? "GLOBAL" : reader.GetString(11),
                        ProxyHost = reader.IsDBNull(12) ? "" : reader.GetString(12),
                        ProxyPort = reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
                        CredentialVersion = version,
                        AuthType = authType,
                    };

                    if (version == AccountCredentialMigrator.LegacyVersion)
                    {
                        account.Password = legacyPassword;
                        account.OAuthToken = legacyAccess.Length > 0 ? legacyAccess : null;
                        account.RefreshToken = legacyRefresh.Length > 0 ? legacyRefresh : null;
                        account.TokenExpiration = reader.IsDBNull(10) || !DateTimeOffset.TryParse(reader.GetString(10), out var exp)
                            ? DateTimeOffset.MinValue : exp;
                    }

                    results.Add(account);
                }
            }

            foreach (var account in results)
            {
                if (account.CredentialVersion == AccountCredentialMigrator.SecureVersion && !account.IsOAuth)
                {
                    try
                    {
                        account.Password = await CredentialStore.GetAsync(CredentialKeys.Password(account.Id)) ?? "";
                    }
                    catch (Exception ex)
                    {
                        // 例如换了 Windows 用户 / 重装系统导致 DPAPI 无法解密：账号保留，认证时提示重新输入
                        System.Diagnostics.Debug.WriteLine($"[DatabaseContext] Cannot read credential for account {account.Id}: {ex.GetType().Name}");
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// 删除账号
        /// </summary>
        public async Task DeleteAccountAsync(string accountId)
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Accounts WHERE Id = $Id";
            command.Parameters.AddWithValue("$Id", accountId);

            await command.ExecuteNonQueryAsync();

            await DeleteCredentialsAsync(accountId);
        }

        /// <summary>删除账号在安全存储中的全部凭据。</summary>
        private async Task DeleteCredentialsAsync(string accountId)
        {
            try
            {
                await CredentialStore.DeleteAsync(CredentialKeys.Password(accountId));
                await Services.AppServices.Tokens.DeleteAsync(accountId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DatabaseContext] Delete credentials failed for {accountId}: {ex.GetType().Name}");
            }
        }

        private async Task<System.Collections.Generic.List<string>> QueryIdsAsync(SqliteConnection connection, string sql)
        {
            var ids = new System.Collections.Generic.List<string>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                ids.Add(reader.GetString(0));
            return ids;
        }

        /// <summary>
        /// 获取一个新的数据库连接
        /// 调用者负责 Dispose
        /// </summary>
        public SqliteConnection GetConnection()
        {
            return new SqliteConnection(ConnectionString);
        }

        /// <summary>
        /// 清理重复的账号（保留最新的）
        /// </summary>
        public async Task CleanupDuplicateAccountsAsync()
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            var duplicateIds = await QueryIdsAsync(connection, @"
                SELECT Id FROM Accounts
                WHERE rowid NOT IN (SELECT MAX(rowid) FROM Accounts GROUP BY Email)");

            var command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM Accounts 
                WHERE rowid NOT IN (
                    SELECT MAX(rowid) 
                    FROM Accounts 
                    GROUP BY Email
                )";
            
            await command.ExecuteNonQueryAsync();

            foreach (var id in duplicateIds)
                await DeleteCredentialsAsync(id);
        }

        /// <summary>
        /// 清理重复的邮件（保留最新的）
        /// </summary>
        public async Task CleanupDuplicateEmailsAsync()
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM Emails 
                WHERE rowid NOT IN (
                    SELECT MAX(rowid) 
                    FROM Emails 
                    GROUP BY Id
                )";
            
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// 清空所有数据（慎用）
        /// </summary>
        public async Task ClearAllDataAsync()
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            var accountIds = await QueryIdsAsync(connection, "SELECT Id FROM Accounts");

            using var transaction = connection.BeginTransaction();
            
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            
            command.CommandText = "DELETE FROM Emails";
            await command.ExecuteNonQueryAsync();
            
            command.CommandText = "DELETE FROM Accounts";
            await command.ExecuteNonQueryAsync();
            
            await transaction.CommitAsync();

            foreach (var id in accountIds)
                await DeleteCredentialsAsync(id);
        }
        /// <summary>
        /// 更新邮件已读状态
        /// </summary>
        public async Task UpdateEmailReadStatusAsync(string emailId, bool isRead)
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "UPDATE Emails SET IsRead = $IsRead WHERE Id = $Id";
            command.Parameters.AddWithValue("$Id", emailId);
            command.Parameters.AddWithValue("$IsRead", isRead ? 1 : 0);

            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// 获取应用级设置值
        /// </summary>
        /// <param name="key">设置键</param>
        /// <returns>设置值，如果不存在则返回 null</returns>
        public async Task<string?> GetSettingAsync(string key)
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT SettingValue FROM AppSettings WHERE SettingKey = $Key";
            command.Parameters.AddWithValue("$Key", key);

            var result = await command.ExecuteScalarAsync();
            return result?.ToString();
        }

        /// <summary>
        /// 保存应用级设置值
        /// </summary>
        /// <param name="key">设置键</param>
        /// <param name="value">设置值</param>
        public async Task SaveSettingAsync(string key, string value)
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT OR REPLACE INTO AppSettings (SettingKey, SettingValue)
                VALUES ($Key, $Value)";
            command.Parameters.AddWithValue("$Key", key);
            command.Parameters.AddWithValue("$Value", value);

            await command.ExecuteNonQueryAsync();
        }

        public async Task UpdateEmailStarredStatusAsync(string emailId, bool isStarred)
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "UPDATE Emails SET IsStarred = $IsStarred WHERE Id = $Id";
            command.Parameters.AddWithValue("$Id", emailId);
            command.Parameters.AddWithValue("$IsStarred", isStarred ? 1 : 0);

            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// 从本地数据库删除邮件记录
        /// </summary>
        public async Task DeleteEmailAsync(string emailId)
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Emails WHERE Id = $Id";
            command.Parameters.AddWithValue("$Id", emailId);

            await command.ExecuteNonQueryAsync();
        }

        public void Dispose()
        {
            // 无需释放资源，因为 Connection 是按需创建的
        }
    }
}
