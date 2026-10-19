using System.Text;
using KeeprawMail.Core.Auth;
using KeeprawMail.Core.Security;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Tests.Fakes;
using Microsoft.Data.Sqlite;

namespace KeeprawMail.Core.Tests;

public class ProtectedFileCredentialStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "krm-cred-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task RoundTrip_Delete_AndNoPlaintextOnDisk()
    {
        var store = new ProtectedFileCredentialStore(_dir, new XorProtector());
        await store.SetAsync(CredentialKeys.Password("acc"), "s3cr3t-password");
        Assert.Equal("s3cr3t-password", await store.GetAsync(CredentialKeys.Password("acc")));

        foreach (var f in Directory.GetFiles(_dir))
        {
            var bytes = await File.ReadAllBytesAsync(f);
            Assert.DoesNotContain("s3cr3t-password", Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain("acc", Path.GetFileName(f)); // 文件名是哈希
        }

        await store.DeleteAsync(CredentialKeys.Password("acc"));
        Assert.Null(await store.GetAsync(CredentialKeys.Password("acc")));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Overwrite_IsAtomic_NoTempFilesLeft()
    {
        var store = new ProtectedFileCredentialStore(_dir, new XorProtector());
        for (var i = 0; i < 20; i++)
            await store.SetAsync("k", "v" + i);
        Assert.Equal("v19", await store.GetAsync("k"));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Dpapi_RoundTrip_OnWindows()
    {
        if (!OperatingSystem.IsWindows()) return; // 仅在 Windows 上可运行
        var p = new DpapiSecretProtector();
        var c = p.Protect(Encoding.UTF8.GetBytes("hello"));
        Assert.Equal("hello", Encoding.UTF8.GetString(p.Unprotect(c)));
    }
}

public class AccountCredentialMigratorTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "krm-db-" + Guid.NewGuid().ToString("N") + ".db");
    private string Cs => $"Data Source={_dbPath};Pooling=False";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    /// <summary>按旧版本（本 PR 之前）的表结构建库，并写入明文凭据。</summary>
    private void CreateLegacyDb()
    {
        using var c = new SqliteConnection(Cs);
        c.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE Accounts (
                Id TEXT PRIMARY KEY, Email TEXT NOT NULL, Provider INTEGER, ImapServer TEXT, ImapPort INTEGER,
                SmtpServer TEXT, SmtpPort INTEGER, Password TEXT, OAuthToken TEXT, RefreshToken TEXT,
                TokenExpiration TEXT, ProxyType TEXT, ProxyHost TEXT, ProxyPort INTEGER);
            INSERT INTO Accounts VALUES ('pw-1','qq@example.test',3,'imap.qq.com',993,'smtp.qq.com',465,'LEGACY-PASSWORD-123','','', '0001-01-01T00:00:00.0000000+00:00','NONE','',0);
            INSERT INTO Accounts VALUES ('oa-1','g@example.test',1,'imap.gmail.com',993,'smtp.gmail.com',587,'','LEGACY-ACCESS-456','LEGACY-REFRESH-789','2026-10-19T12:00:00.0000000+00:00','SOCKS5','127.0.0.1',1080);";
        cmd.ExecuteNonQuery();
    }

    private List<(string Id, string Email, string Pw, string At, string Rt, long Ver, string? Auth, string Proxy)> Rows()
    {
        using var c = new SqliteConnection(Cs);
        c.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, Email, IFNULL(Password,''), IFNULL(OAuthToken,''), IFNULL(RefreshToken,''), IFNULL(CredentialVersion,0), AuthType, ProxyType FROM Accounts ORDER BY Id";
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, string, string, string, long, string?, string)>();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7)));
        return list;
    }

    [Fact]
    public async Task Migrate_KeepsAllAccounts_MovesSecrets_ClearsPlaintext()
    {
        CreateLegacyDb();
        var store = new InMemoryCredentialStore();
        var result = await new AccountCredentialMigrator(Cs, store).MigrateAsync();

        Assert.Equal(2, result.Migrated);
        Assert.Empty(result.FailedAccountIds);

        var rows = Rows();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => { Assert.Equal("", r.Pw); Assert.Equal("", r.At); Assert.Equal("", r.Rt); Assert.Equal(1, r.Ver); });
        Assert.Equal("OAUTH2", rows.Single(r => r.Id == "oa-1").Auth);
        Assert.Equal("PASSWORD", rows.Single(r => r.Id == "pw-1").Auth);
        Assert.Equal("SOCKS5", rows.Single(r => r.Id == "oa-1").Proxy); // 其它字段不动

        Assert.Equal("LEGACY-PASSWORD-123", store.Data[CredentialKeys.Password("pw-1")]);
        var tokens = OAuthTokenSet.Deserialize(store.Data[CredentialKeys.OAuthTokens("oa-1")]);
        Assert.Equal("LEGACY-REFRESH-789", tokens.RefreshToken);
        Assert.Equal("LEGACY-ACCESS-456", tokens.AccessToken);

        // VACUUM 之后数据库文件里不应再残留明文
        SqliteConnection.ClearAllPools();
        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(_dbPath));
        Assert.DoesNotContain("LEGACY-PASSWORD-123", raw);
        Assert.DoesNotContain("LEGACY-REFRESH-789", raw);
        Assert.DoesNotContain("LEGACY-ACCESS-456", raw);
    }

    [Fact]
    public async Task Migrate_IsIdempotent()
    {
        CreateLegacyDb();
        var store = new InMemoryCredentialStore();
        var m = new AccountCredentialMigrator(Cs, store);
        await m.MigrateAsync();
        var second = await m.MigrateAsync();
        Assert.Equal(0, second.Migrated);
        Assert.Equal(2, second.AlreadySecure);
        Assert.Equal("LEGACY-PASSWORD-123", store.Data[CredentialKeys.Password("pw-1")]);
    }

    [Fact]
    public async Task Migrate_StoreWriteFails_PlaintextKept_AccountsStillUsable()
    {
        CreateLegacyDb();
        var store = new InMemoryCredentialStore { FailWrites = true };
        var result = await new AccountCredentialMigrator(Cs, store).MigrateAsync();

        Assert.Equal(0, result.Migrated);
        Assert.Equal(2, result.FailedAccountIds.Count);
        var rows = Rows();
        Assert.Equal("LEGACY-PASSWORD-123", rows.Single(r => r.Id == "pw-1").Pw);
        Assert.Equal("LEGACY-REFRESH-789", rows.Single(r => r.Id == "oa-1").Rt);
        Assert.All(rows, r => Assert.Equal(0, r.Ver));
    }

    [Fact]
    public async Task Migrate_VerifyMismatch_PlaintextKept()
    {
        CreateLegacyDb();
        var store = new InMemoryCredentialStore { CorruptReads = true };
        var result = await new AccountCredentialMigrator(Cs, store).MigrateAsync();
        Assert.Equal(2, result.FailedAccountIds.Count);
        Assert.Equal("LEGACY-PASSWORD-123", Rows().Single(r => r.Id == "pw-1").Pw);
    }

    [Fact]
    public async Task Rollback_RestoresPlaintextForOldVersions()
    {
        CreateLegacyDb();
        var store = new InMemoryCredentialStore();
        var m = new AccountCredentialMigrator(Cs, store);
        await m.MigrateAsync();

        var n = await m.RollbackAsync();
        Assert.Equal(2, n);
        var rows = Rows();
        Assert.Equal("LEGACY-PASSWORD-123", rows.Single(r => r.Id == "pw-1").Pw);
        Assert.Equal("LEGACY-REFRESH-789", rows.Single(r => r.Id == "oa-1").Rt);
        Assert.All(rows, r => Assert.Equal(0, r.Ver));

        // 回滚后还能再次升级
        var again = await m.MigrateAsync();
        Assert.Equal(2, again.Migrated);
    }
}
