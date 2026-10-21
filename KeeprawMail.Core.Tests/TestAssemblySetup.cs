using System.Net;
using System.Runtime.CompilerServices;

namespace KeeprawMail.Core.Tests;

/// <summary>
/// 测试进程的全局网络设置。
///
/// 根因说明（Tls_RevokedCertificate_Rejected 在 Linux 上偶发通过/失败）：
/// Linux 上 .NET 的 X509Chain 用 HttpClient 下载 CRL，走的是进程级的 <see cref="HttpClient.DefaultProxy"/>，
/// 它在首次使用时从 HTTP_PROXY 等环境变量读取并缓存。ProxyRoutingTests 中有测试会临时把 HTTP_PROXY
/// 设为一个"陷阱"代理；若它与 TLS 测试并行、且恰好在 DefaultProxy 首次初始化时生效，之后整个进程的
/// CRL 下载都会发往陷阱/已关闭的代理 → 链状态变成 RevocationStatusUnknown + OfflineRevocation →
/// 按既定策略软失败放行，于是"已吊销证书"被接受。Windows 用 CryptoAPI(WinHTTP) 下载 CRL，不受影响。
///
/// 修复：测试进程中 CRL 下载固定为直连（本地 127.0.0.1 的 CRL 服务），不受环境变量与测试顺序影响；
/// 修改环境代理的测试放到不并行的集合中，并在结束时恢复。
/// </summary>
internal static class TestAssemblySetup
{
    [ModuleInitializer]
    internal static void Init()
    {
        HttpClient.DefaultProxy = new WebProxy(); // 无代理地址 ⇒ 所有请求直连
    }
}

/// <summary>会修改进程级环境（环境变量、HttpClient.DefaultProxy）的测试，禁止与其它测试并行。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "ProcessEnvironment";
}
