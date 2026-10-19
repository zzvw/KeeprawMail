using KeeprawMail.Core.Auth;
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
        private readonly AccountSecretsService? _secretsOverride;

        /// <summary>
        /// 账号凭据服务（密码、OAuth Token、迁移、删除共用同一个存储与同一个 TokenManager）。
        /// 默认使用进程单例 AppServices.Secrets，测试或工具可注入。
        /// </summary>
        private AccountSecretsService Secrets => _secretsOverride ?? Services.AppServices.Secrets;

        private AccountRepository Accounts => new(ConnectionString, Secrets);

        /// <summary>最近一次启动迁移的结果（用于提示用户；不含任何凭据）。</summary>
        public static CredentialMigrationResult? LastMigrationResult { get; private set; }

        /// <summary>最近一次启动迁移是否整体失败（如安全存储不可用）。</summary>
        public static bool LastMigrationCrashed { get; private set; }

        /// <summary>
        /// 凭据迁移未完全成功时给用户看的提示（状态栏）；成功时为 null。
        /// 账号不会因迁移失败而丢失，下次启动会自动重试。
        /// </summary>
        public static string? CredentialMigrationNotice =>
            LastMigrationCrashed
                ? "安全凭据存储暂不可用：已有账号继续按旧方式工作，下次启动会自动重试迁移。"
                : LastMigrationResult is { FailedAccountIds.Count: > 0 } r
                    ? $"{r.FailedAccountIds.Count} 个账号的凭据未能迁移到安全存储，仍可正常使用，下次启动会自动重试。"
                    : null;

        public DatabaseContext(AccountSecretsService secrets, string? databasePath = null) : this()
        {
            _secretsOverride = secrets;
            if (!string.IsNullOrEmpty(databasePath))
                _dbPath = databasePath;
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
                var result = await Secrets.CreateMigrator(ConnectionString).MigrateAsync();
                LastMigrationResult = result;
                LastMigrationCrashed = false;
                System.Diagnostics.Debug.WriteLine(
                    $"[DatabaseContext] Credential migration: migrated={result.Migrated}, alreadySecure={result.AlreadySecure}, failed={result.FailedAccountIds.Count}");
            }
            catch (Exception ex)
            {
                // 迁移失败不影响启动：旧账号仍可用明文兜底，下次启动重试
                LastMigrationCrashed = true;
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
            return await Secrets.CreateMigrator(ConnectionString).RollbackAsync();
        }

        private async Task InitializeSchemaAsync()
        {
            using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            // 1. 创建 Accounts 表（建表语句与 Core 中的 AccountRepository 共用）
            await ExecuteNonQueryAsync(connection, AccountRepository.CreateTableSql);

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
        /// 保存账号：凭据写安全存储，数据库只存非敏感信息（实现见 Core.Storage.AccountRepository）。
        /// </summary>
        public async Task SaveAccountAsync(Models.AccountProfile account)
        {
            var record = ToRecord(account);
            await Accounts.SaveAsync(record);
            account.AuthType = record.AuthType;
            account.CredentialVersion = record.CredentialVersion;
            // Token 已交给 TokenManager，内存中不再保留
            account.OAuthToken = null;
            account.RefreshToken = null;
        }

        /// <summary>获取所有账号。</summary>
        public async Task<System.Collections.Generic.List<Models.AccountProfile>> GetAccountsAsync()
        {
            var records = await Accounts.GetAllAsync();
            var results = new System.Collections.Generic.List<Models.AccountProfile>(records.Count);
            foreach (var r in records)
                results.Add(FromRecord(r));
            return results;
        }

        private static AccountRecord ToRecord(Models.AccountProfile a) => new()
        {
            Id = a.Id,
            Email = a.Email,
            Provider = (int)a.Provider,
            ImapServer = a.ImapServer,
            ImapPort = a.ImapPort,
            SmtpServer = a.SmtpServer,
            SmtpPort = a.SmtpPort,
            ProxyType = a.ProxyType,
            ProxyHost = a.ProxyHost,
            ProxyPort = a.ProxyPort,
            AuthType = a.AuthType,
            CredentialVersion = a.CredentialVersion,
            Password = a.Password,
            PendingOAuthTokens = string.IsNullOrEmpty(a.RefreshToken)
                ? null
                : new OAuthTokenSet(a.RefreshToken!, a.OAuthToken, a.TokenExpiration),
        };

        private static Models.AccountProfile FromRecord(AccountRecord r) => new()
        {
            Id = r.Id,
            Email = r.Email,
            Provider = (Models.EmailProvider)r.Provider,
            ImapServer = r.ImapServer,
            ImapPort = r.ImapPort,
            SmtpServer = r.SmtpServer,
            SmtpPort = r.SmtpPort,
            ProxyType = r.ProxyType,
            ProxyHost = r.ProxyHost,
            ProxyPort = r.ProxyPort,
            AuthType = r.AuthType,
            CredentialVersion = r.CredentialVersion,
            Password = r.Password,
            CredentialUnreadable = r.CredentialUnreadable,
            // 旧版明文行：把 Token 带上，首次认证时迁入安全存储
            OAuthToken = r.PendingOAuthTokens?.AccessToken,
            RefreshToken = string.IsNullOrEmpty(r.PendingOAuthTokens?.RefreshToken) ? null : r.PendingOAuthTokens!.RefreshToken,
            TokenExpiration = r.PendingOAuthTokens?.AccessTokenExpiresAt ?? DateTimeOffset.MinValue,
        };

        /// <summary>
        /// 删除账号（连同安全存储中的凭据）。
        /// </summary>
        public Task DeleteAccountAsync(string accountId) => Accounts.DeleteAsync(accountId);

        /// <summary>删除账号在安全存储中的全部凭据。</summary>
        private async Task DeleteCredentialsAsync(string accountId)
        {
            try
            {
                await Accounts.DeleteCredentialsAsync(accountId);
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
