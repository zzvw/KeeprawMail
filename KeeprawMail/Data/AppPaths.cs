using KeeprawMail.Core.Storage;
using System;
using System.IO;

namespace KeeprawMail.Data
{
    /// <summary>应用本地目录（%LOCALAPPDATA%\KeeprawMail\...）。</summary>
    public static class AppPaths
    {
        public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeeprawMail");

        /// <summary>正文缓存目录（同步下载的正文）。删除账号时只清理这里面的文件。</summary>
        public static AppFileCache BodyCache { get; } = new(Path.Combine(Root, "Emails"));

        /// <summary>阅读器页面缓存目录（打开邮件时生成的 HTML 页面，启动时清空）。</summary>
        public static AppFileCache ReaderCache { get; } = new(Path.Combine(Root, "ReaderCache"));

        public static string DiagnosticLogPath { get; } = Path.Combine(Root, "logs", "diagnostics.log");
    }
}
