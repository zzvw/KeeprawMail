using System.Globalization;

namespace KeeprawMail.Core.Formatting;

/// <summary>
/// 用户可见的邮件时间格式化（列表、完整时间提示、阅读器头部、回复 / 转发引用）。
///
/// 规则：
///  * 数据库保存的是邮件的绝对时间及原始偏移（如 Gmail INTERNALDATE 为 +00:00），这里只在显示时换算为本地时区，
///    不修改存储、IMAP 日期解析或同步游标；
///  * 本地时区默认是 Windows 当前系统时区（<see cref="TimeZoneInfo.Local"/>，每次格式化时读取，不硬编码 UTC+8）；
///    测试可注入任意 <see cref="TimeZoneInfo"/> 与 <see cref="TimeProvider"/>；
///  * “今天 / 昨天 / 星期 / 月日 / 年月日”按<b>本地日历日期</b>判断（不是按相差的小时数），夏令时由 <see cref="TimeZoneInfo.ConvertTime(DateTimeOffset, TimeZoneInfo)"/> 处理；
///  * 排序不经过这里：列表按真实时刻排序（见 <c>EmailStore.ListOrderSql</c>），与显示时区无关。
/// </summary>
public sealed class MailDateFormatter
{
    private readonly TimeZoneInfo? _zone;
    private readonly TimeProvider _time;
    private readonly CultureInfo? _culture;

    /// <summary>使用 Windows 当前系统时区、系统时钟与当前区域格式。</summary>
    public static MailDateFormatter System { get; } = new();

    /// <param name="zone">本地时区；为 null 时每次读取 <see cref="TimeZoneInfo.Local"/>（系统时区）。</param>
    /// <param name="time">时钟；为 null 时使用 <see cref="TimeProvider.System"/>。</param>
    /// <param name="culture">星期名称使用的区域；为 null 时使用 <see cref="CultureInfo.CurrentCulture"/>。数字部分固定格式，不受区域影响。</param>
    public MailDateFormatter(TimeZoneInfo? zone = null, TimeProvider? time = null, CultureInfo? culture = null)
    {
        _zone = zone;
        _time = time ?? TimeProvider.System;
        _culture = culture;
    }

    /// <summary>当前使用的本地时区。</summary>
    public TimeZoneInfo Zone => _zone ?? TimeZoneInfo.Local;

    /// <summary>
    /// 丢弃 .NET 缓存的系统时区（<see cref="TimeZoneInfo.Local"/> 与 <see cref="DateTimeOffset.Now"/> 在进程内会缓存时区）。
    /// 重新加载列表前调用，用户在 Windows 中切换时区后，下一次刷新列表即按新时区显示。
    /// </summary>
    public static void RefreshSystemTimeZone() => TimeZoneInfo.ClearCachedData();

    /// <summary>换算为本地时区的同一时刻；无法换算（如未知日期 <see cref="DateTimeOffset.MinValue"/>）时返回 null。</summary>
    public DateTimeOffset? ToLocal(DateTimeOffset value)
    {
        if (value == DateTimeOffset.MinValue || value == DateTimeOffset.MaxValue) return null;
        try
        {
            return TimeZoneInfo.ConvertTime(value, Zone);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// 列表中的短时间：本地今天 ⇒ HH:mm；本地昨天 ⇒ “昨天”；2–6 天前 ⇒ 星期（按区域，如“周三”）；同一年 ⇒ MM-dd；更早 ⇒ yyyy-MM-dd。
    /// 本地日期晚于今天（发件方时钟偏差）时按日期显示。
    /// </summary>
    public string FormatListDate(DateTimeOffset value)
    {
        if (ToLocal(value) is not DateTimeOffset local) return string.Empty;
        var today = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), Zone).Date;
        var days = (today - local.Date).Days;
        if (days == 0) return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (days == 1) return "昨天";
        if (days is > 1 and < 7) return local.ToString("ddd", _culture ?? CultureInfo.CurrentCulture);
        return local.Year == today.Year
            ? local.ToString("MM-dd", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>完整的本地时间（完整时间提示、阅读器头部、回复 / 转发引用）：yyyy-MM-dd HH:mm:ss。</summary>
    public string FormatFull(DateTimeOffset value) =>
        ToLocal(value) is DateTimeOffset local ? local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;
}
