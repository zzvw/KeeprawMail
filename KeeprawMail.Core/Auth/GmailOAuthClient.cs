using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KeeprawMail.Core.Networking;

namespace KeeprawMail.Core.Auth;

/// <summary>登录结果。</summary>
public sealed record GmailLoginResult(string Email, OAuthTokenSet Tokens);

/// <summary>刷新结果。Google 可能轮换 refresh_token，此时 NewRefreshToken 非空。</summary>
public sealed record TokenRefreshResult(string AccessToken, DateTimeOffset ExpiresAt, string? NewRefreshToken);

/// <summary>Token 刷新抽象（便于 TokenManager 测试与将来接入其他提供商）。</summary>
public interface ITokenRefresher
{
    Task<TokenRefreshResult> RefreshAsync(string refreshToken, ProxyEndpoint proxy, CancellationToken cancellationToken);
}

/// <summary>
/// Gmail OAuth 2.0（已安装应用 + 回环重定向 + PKCE S256）。
///
/// 流程：
///  1. 生成 code_verifier / code_challenge(S256) / 随机 state；
///  2. 在 127.0.0.1 的随机端口上监听回调；
///  3. 用系统默认浏览器打开授权页（浏览器的代理行为见 <see cref="IBrowserLauncher"/>）；
///  4. 等待回调：校验路径与 state，处理 error=access_denied（用户取消）与其它错误；
///     支持超时与 CancellationToken；
///  5. 经由账号的生效代理，用 code + code_verifier 换 Token，并请求 UserInfo 取邮箱。
/// </summary>
public sealed class GmailOAuthClient : ITokenRefresher
{
    private readonly GmailOAuthOptions _options;
    private readonly Func<ProxyEndpoint, HttpClient> _httpClientFactory;
    private readonly IBrowserLauncher _browser;
    private readonly TimeProvider _time;

