using System.Globalization;
using KeeprawMail.Core.Formatting;
using Microsoft.Extensions.Time.Testing;

namespace KeeprawMail.Core.Tests;

/// <summary>
/// 用户可见邮件时间的本地化：UTC、UTC+8、UTC-7 / UTC-8、夏令时切换、午夜跨日、今天 / 昨天 / 星期 / 年份，
/// 以及换算只影响显示（原始绝对时间与偏移不变）。时区由测试注入，不依赖运行机器的系统时区。
/// </summary>
public class MailDateFormatterTests
{
    private static TimeZoneInfo Zone(string iana, string windows)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
    }

    private static readonly TimeZoneInfo Shanghai = Zone("Asia/Shanghai", "China Standard Time");
    private static readonly TimeZoneInfo LosAngeles = Zone("America/Los_Angeles", "Pacific Standard Time");
    private static readonly TimeZoneInfo Phoenix = Zone("America/Phoenix", "US Mountain Standard Time"); // 全年 UTC-7，无夏令时
    private static readonly TimeZoneInfo NewYork = Zone("America/New_York", "Eastern Standard Time");

    private static MailDateFormatter F(TimeZoneInfo zone, DateTimeOffset nowUtc) =>
        new(zone, new FakeTimeProvider(nowUtc), CultureInfo.InvariantCulture);

    // Gmail INTERNALDATE 以 +00:00 保存：北京时间 2026-10-22 22:00 收到的邮件
    private static readonly DateTimeOffset BeijingTenPm = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReportedBug_Utc0StoredMail_ShownInBeijingLocalTime()
    {
        var now = new DateTimeOffset(2026, 10, 22, 15, 0, 0, TimeSpan.Zero); // 北京 23:00
        var f = F(Shanghai, now);
        Assert.Equal("22:00", f.FormatListDate(BeijingTenPm));          // 修复前显示 14:00
        Assert.Equal("2026-10-22 22:00:00", f.FormatFull(BeijingTenPm));
        // 同一时刻以 +08:00 保存时显示完全相同（只看绝对时间）
        var sameInstant = new DateTimeOffset(2026, 10, 22, 22, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal(f.FormatFull(BeijingTenPm), f.FormatFull(sameInstant));
        // 原始值不被修改
        Assert.Equal(TimeSpan.Zero, BeijingTenPm.Offset);
        Assert.Equal(14, BeijingTenPm.Hour);
    }

    [Fact]
    public void Utc_Zone_ShowsUtcTime()
    {
        var f = F(TimeZoneInfo.Utc, new DateTimeOffset(2026, 10, 22, 20, 0, 0, TimeSpan.Zero));
        Assert.Equal("14:00", f.FormatListDate(BeijingTenPm));
        Assert.Equal("2026-10-22 14:00:00", f.FormatFull(BeijingTenPm));
        var plus8 = new DateTimeOffset(2026, 10, 22, 22, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal("2026-10-22 14:00:00", f.FormatFull(plus8));
    }

    [Fact]
    public void SwitchToUsTimeZones_UtcMinus7AndMinus8()
    {
        // 同一封邮件，用户把 Windows 时区切换到美国
        var nowUtc = new DateTimeOffset(2026, 10, 22, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal("2026-10-22 07:00:00", F(LosAngeles, nowUtc).FormatFull(BeijingTenPm)); // 10 月为 PDT（UTC-7）
        Assert.Equal("07:00", F(LosAngeles, nowUtc).FormatListDate(BeijingTenPm));
        Assert.Equal("2026-10-22 07:00:00", F(Phoenix, nowUtc).FormatFull(BeijingTenPm));    // UTC-7
        Assert.Equal("2026-10-22 10:00:00", F(NewYork, nowUtc).FormatFull(BeijingTenPm));    // EDT（UTC-4）

        // 12 月：洛杉矶为 PST（UTC-8），凤凰城仍为 UTC-7
        var winter = new DateTimeOffset(2026, 12, 15, 14, 0, 0, TimeSpan.Zero);
        var winterNow = new DateTimeOffset(2026, 12, 15, 23, 0, 0, TimeSpan.Zero);
        Assert.Equal("2026-12-15 06:00:00", F(LosAngeles, winterNow).FormatFull(winter));
        Assert.Equal("2026-12-15 07:00:00", F(Phoenix, winterNow).FormatFull(winter));
        Assert.Equal("2026-12-15 22:00:00", F(Shanghai, winterNow).FormatFull(winter));

        // 固定偏移的自定义时区（与系统时区数据无关）
        var minus8 = TimeZoneInfo.CreateCustomTimeZone("Test-8", TimeSpan.FromHours(-8), "UTC-8", "UTC-8");
        Assert.Equal("2026-10-22 06:00:00", F(minus8, nowUtc).FormatFull(BeijingTenPm));
    }

    [Fact]
    public void DaylightSaving_Transitions_LosAngeles()
    {
        // 2026-03-08 02:00 PST → 03:00 PDT；2026-11-01 02:00 PDT → 01:00 PST
        var f = F(LosAngeles, new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("2026-03-08 01:30:00", f.FormatFull(new DateTimeOffset(2026, 3, 8, 9, 30, 0, TimeSpan.Zero)));  // 切换前 UTC-8
        Assert.Equal("2026-03-08 03:30:00", f.FormatFull(new DateTimeOffset(2026, 3, 8, 10, 30, 0, TimeSpan.Zero))); // 切换后 UTC-7（02:xx 不存在）
        Assert.Equal("2026-11-01 01:30:00", f.FormatFull(new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero)));  // 第一次 01:30（PDT）
        Assert.Equal("2026-11-01 01:30:00", f.FormatFull(new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero)));  // 第二次 01:30（PST）
        Assert.Equal("2026-11-01 02:30:00", f.FormatFull(new DateTimeOffset(2026, 11, 1, 10, 30, 0, TimeSpan.Zero)));

        // 自定义夏令时规则（与系统时区数据无关）：3 月第二个周日 2:00 → 11 月第一个周日 2:00
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday));
        var custom = TimeZoneInfo.CreateCustomTimeZone("Test-Pacific", TimeSpan.FromHours(-8), "Test Pacific", "TPST", "TPDT", new[] { rule });
        var g = F(custom, new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("2026-07-01 05:00:00", g.FormatFull(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)));  // UTC-7
        Assert.Equal("2026-01-01 04:00:00", g.FormatFull(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)));  // UTC-8
    }

    [Fact]
    public void TodayYesterday_ByLocalDate_AcrossMidnight()
    {
        // 北京时间 10-23 00:30（UTC 仍是 10-22 16:30）；邮件在北京时间 10-22 23:50
        var now = new DateTimeOffset(2026, 10, 22, 16, 30, 0, TimeSpan.Zero);
        var mail = new DateTimeOffset(2026, 10, 22, 15, 50, 0, TimeSpan.Zero);
        Assert.Equal("昨天", F(Shanghai, now).FormatListDate(mail)); // 本地已跨日
        Assert.Equal("15:50", F(TimeZoneInfo.Utc, now).FormatListDate(mail)); // UTC 同一天

        // 北京 00:10 收到的邮件，北京 00:30 查看：今天
        Assert.Equal("00:10", F(Shanghai, now).FormatListDate(new DateTimeOffset(2026, 10, 22, 16, 10, 0, TimeSpan.Zero)));

        // 洛杉矶 10-22 23:30（PDT）查看：洛杉矶 10-22 00:05 的邮件是今天，10-21 23:59 是昨天
        var laNow = new DateTimeOffset(2026, 10, 23, 6, 30, 0, TimeSpan.Zero);
        Assert.Equal("00:05", F(LosAngeles, laNow).FormatListDate(new DateTimeOffset(2026, 10, 22, 7, 5, 0, TimeSpan.Zero)));
        Assert.Equal("昨天", F(LosAngeles, laNow).FormatListDate(new DateTimeOffset(2026, 10, 22, 6, 59, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void YesterdayAcrossDstNight_UsesCalendarDays()
    {
        // 秋季回拨的那一天只有 25 小时：按本地日历日期判断，不按 24 小时
        var now = new DateTimeOffset(2026, 11, 2, 7, 30, 0, TimeSpan.Zero);   // 洛杉矶 11-01 23:30 PST
        var mail = new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);  // 洛杉矶 10-31 23:30 PDT
        Assert.Equal("昨天", F(LosAngeles, now).FormatListDate(mail));
        Assert.Equal("00:30", F(LosAngeles, now).FormatListDate(new DateTimeOffset(2026, 11, 1, 7, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Weekday_Month_Year_Rules()
    {
        var now = new DateTimeOffset(2026, 10, 22, 4, 0, 0, TimeSpan.Zero); // 北京 10-22（周四）12:00
        var f = F(Shanghai, now);
        Assert.Equal("Tue", f.FormatListDate(new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.Zero)));  // 2 天前
        Assert.Equal("Fri", f.FormatListDate(new DateTimeOffset(2026, 10, 16, 2, 0, 0, TimeSpan.Zero)));  // 6 天前
        Assert.Equal("10-15", f.FormatListDate(new DateTimeOffset(2026, 10, 15, 2, 0, 0, TimeSpan.Zero))); // 7 天前：同一年 MM-dd
        Assert.Equal("01-01", f.FormatListDate(new DateTimeOffset(2025, 12, 31, 20, 0, 0, TimeSpan.Zero))); // UTC 是 2025 年，北京已是 2026-01-01
        Assert.Equal("2025-12-31", f.FormatListDate(new DateTimeOffset(2025, 12, 31, 10, 0, 0, TimeSpan.Zero)));
        Assert.Equal("10-25", f.FormatListDate(new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.Zero)));  // 本地日期晚于今天（发件方时钟偏差）

        // 星期名称跟随区域设置
        var zh = new MailDateFormatter(Shanghai, new FakeTimeProvider(now), CultureInfo.GetCultureInfo("zh-CN"));
        var weekday = zh.FormatListDate(new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTime(2026, 10, 20).ToString("ddd", CultureInfo.GetCultureInfo("zh-CN")), weekday);
    }

    [Fact]
    public void UnknownDate_NoException()
    {
        var f = F(LosAngeles, DateTimeOffset.UnixEpoch.AddYears(50));
        Assert.Equal(string.Empty, f.FormatListDate(DateTimeOffset.MinValue));
        Assert.Equal(string.Empty, f.FormatFull(DateTimeOffset.MinValue));
        Assert.Equal(string.Empty, F(Shanghai, DateTimeOffset.UnixEpoch).FormatFull(DateTimeOffset.MaxValue));
        Assert.Equal("1970-01-01 08:00:00", F(Shanghai, DateTimeOffset.UnixEpoch).FormatFull(DateTimeOffset.UnixEpoch)); // 同步时无日期的回退值
    }

    [Fact]
    public void SortingIsIndependentOfDisplayZone()
    {
        // 显示顺序由真实时刻决定：换算到任何时区后先后关系不变
        var a = new DateTimeOffset(2026, 10, 22, 21, 30, 0, TimeSpan.FromHours(8)); // 13:30Z
        var b = new DateTimeOffset(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);          // 14:00Z
        Assert.True(a < b);
        foreach (var zone in new[] { Shanghai, LosAngeles, TimeZoneInfo.Utc })
            Assert.True(new MailDateFormatter(zone).ToLocal(a) < new MailDateFormatter(zone).ToLocal(b));
    }

    [Fact]
    public void SystemFormatter_UsesSystemLocalZone()
    {
        var value = new DateTimeOffset(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            MailDateFormatter.System.FormatFull(value));
        MailDateFormatter.RefreshSystemTimeZone(); // 不抛异常
        Assert.Equal(TimeZoneInfo.Local.Id, MailDateFormatter.System.Zone.Id);
    }
}
