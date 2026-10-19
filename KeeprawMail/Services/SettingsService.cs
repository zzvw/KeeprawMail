using KeeprawMail.Data;
using Microsoft.UI.Xaml;
using System;
using System.Threading.Tasks;

namespace KeeprawMail.Services
{
    /// <summary>
    /// 代理配置模型
    /// </summary>
    public class ProxyConfig
    {
        /// <summary>
        /// 代理类型：NONE, HTTP, SOCKS5
        /// </summary>
        public string Type { get; set; } = "NONE";

        /// <summary>
        /// 代理主机地址
        /// </summary>
        public string Host { get; set; } = string.Empty;

        /// <summary>
        /// 代理端口
        /// </summary>
        public int Port { get; set; } = 0;
    }

    /// <summary>
    /// 设置管理服务
    /// 负责应用级设置的读取、保存和管理
    /// </summary>
    public class SettingsService
    {
        private readonly DatabaseContext _dbContext;

        // 设置键常量
        private const string ThemeKey = "AppTheme";
        private const string GlobalProxyTypeKey = "GlobalProxyType";
        private const string GlobalProxyHostKey = "GlobalProxyHost";
        private const string GlobalProxyPortKey = "GlobalProxyPort";

        /// <summary>
        /// 主题变更事件
        /// </summary>
        public event EventHandler<ElementTheme>? ThemeChanged;

        public SettingsService(DatabaseContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <summary>
        /// 获取应用主题设置
        /// </summary>
        /// <returns>主题类型，默认为 Default（跟随系统）</returns>
        public async Task<ElementTheme> GetThemeAsync()
        {
            var themeValue = await _dbContext.GetSettingAsync(ThemeKey);
            
            // 如果未设置，返回默认值（跟随系统）
            if (string.IsNullOrEmpty(themeValue))
                return ElementTheme.Default;

            // 解析枚举值（Native AOT 安全）
            return themeValue switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }

        /// <summary>
        /// 保存应用主题设置
        /// </summary>
        /// <param name="theme">主题类型</param>
        public async Task SaveThemeAsync(ElementTheme theme)
        {
            // 枚举转字符串（Native AOT 安全）
            var themeValue = theme switch
            {
                ElementTheme.Light => "Light",
                ElementTheme.Dark => "Dark",
                _ => "Default"
            };

            await _dbContext.SaveSettingAsync(ThemeKey, themeValue);

            // 触发主题变更事件
            ThemeChanged?.Invoke(this, theme);
        }

        /// <summary>
        /// 获取全局代理配置
        /// </summary>
        /// <returns>代理配置对象</returns>
        public async Task<ProxyConfig> GetGlobalProxyAsync()
        {
            var proxyType = await _dbContext.GetSettingAsync(GlobalProxyTypeKey);
            var proxyHost = await _dbContext.GetSettingAsync(GlobalProxyHostKey);
            var proxyPortStr = await _dbContext.GetSettingAsync(GlobalProxyPortKey);

            // 解析端口号
            int.TryParse(proxyPortStr, out var proxyPort);

            return new ProxyConfig
            {
                Type = proxyType ?? "NONE",
                Host = proxyHost ?? string.Empty,
                Port = proxyPort
            };
        }

        /// <summary>
        /// 保存全局代理配置
        /// </summary>
        /// <param name="proxy">代理配置对象</param>
        public async Task SaveGlobalProxyAsync(ProxyConfig proxy)
        {
            await _dbContext.SaveSettingAsync(GlobalProxyTypeKey, proxy.Type);
            await _dbContext.SaveSettingAsync(GlobalProxyHostKey, proxy.Host);
            await _dbContext.SaveSettingAsync(GlobalProxyPortKey, proxy.Port.ToString());
        }
    }
}
