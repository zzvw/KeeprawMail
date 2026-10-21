using System.Text;
using System.Text.Json;

namespace KeeprawMail.Core.Auth;

/// <summary>
/// 一个账号的 OAuth Token 集合。作为一个整体加密保存，保证三者原子更新。
/// ToString() 不输出任何 Token。
/// </summary>
public sealed record OAuthTokenSet(string RefreshToken, string? AccessToken, DateTimeOffset AccessTokenExpiresAt)
{
    public bool HasUsableAccessToken(DateTimeOffset now, TimeSpan skew) =>
        !string.IsNullOrEmpty(AccessToken) && AccessTokenExpiresAt - skew > now;

    /// <summary>手写序列化（Native AOT 安全，不依赖反射）。</summary>
    public string Serialize()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("refresh_token", RefreshToken);
            w.WriteString("access_token", AccessToken ?? string.Empty);
            w.WriteString("expires_at", AccessTokenExpiresAt.ToString("o"));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static OAuthTokenSet Deserialize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var rt = root.GetProperty("refresh_token").GetString() ?? string.Empty;
        var at = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        var exp = root.TryGetProperty("expires_at", out var e) && DateTimeOffset.TryParse(e.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
        return new OAuthTokenSet(rt, string.IsNullOrEmpty(at) ? null : at, exp);
    }

    public override string ToString() =>
        $"OAuthTokenSet(refresh=***, access={(AccessToken is null ? "none" : "***")}, expiresAt={AccessTokenExpiresAt:o})";
}
