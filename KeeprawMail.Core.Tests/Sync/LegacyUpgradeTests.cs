using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Tests.Fakes;
using Microsoft.Data.Sqlite;

namespace KeeprawMail.Core.Tests.Sync;

/// <summary>旧版（结构版本 2、无同步状态、无 ImapUid 列）数据库升级到版本 3 及首次基线同步。</summary>
public class LegacyUpgradeTests : IDisposable
{
    private readonly SyncTestDb _db = new();
    public void Dispose() => _db.Dispose();

    /// <summary>按 PR1 的真实结构建库（Emails 无 ImapUid，user_version = 2），写入旧数据与正文缓存。</summary>
    private async Task<Dictionary<uint, string>> CreateLegacyDatabaseAsync()
    {
        using (var c = new SqliteConnection(_db.Cs))
        {
            await c.OpenAsync();
            foreach (var sql in new[]
            {
                AccountRepository.CreateTableSql,
                @"CREATE TABLE IF NOT EXISTS Emails (Id TEXT PRIMARY KEY, Subject TEXT, SenderName TEXT, SenderAddress TEXT, Date TEXT, Snippet TEXT,
                    BodyLocalPath TEXT, IsRead INTEGER, Mailbox TEXT, AccountId TEXT, IsStarred INTEGER DEFAULT 0, FOREIGN KEY(AccountId) REFERENCES Accounts(Id));",
                @"CREATE VIRTUAL TABLE IF NOT EXISTS Emails_FTS USING fts5(Subject, SenderName, SenderAddress, Snippet, content='Emails', content_rowid='rowid');",
                @"CREATE TRIGGER IF NOT EXISTS Emails_ai AFTER INSERT ON Emails BEGIN INSERT INTO Emails_FTS(rowid, Subject, SenderName, SenderAddress, Snippet) VALUES (new.rowid, new.Subject, new.SenderName, new.SenderAddress, new.Snippet); END;",
                @"CREATE TRIGGER IF NOT EXISTS Emails_ad AFTER DELETE ON Emails BEGIN INSERT INTO Emails_FTS(Emails_FTS, rowid, Subject, SenderName, SenderAddress, Snippet) VALUES('delete', old.rowid, old.Subject, old.SenderName, old.SenderAddress, old.Snippet); END;",
                @"CREATE TRIGGER IF NOT EXISTS Emails_au AFTER UPDATE OF Subject, SenderName, SenderAddress, Snippet ON Emails BEGIN
                    INSERT INTO Emails_FTS(Emails_FTS, rowid, Subject, SenderName, SenderAddress, Snippet) VALUES('delete', old.rowid, old.Subject, old.SenderName, old.SenderAddress, old.Snippet);
                    INSERT INTO Emails_FTS(rowid, Subject, SenderName, SenderAddress, Snippet) VALUES (new.rowid, new.Subject, new.SenderName, new.SenderAddress, new.Snippet); END;",
                MailStoreSchema.PendingCleanupTableSql,
                "INSERT INTO Accounts (Id, Email, Provider, ImapServer, ImapPort) VALUES ('acc', 'acc@example.test', 0, 'imap.example.test', 993)",
                "PRAGMA user_version = 2",
            })
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        var paths = new Dictionary<uint, string>();
        using (var c = new SqliteConnection(_db.Cs))
        {
            await c.OpenAsync();
            foreach (var uid in new uint[] { 1, 2, 3, 4 })
            {
                var id = $"acc|INBOX|{uid}";
                var path = await _db.Cache.WriteTextAsync(_db.Cache.GetBodyPath("acc", id), $"<p>legacy body {uid}</p>");
                paths[uid] = path;
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"INSERT INTO Emails (Id, Subject, SenderName, SenderAddress, Date, Snippet, BodyLocalPath, IsRead, Mailbox, AccountId, IsStarred)
                                    VALUES ($Id, $S, 'Sender', 'sender@example.test', $D, $Sn, $P, 1, 'INBOX', 'acc', 0)";
                cmd.Parameters.AddWithValue("$Id", id);
                cmd.Parameters.AddWithValue("$S", $"legacy{uid}");
                cmd.Parameters.AddWithValue("$D", DateTimeOffset.UnixEpoch.AddDays(uid + 1).ToString("o"));
                cmd.Parameters.AddWithValue("$Sn", $"legacysnippet{uid}");
                cmd.Parameters.AddWithValue("$P", path);
                await cmd.ExecuteNonQueryAsync();
            }
            // 一条 Id 无法解析出 UID 的异常旧行
            using var odd = c.CreateCommand();
            odd.CommandText = "INSERT INTO Emails (Id, Subject, Date, Snippet, BodyLocalPath, IsRead, Mailbox, AccountId) VALUES ('weird-id', 'odd', '2020-01-01', '', '', 0, 'INBOX', 'acc')";
            await odd.ExecuteNonQueryAsync();
        }
        return paths;
    }

