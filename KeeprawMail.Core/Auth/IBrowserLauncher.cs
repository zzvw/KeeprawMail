namespace KeeprawMail.Core.Auth;

/// <summary>
/// 打开系统默认浏览器。
///
/// 注意：外部浏览器是独立进程，使用它自己的（通常是系统）代理设置。
/// 本应用的代理配置只作用于应用自身发出的请求（Token 交换、刷新、UserInfo、IMAP、SMTP），
/// 无法也不会控制外部浏览器访问 accounts.google.com 时是否走代理。
/// 浏览器回调地址是 127.0.0.1，本机回环，不经过任何代理。
/// </summary>
public interface IBrowserLauncher
{
    void Open(Uri url);
}
