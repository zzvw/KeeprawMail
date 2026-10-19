namespace KeeprawMail.Core.Auth;

/// <summary>OAuth 相关异常基类。Message 面向用户，绝不包含 Token。</summary>
public class OAuthException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>用户在浏览器里点了"取消/拒绝"（error=access_denied）。</summary>
public sealed class OAuthUserCancelledException()
    : OAuthException("你在浏览器中取消了授权。");

/// <summary>授权服务器返回错误，或回调参数不合法（state 不匹配等）。</summary>
public sealed class OAuthAuthorizationException(string error, string? description = null)
    : OAuthException(string.IsNullOrEmpty(description) ? $"授权失败：{error}" : $"授权失败：{error}（{description}）")
{
    public string Error { get; } = error;
}

/// <summary>等待浏览器回调超时。</summary>
public sealed class OAuthTimeoutException(TimeSpan timeout)
    : OAuthException($"等待浏览器授权超时（{(int)timeout.TotalMinutes} 分钟）。请重新点击登录。");

/// <summary>
/// Refresh Token 已失效（被撤销、过期、测试模式 7 天到期、修改了密码等），必须重新登录。
/// </summary>
public sealed class OAuthReauthorizationRequiredException(string accountHint, string? reason = null)
    : OAuthException($"账号 {accountHint} 的授权已失效，请删除后重新登录。" + (reason is null ? "" : $"（{reason}）"));

/// <summary>Token 端点 / UserInfo 端点返回了无法理解的响应或网络错误。</summary>
public sealed class OAuthProtocolException(string message, Exception? inner = null) : OAuthException(message, inner);