    [Fact]
    public async Task Upgrade_PreservesEmailsAndBodyCache_BackfillsUid_IsRepeatable()
    {
        var paths = await CreateLegacyDatabaseAsync();

        await _db.Repo.EnsureSchemaAsync();
        await _db.Repo.EnsureSchemaAsync(); // 迁移可重复执行

        Assert.Equal(MailStoreSchema.SchemaVersion, await _db.ScalarAsync("PRAGMA user_version")); // v2 → 当前版本（PR3 起为 5：v4 预览状态 + v5 预览算法版本 / 诊断）
        Assert.Equal(5, MailStoreSchema.SchemaVersion);
        Assert.Equal(5, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails"));
        Assert.Equal(1, await _db.ScalarAsync("SELECT COUNT(*) FROM Accounts"));
        Assert.Equal(0, await _db.ScalarAsync("SELECT COUNT(*) FROM MailboxSyncState"));
        var rows = await _db.Emails.GetForAccountAsync("acc");
        foreach (var uid in new uint[] { 1, 2, 3, 4 })
        {
            var row = rows.Single(r => r.Id == $"acc|INBOX|{uid}");
            Assert.Equal(uid, row.ImapUid);
            Assert.Equal(paths[uid], row.BodyLocalPath);
            Assert.True(File.Exists(paths[uid]));
        }
        Assert.Null(rows.Single(r => r.Id == "weird-id").ImapUid);
        Assert.Single(await _db.Emails.SearchIdsAsync("legacysnippet3"));
        await _db.Emails.IntegrityCheckAsync();
    }

    [Fact]
    public async Task LegacyBaseline_KeepsVerifiedCache_DropsStaleAndUnverifiableMappings()
    {
        var paths = await CreateLegacyDatabaseAsync();
        await _db.Repo.EnsureSchemaAsync();

        var server = new FakeImapServer();
        var inbox = server.Mailbox("INBOX");
        inbox.UidValidity = 777;
        // UID 1、2：与本地记录一致（同主题、发件人、时间） ⇒ 保留正文缓存
        foreach (var uid in new uint[] { 1, 2 })
            inbox.Messages[uid] = new FakeMessage { Uid = uid, Subject = $"legacy{uid}", SenderAddress = "sender@example.test", Date = DateTimeOffset.UnixEpoch.AddDays(uid + 1), Seen = false };
        // UID 3：服务器上同一 UID 是另一封邮件（旧版无法知道 UIDVALIDITY 是否变过）⇒ 丢弃旧正文缓存
        inbox.Messages[3] = new FakeMessage { Uid = 3, Subject = "something else", SenderAddress = "other@example.test", Date = DateTimeOffset.UnixEpoch.AddDays(100) };
        // UID 4：服务器上已不存在 ⇒ 删除本地记录与缓存
        inbox.Messages[5] = new FakeMessage { Uid = 5, Subject = "new five", Date = DateTimeOffset.UnixEpoch.AddDays(200) };
        inbox.UidNext = 6;

        await using (var s = server.CreateSession())
        {
            var r = await _db.Engine().SyncAsync(s, "acc", "INBOX");
            Assert.True(r.BaselinePerformed);
            Assert.Equal(1, r.BodyCacheInvalidated);
            Assert.Equal(2, r.RemovedLocal); // UID 4 + 无法解析的旧行
        }
        Assert.Equal(0, server.BodyFetches);

        var rows = await _db.MailboxAsync("acc", "INBOX");
        Assert.Equal(new uint?[] { 1, 2, 3, 5 }, rows.Select(r => r.ImapUid));
        Assert.Equal(paths[1], rows[0].BodyLocalPath);
        Assert.Equal("legacysnippet1", rows[0].Snippet);
        Assert.False(rows[0].IsRead);                      // Flags 以服务器为准
        Assert.Equal(paths[2], rows[1].BodyLocalPath);
        Assert.Equal("", rows[2].BodyLocalPath);           // 映射无法确认：不再显示可能属于另一封邮件的正文
        Assert.Equal("", rows[2].Snippet);
        Assert.Equal("something else", rows[2].Subject);
        Assert.False(File.Exists(paths[3]));
        Assert.False(File.Exists(paths[4]));
        Assert.True(File.Exists(paths[1]));
        Assert.Equal(777u, (await _db.States.GetAsync("acc", "INBOX"))!.UidValidity);
        await _db.Emails.IntegrityCheckAsync();
    }

    [Fact]
    public async Task AccountDeletion_RemovesSyncState_InSameTransaction()
    {
        await _db.WithAccountsAsync("acc", "other");
        var server = new FakeImapServer();
        server.Mailbox("INBOX").Add("x");
        await using (var s = server.CreateSession()) await _db.Engine().SyncAsync(s, "acc", "INBOX");
        await using (var s = server.CreateSession()) await _db.Engine().SyncAsync(s, "other", "INBOX");

        var result = await _db.Repo.DeleteAsync("acc", _db.Cache);
        Assert.True(result.FullyCleaned);
        Assert.Equal(0, await _db.ScalarAsync("SELECT COUNT(*) FROM MailboxSyncState WHERE AccountId = 'acc'"));
        Assert.Equal(1, await _db.ScalarAsync("SELECT COUNT(*) FROM MailboxSyncState WHERE AccountId = 'other'"));
        Assert.Equal(0, await _db.ScalarAsync("SELECT COUNT(*) FROM Accounts WHERE Id = 'acc'"));
    }

    [Fact]
    public async Task AccountDeletion_RollsBackSyncStateWhenAccountDeleteFails()
    {
        await _db.WithAccountsAsync("acc");
        var server = new FakeImapServer();
        server.Mailbox("INBOX").Add("x");
        await using (var s = server.CreateSession()) await _db.Engine().SyncAsync(s, "acc", "INBOX");
        await _db.ExecAsync("CREATE TRIGGER block_delete BEFORE DELETE ON Accounts BEGIN SELECT RAISE(ABORT, 'injected'); END;");

        await Assert.ThrowsAsync<SqliteException>(() => _db.Repo.DeleteAsync("acc", _db.Cache));
        Assert.Equal(1, await _db.ScalarAsync("SELECT COUNT(*) FROM MailboxSyncState WHERE AccountId = 'acc'"));
        Assert.Equal(1, await _db.ScalarAsync("SELECT COUNT(*) FROM Emails WHERE AccountId = 'acc'"));
    }

    [Fact]
    public async Task SyncState_ForeignKey_RequiresAccount()
    {
        await _db.WithAccountsAsync("acc");
        await Assert.ThrowsAsync<SqliteException>(() => _db.States.SaveAsync(new MailboxSyncState { AccountId = "nobody", MailboxName = "INBOX", UidValidity = 1 }));
    }
}
