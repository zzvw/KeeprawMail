namespace KeeprawMail.Core.Networking;

/// <summary>
/// 把"全局代理 + 账号代理"解析为最终生效的 <see cref="ProxyEndpoint"/>。
/// 所有网络出口（IMAP、SMTP、OAuth Token Exchange、Token Refresh、UserInfo）都必须先经过这里。
/// </summary>
public static class ProxyResolver
{
    public static ProxyEndpoint Resolve(ProxySettings global, AccountProxySettings? account)
    {
        ArgumentNullException.ThrowIfNull(global);
        account ??= AccountProxySettings.Inherit;

        var effective = account.Mode switch
        {
            AccountProxyMode.Inherit => global.Global,
            AccountProxyMode.Direct => ProxyEndpoint.Direct,
            AccountProxyMode.Override => account.Override
                ?? throw new ProxyConfigurationException("账号选择了自定义代理，但没有填写代理地址。"),
            _ => throw new ProxyConfigurationException($"未知的账号代理模式：{account.Mode}"),
        };

        return effective.Validate();
    }
}
