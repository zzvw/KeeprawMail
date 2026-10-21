using System.Text;
using KeeprawMail.Core.Auth;
using KeeprawMail.Core.Networking;
using KeeprawMail.Core.Security;
using KeeprawMail.Core.Storage;
using KeeprawMail.Core.Tests.Fakes;
using Microsoft.Data.Sqlite;

namespace KeeprawMail.Core.Tests;

/// <summary>
/// 集成测试：注入临时目录的文件凭据存储（Windows 上用真实 DPAPI），
/// 通过 AccountRepository + AccountSecretsService 完整走"保存 / 读取 / 更新 / 重新授权 / 删除"。
/// 同时确认不会写入全局真实用户目录（%LOCALAPPDATA%\KeeprawMail\credentials）。
/// </summary>
public class AccountRepositoryIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "krm-it-" + Guid.NewGuid().ToString("N"));
    private readonly string _credDir;
    private readonly string _dbPath;
    private readonly FakeOAuthServer _oauth = new();
    private readonly NetworkClientFactory _factory = new();
    private readonly string[] _globalBefore;

    public AccountRepositoryIntegrationTests()
    {
        _credDir = Path.Combine(_root, "credentials");
        _dbPath = Path.Combine(_root, "keepraw.db");
        Directory.CreateDirectory(_root);
        _globalBefore = SnapshotGlobal();
    }

    private string Cs => $"Data Source={_dbPath};Pooling=False";

    private static string[] SnapshotGlobal() =>
        Directory.Exists(ProtectedFileCredentialStore.DefaultDirectory)
            ? Directory.GetFiles(ProtectedFileCredentialStore.DefaultDirectory).OrderBy(x => x).ToArray()
            : [];

    private static ISecretProtector Protector() =>
#pragma warning disable CA1416
        OperatingSystem.IsWindows() ? new DpapiSecretProtector() : new XorProtector();
