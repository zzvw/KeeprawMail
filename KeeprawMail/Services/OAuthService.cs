using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace KeeprawMail.Services
{
    /// <summary>
    /// OAuth2 服务
    /// 负责处理 Gmail 的 OAuth2 认证流程
    /// </summary>
    public class OAuthService
    {
        // Gmail Client ID (由用户提供)
        private const string GmailClientId = "REDACTED_CLIENT_ID";
        // Gmail Client Secret (桌面应用通常不需要，或者使用空字符串)
        private const string GmailClientSecret = "REDACTED_CLIENT_SECRET"; // 用户提供的 Secret
        
        private const string GoogleAuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        private const string GoogleTokenEndpoint = "https://oauth2.googleapis.com/token";

        // 需要的权限范围
        private const string Scopes = "email profile https://mail.google.com/";

        /// <summary>
        /// 登录 Gmail 并获取 Token
        /// </summary>
        public async Task<(string AccessToken, string RefreshToken, DateTimeOffset Expiration, string Email)> LoginGmailAsync(string proxyType = "NONE", string proxyHost = "", int proxyPort = 0)
        {
            // 1. 获取一个随机的本地端口用于回调
            var port = GetRandomUnusedPort();
            var redirectUri = $"http://127.0.0.1:{port}/";

            // 2. 创建 HttpListener 监听回调
            using var listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);
            listener.Start();

            // 3. 构建认证 URL
            var authUrl = $"{GoogleAuthEndpoint}?" +
                          $"client_id={GmailClientId}&" +
                          $"redirect_uri={Uri.EscapeDataString(redirectUri)}&" +
                          $"response_type=code&" +
                          $"scope={Uri.EscapeDataString(Scopes)}&" +
                          $"access_type=offline"; // offline 获取 Refresh Token

            // 4. 打开系统默认浏览器
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            // 5. 等待回调
            var context = await listener.GetContextAsync();
            var request = context.Request;
            var response = context.Response;

            // 6. 解析 Code
            var code = request.QueryString["code"];
            var error = request.QueryString["error"];

            // 7. 返回响应给浏览器
            var responseString = "<html><body><h2>Login Successful!</h2><p>You can close this window and return to Keepraw Mail.</p></body></html>";
            if (!string.IsNullOrEmpty(error))
            {
                responseString = $"<html><body><h2>Login Failed</h2><p>Error: {error}</p></body></html>";
            }
            var buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;
            var output = response.OutputStream;
            output.Write(buffer, 0, buffer.Length);
            output.Close();
            listener.Stop();

            if (string.IsNullOrEmpty(code))
            {
                throw new Exception($"OAuth failed: {error}");
            }

            // 8. 使用 Code 换取 Token (使用代理)
            return await ExchangeCodeForTokenAsync(code, redirectUri, proxyType, proxyHost, proxyPort);
        }

        private async Task<(string AccessToken, string RefreshToken, DateTimeOffset Expiration, string Email)> ExchangeCodeForTokenAsync(string code, string redirectUri, string proxyType, string proxyHost, int proxyPort)
        {
            HttpClient client = CreateHttpClient(proxyType, proxyHost, proxyPort);

            using (client)
            {
                var values = new Dictionary<string, string>
                {
                    { "client_id", GmailClientId },
                    { "client_secret", GmailClientSecret },
                    { "code", code },
                    { "redirect_uri", redirectUri },
                    { "grant_type", "authorization_code" }
                };

                var content = new FormUrlEncodedContent(values);
                var response = await client.PostAsync(GoogleTokenEndpoint, content);
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Failed to exchange code for token: {json}");
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var accessToken = root.GetProperty("access_token").GetString() ?? "";
                var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
                var expiresIn = root.GetProperty("expires_in").GetInt32();

                // 获取用户信息 (Email)
                var email = await GetUserInfoAsync(accessToken, client);

                return (accessToken, refreshToken, DateTimeOffset.Now.AddSeconds(expiresIn), email);
            }
        }

        private async Task<string> GetUserInfoAsync(string accessToken, HttpClient client)
        {
            // 使用 Google UserInfo API
            var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v2/userinfo");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return "";

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("email").GetString() ?? "";
        }

        /// <summary>
        /// 刷新 Access Token
        /// </summary>
        public async Task<(string accessToken, int expiresInSeconds)> RefreshAccessTokenAsync(string refreshToken, string proxyType = "NONE", string proxyHost = "", int proxyPort = 0)
        {
            System.Diagnostics.Debug.WriteLine($"[OAuthService] Refreshing token. Proxy: {proxyType}://{proxyHost}:{proxyPort}");
            HttpClient client = CreateHttpClient(proxyType, proxyHost, proxyPort);

            using (client)
            {
                var values = new Dictionary<string, string>
                {
                    { "client_id", GmailClientId },
                    { "client_secret", GmailClientSecret },
                    { "refresh_token", refreshToken },
                    { "grant_type", "refresh_token" }
                };

                var content = new FormUrlEncodedContent(values);
                var response = await client.PostAsync(GoogleTokenEndpoint, content);
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Failed to refresh token: {json}");
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var accessToken = root.GetProperty("access_token").GetString();
                var expiresIn = root.GetProperty("expires_in").GetInt32();

                return (accessToken ?? "", expiresIn);
            }
        }

        /// <summary>
        /// 创建配置了代理的 HttpClient
        /// </summary>
        private HttpClient CreateHttpClient(string proxyType, string proxyHost, int proxyPort)
        {
            if (proxyType == "HTTP" && !string.IsNullOrEmpty(proxyHost) && proxyPort > 0)
            {
                var proxy = new WebProxy($"http://{proxyHost}:{proxyPort}");
                var handler = new HttpClientHandler 
                { 
                    Proxy = proxy,
                    UseProxy = true
                };
                return new HttpClient(handler);
            }
            else if (proxyType == "SOCKS5")
            {
                // HttpClient 不原生支持 SOCKS5 协议
                // 但 v2rayN/Clash 的 Mixed Port 可以同时处理 HTTP 和 SOCKS5 请求
                // 因此我们将 SOCKS5 配置当作 HTTP 代理使用（仅适用于 Mixed Port）
                System.Diagnostics.Debug.WriteLine($"[OAuthService] Treating SOCKS5 as HTTP proxy (Mixed Port compatible): {proxyHost}:{proxyPort}");
                
                var proxy = new WebProxy($"http://{proxyHost}:{proxyPort}");
                var handler = new HttpClientHandler 
                { 
                    Proxy = proxy,
                    UseProxy = true
                };
                return new HttpClient(handler);
            }
            
            // NONE 或其他情况
            return new HttpClient();
        }

        private static int GetRandomUnusedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