    public GmailOAuthClient(GmailOAuthOptions options, Func<ProxyEndpoint, HttpClient> httpClientFactory, IBrowserLauncher browser, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ClientId))
            throw new ArgumentException("未配置 Gmail OAuth Client ID。", nameof(options));
        _options = options;
        _httpClientFactory = httpClientFactory;
        _browser = browser;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>构建授权 URL（internal 以便测试）。</summary>
    internal Uri BuildAuthorizationUrl(string redirectUri, string state, string codeChallenge, string? loginHint)
    {
        var q = new List<KeyValuePair<string, string>>
        {
            new("client_id", _options.ClientId),
            new("redirect_uri", redirectUri),
            new("response_type", "code"),
            new("scope", _options.Scopes),
            new("state", state),
            new("code_challenge", codeChallenge),
            new("code_challenge_method", "S256"),
            new("access_type", "offline"),
            // 强制显示同意页，保证总能拿到 refresh_token（Google 仅在首次同意时返回）
            new("prompt", "consent"),
        };
        if (!string.IsNullOrWhiteSpace(loginHint))
            q.Add(new("login_hint", loginHint));

        var query = string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return new Uri($"{_options.AuthorizationEndpoint}?{query}");
    }

    /// <summary>
    /// 交互式登录。proxy 只影响 Token 交换和 UserInfo 请求，不影响外部浏览器。
    /// </summary>
    public async Task<GmailLoginResult> LoginAsync(ProxyEndpoint proxy, string? loginHint = null, CancellationToken cancellationToken = default)
    {
        proxy.Validate();

        var verifier = Pkce.CreateVerifier();
        var challenge = Pkce.CreateChallenge(verifier);
        var state = Pkce.CreateState();

        var port = GetFreeLoopbackPort();
        var redirectUri = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        using var timeoutCts = new CancellationTokenSource(_options.LoginTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        string code;
        try
        {
            _browser.Open(BuildAuthorizationUrl(redirectUri, state, challenge, loginHint));
            code = await WaitForCallbackAsync(listener, state, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new OAuthTimeoutException(_options.LoginTimeout);
        }
        finally
        {
            try { listener.Stop(); } catch (ObjectDisposedException) { }
        }

        var http = _httpClientFactory(proxy);
        var tokens = await ExchangeCodeAsync(http, code, verifier, redirectUri, cancellationToken).ConfigureAwait(false);
        var email = await GetEmailAsync(http, tokens.AccessToken!, cancellationToken).ConfigureAwait(false);
        return new GmailLoginResult(email, tokens);
    }

    private static async Task<string> WaitForCallbackAsync(HttpListener listener, string expectedState, CancellationToken ct)
    {
        // HttpListener.GetContextAsync 不接受 CancellationToken：取消时停止监听器以解除等待
        await using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });

        while (true)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                ct.ThrowIfCancellationRequested();
                throw new OAuthProtocolException("本地回调监听意外中断。", ex);
            }

            var req = ctx.Request;
            // 只接受根路径的回调；favicon 等其它请求直接 404 并继续等待
            if (req.Url is null || req.Url.AbsolutePath != "/")
            {
                Respond(ctx, 404, "Not found");
                continue;
            }

            var qs = req.QueryString;
            var state = qs["state"];
            var error = qs["error"];
            var code = qs["code"];

            // 先校验 state：不匹配的请求可能来自其它网页的伪造，忽略且不结束登录流程
            if (!Pkce.StateEquals(expectedState, state))
            {
                Respond(ctx, 400, "登录请求校验失败（state 不匹配），已忽略。请回到 Keepraw Mail。");
                continue;
            }

            if (!string.IsNullOrEmpty(error))
            {
                Respond(ctx, 200, error == "access_denied" ? "已取消授权，可以关闭此页面。" : "授权失败，可以关闭此页面并回到 Keepraw Mail 查看原因。");
                if (error == "access_denied")
                    throw new OAuthUserCancelledException();
                throw new OAuthAuthorizationException(error, qs["error_description"]);
            }

            if (string.IsNullOrEmpty(code))
            {
                Respond(ctx, 400, "回调缺少授权码。");
                throw new OAuthAuthorizationException("missing_code");
            }

            Respond(ctx, 200, "登录成功，可以关闭此页面并回到 Keepraw Mail。");
            return code;
        }
    }

    private static void Respond(HttpListenerContext ctx, int status, string message)
    {
        try
        {
            var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Keepraw Mail</title></head><body><h3>{WebUtility.HtmlEncode(message)}</h3></body></html>";
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.OutputStream.Close();
        }
        catch
        {
            // 浏览器提前关闭不影响登录结果
        }
    }

    private async Task<OAuthTokenSet> ExchangeCodeAsync(HttpClient http, string code, string verifier, string redirectUri, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        };
        AddSecret(form);

        using var root = await PostTokenAsync(http, form, ct).ConfigureAwait(false);
        var r = root.RootElement;
        var access = ReadRequired(r, "access_token");
        var refresh = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        if (string.IsNullOrEmpty(refresh))
            throw new OAuthProtocolException("Google 没有返回 Refresh Token。请在 Google 账号的“第三方应用访问权限”中移除本应用后重试。");

        return new OAuthTokenSet(refresh, access, ComputeExpiry(r));
    }

    public async Task<TokenRefreshResult> RefreshAsync(string refreshToken, ProxyEndpoint proxy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);
        proxy.Validate();

        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        };
        AddSecret(form);

        var http = _httpClientFactory(proxy);
        using var doc = await PostTokenAsync(http, form, cancellationToken).ConfigureAwait(false);
        var r = doc.RootElement;
        var access = ReadRequired(r, "access_token");
        var rotated = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        return new TokenRefreshResult(access, ComputeExpiry(r), string.IsNullOrEmpty(rotated) ? null : rotated);
    }

    private void AddSecret(Dictionary<string, string> form)
    {
        if (!string.IsNullOrEmpty(_options.ClientSecret))
            form["client_secret"] = _options.ClientSecret;
    }

    private DateTimeOffset ComputeExpiry(JsonElement r)
    {
        var expiresIn = r.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
        return _time.GetUtcNow().AddSeconds(expiresIn);
    }

    private async Task<JsonDocument> PostTokenAsync(HttpClient http, Dictionary<string, string> form, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsync(_options.TokenEndpoint, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OAuthProtocolException($"无法连接 Google Token 服务：{ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OAuthProtocolException("连接 Google Token 服务超时。", ex);
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(body); } catch (JsonException) { }

            if (resp.IsSuccessStatusCode && doc is not null)
                return doc;

            // 只提取 error / error_description，不把原始响应（可能含 Token）写入异常
            string? error = null, desc = null;
            if (doc is not null)
            {
                if (doc.RootElement.TryGetProperty("error", out var e)) error = e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString();
                if (doc.RootElement.TryGetProperty("error_description", out var d)) desc = d.GetString();
                doc.Dispose();
            }

            if (error == "invalid_grant")
                throw new OAuthReauthorizationRequiredException("(Gmail)", desc);

            throw new OAuthAuthorizationException(error ?? $"http_{(int)resp.StatusCode}", desc);
        }
    }

    private async Task<string> GetEmailAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _options.UserInfoEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OAuthProtocolException($"无法获取 Google 用户信息：{ex.Message}", ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new OAuthProtocolException($"获取 Google 用户信息失败（HTTP {(int)resp.StatusCode}）。");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var email = doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
            if (string.IsNullOrWhiteSpace(email))
                throw new OAuthProtocolException("Google 用户信息中没有邮箱地址（请确认已授予 email 权限）。");
            return email;
        }
    }

    private static string ReadRequired(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.GetString() is { Length: > 0 } s
            ? s
            : throw new OAuthProtocolException($"Token 响应缺少 {name}。");

    private static int GetFreeLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }
}