#pragma warning restore CA1416

    private (AccountRepository Repo, AccountSecretsService Secrets) Build(ICredentialStore? store = null)
    {
        var client = new GmailOAuthClient(new GmailOAuthOptions
        {
            ClientId = "cid",
            TokenEndpoint = new Uri($"http://127.0.0.1:{_oauth.Port}/token"),
            UserInfoEndpoint = new Uri($"http://127.0.0.1:{_oauth.Port}/userinfo"),
        }, _factory.CreateHttpClient, new FakeBrowser());
        var secrets = new AccountSecretsService(store ?? new ProtectedFileCredentialStore(_credDir, Protector()), client);
        return (new AccountRepository(Cs, secrets), secrets);
    }

    public void Dispose()
    {
        // 关键断言：全局用户目录没有被写入
        Assert.Equal(_globalBefore, SnapshotGlobal());
        _oauth.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private string DbText()
    {
        SqliteConnection.ClearAllPools();
        return Encoding.UTF8.GetString(File.ReadAllBytes(_dbPath));
    }

    [Fact]
    public async Task OAuthAccount_Save_Read_Update_Reauthorize_Delete()
    {
        var (repo, secrets) = Build();
        await repo.EnsureSchemaAsync();

        // 保存
        var acc = new AccountRecord
        {
            Id = "acc-1",
            Email = "user@example.test",
            Provider = 1,
            ImapServer = "imap.gmail.com",
            SmtpServer = "smtp.gmail.com",
            SmtpPort = 587,
            ProxyType = "SOCKS5",
            ProxyHost = "127.0.0.1",
            ProxyPort = 1080,
            PendingOAuthTokens = new OAuthTokenSet("REFRESH-AAA", "ACCESS-AAA", DateTimeOffset.UtcNow.AddHours(1)),
        };
        await repo.SaveAsync(acc);
        Assert.Null(acc.PendingOAuthTokens);
        Assert.NotEmpty(Directory.GetFiles(_credDir));
        Assert.DoesNotContain("REFRESH-AAA", DbText());
        Assert.DoesNotContain("ACCESS-AAA", DbText());

        // 读取
        var loaded = Assert.Single(await repo.GetAllAsync());
        Assert.Equal(AccountAuthTypes.OAuth2, loaded.AuthType);
        Assert.Equal(AccountCredentialMigrator.SecureVersion, loaded.CredentialVersion);
        Assert.Null(loaded.PendingOAuthTokens);
        Assert.Equal("ACCESS-AAA", await secrets.Tokens.GetAccessTokenAsync("acc-1", loaded.Email, ProxyEndpoint.Direct));

        // 更新（只改代理，不应删掉 Token）
        loaded.ProxyType = "GLOBAL"; loaded.ProxyHost = ""; loaded.ProxyPort = 0;
        await repo.SaveAsync(loaded);
        var updated = Assert.Single(await repo.GetAllAsync());
        Assert.Equal("GLOBAL", updated.ProxyType);
        Assert.Equal("ACCESS-AAA", await secrets.Tokens.GetAccessTokenAsync("acc-1", updated.Email, ProxyEndpoint.Direct));

        // 重新授权：新 Token 写入同一个 AccountId（同一套 secrets / TokenManager）
        await secrets.SaveOAuthTokensAsync("acc-1", new OAuthTokenSet("REFRESH-BBB", "ACCESS-BBB", DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Equal("ACCESS-BBB", await secrets.Tokens.GetAccessTokenAsync("acc-1", updated.Email, ProxyEndpoint.Direct));
        Assert.Equal("acc-1", Assert.Single(await repo.GetAllAsync()).Id);

        // 删除
        await repo.DeleteAsync("acc-1");
        Assert.Empty(await repo.GetAllAsync());
        Assert.Empty(Directory.GetFiles(_credDir));
    }

    [Fact]
    public async Task PasswordAccount_RoundTrip_AndUnreadableCredentialIsFlagged()
    {
        var (repo, _) = Build();
        await repo.EnsureSchemaAsync();
        await repo.SaveAsync(new AccountRecord { Id = "pw-1", Email = "qq@example.test", Password = "AUTH-CODE-123" });
        Assert.DoesNotContain("AUTH-CODE-123", DbText());
        Assert.Equal("AUTH-CODE-123", Assert.Single(await repo.GetAllAsync()).Password);

        // 模拟换了 Windows 用户：凭据无法解密 → 账号保留，并标记为不可读
        foreach (var f in Directory.GetFiles(_credDir)) await File.WriteAllBytesAsync(f, "KRC1garbage"u8.ToArray());
        var (repo2, _) = Build(new ProtectedFileCredentialStore(_credDir, new ThrowingProtector()));
        var acc = Assert.Single(await repo2.GetAllAsync());
        Assert.True(acc.CredentialUnreadable);
        Assert.Equal("qq@example.test", acc.Email);
    }

    [Fact]
    public async Task FailedMigration_AccountStillUsable_AndRecoversOnNextSave()
    {
        // 旧版数据库
        using (var c = new SqliteConnection(Cs))
        {
            c.Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = AccountRepository.CreateTableSql + @"
                INSERT INTO Accounts VALUES ('oa-1','g@example.test',1,'imap.gmail.com',993,'smtp.gmail.com',587,'','LEGACY-ACCESS','LEGACY-REFRESH','2026-10-19T12:00:00.0000000+00:00','NONE','',0);";
            cmd.ExecuteNonQuery();
        }

        // 1) 安全存储写入失败 → 迁移失败，账号原样保留
        var broken = new InMemoryCredentialStore { FailWrites = true };
        var (brokenRepo, brokenSecrets) = Build(broken);
        var result = await brokenSecrets.CreateMigrator(Cs).MigrateAsync();
        Assert.Equal(["oa-1"], result.FailedAccountIds);
        var legacy = Assert.Single(await brokenRepo.GetAllAsync());
        Assert.Equal(AccountCredentialMigrator.LegacyVersion, legacy.CredentialVersion);
        Assert.Equal("LEGACY-REFRESH", legacy.PendingOAuthTokens!.RefreshToken); // 仍可用明文兜底

        // 2) 存储恢复后，下次启动迁移成功
        var (repo, secrets) = Build();
        var again = await secrets.CreateMigrator(Cs).MigrateAsync();
        Assert.Equal(1, again.Migrated);
        var migrated = Assert.Single(await repo.GetAllAsync());
        Assert.Equal(AccountCredentialMigrator.SecureVersion, migrated.CredentialVersion);
        Assert.Equal("g@example.test", migrated.Email);
        Assert.DoesNotContain("LEGACY-REFRESH", DbText());
    }

    private sealed class ThrowingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] p) => throw new System.Security.Cryptography.CryptographicException("cannot protect");
        public byte[] Unprotect(byte[] c) => throw new System.Security.Cryptography.CryptographicException("cannot unprotect");
    }
}
