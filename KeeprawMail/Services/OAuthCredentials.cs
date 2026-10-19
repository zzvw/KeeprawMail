using System;
using System.IO;
using System.Text.Json;

namespace KeeprawMail.Services
{
    /// <summary>
    /// 解析 Google OAuth 客户端凭据。
    ///
    /// 凭据绝不写入源码（会被提交到仓库并触发 GitHub Secret Scanning 拦截）。
    /// 按以下优先级解析：
    ///   1. 环境变量 KEEPRAWMAIL_GOOGLE_CLIENT_ID / KEEPRAWMAIL_GOOGLE_CLIENT_SECRET
    ///   2. %LOCALAPPDATA%\KeeprawMail\oauth.credentials.json
    ///
    /// 两种方式都不会把凭据写入版本库。
    /// </summary>
    public static class OAuthCredentials
    {
        private const string ClientIdVariable = "KEEPRAWMAIL_GOOGLE_CLIENT_ID";
        private const string ClientSecretVariable = "KEEPRAWMAIL_GOOGLE_CLIENT_SECRET";

        private const string MissingClientIdMessage =
            "未配置 Google OAuth Client ID。请设置环境变量 " + ClientIdVariable +
            "，或创建文件 %LOCALAPPDATA%\\KeeprawMail\\oauth.credentials.json" +
            "（内容为 {\"clientId\":\"...\",\"clientSecret\":\"...\"}）。" +
            "获取方式见 docs/HowToGetGmailClientId.md。";

        private static readonly Lazy<string> LazyClientId = new(ResolveClientId);
        private static readonly Lazy<string> LazyClientSecret = new(ResolveClientSecret);

        /// <summary>Google OAuth 客户端 ID。</summary>
        public static string ClientId => LazyClientId.Value;

        /// <summary>
        /// Google OAuth 客户端密钥。桌面应用通常可以留空，返回空字符串表示不发送该参数。
        /// </summary>
        public static string ClientSecret => LazyClientSecret.Value;

        /// <summary>客户端 ID 是否已配置。</summary>
        public static bool IsConfigured => ClientId.Length > 0;

        private static string ResolveClientId() =>
            Read(Environment.GetEnvironmentVariable(ClientIdVariable), "clientId") ?? "";

        private static string ResolveClientSecret() =>
            Read(Environment.GetEnvironmentVariable(ClientSecretVariable), "clientSecret") ?? "";

        /// <summary>
        /// 先取环境变量，取不到再回退到本地凭据文件。
        /// </summary>
        private static string? Read(string? fromEnvironment, string jsonKey)
        {
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
                return fromEnvironment.Trim();

            try
            {
                var path = GetCredentialsFilePath();
                if (!File.Exists(path))
                    return null;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return null;

                return doc.RootElement.TryGetProperty(jsonKey, out var value)
                    ? value.GetString()
                    : null;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string GetCredentialsFilePath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeeprawMail",
            "oauth.credentials.json");

        /// <summary>
        /// 确保凭据已配置，未配置时抛出带操作指引的异常。
        /// </summary>
        public static void EnsureConfigured()
        {
            if (!IsConfigured)
                throw new InvalidOperationException(MissingClientIdMessage);
        }
    }
}