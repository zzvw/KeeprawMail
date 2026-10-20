using System.Collections.Concurrent;
using KeeprawMail.Core.Security;

namespace KeeprawMail.Core.Tests.Fakes;

public sealed class InMemoryCredentialStore : ICredentialStore
{
    public ConcurrentDictionary<string, string> Data { get; } = new();
    public int Writes;
    public bool FailWrites;
    public bool CorruptReads;
    public bool FailDeletes;

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Data.TryGetValue(key, out var v) ? (CorruptReads ? v + "x" : v) : null);

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        if (FailWrites) throw new IOException("disk full");
        Interlocked.Increment(ref Writes);
        Data[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        if (FailDeletes) throw new IOException("credential store locked");
        Data.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

/// <summary>测试用"加密"：逐字节异或，仅用于验证存储层不写明文。生产环境为 DPAPI。</summary>
public sealed class XorProtector : ISecretProtector
{
    public byte[] Protect(byte[] p) => p.Select(b => (byte)(b ^ 0x5A)).ToArray();
    public byte[] Unprotect(byte[] c) => c.Select(b => (byte)(b ^ 0x5A)).ToArray();
}
