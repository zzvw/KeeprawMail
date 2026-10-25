using KeeprawMail.Core.Search;

namespace KeeprawMail.Core.Tests.Search;

/// <summary>搜索语法解析与转义（纯逻辑；真实 SQLite 行为见 LocalMailSearchTests）。</summary>
public class MailSearchQueryTests
{
    private static MailSearchQuery Parse(string s)
    {
        Assert.True(MailSearchQuery.TryParse(s, out var q, out var error), error);
        return q!;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Empty_ReturnsFalse_WithoutError(string? input)
    {
        Assert.False(MailSearchQuery.TryParse(input, out var q, out var error));
        Assert.Null(q);
        Assert.Null(error); // 空查询 = 回到收件箱，不是错误
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("\"\"")]
    [InlineData("-- ** ^^ ()")]
    [InlineData("subject:\"\"")]
    public void OnlyPunctuation_FriendlyError(string input)
    {
        Assert.False(MailSearchQuery.TryParse(input, out _, out var error));
        Assert.Equal("请输入至少一个文字、字母或数字再搜索。", error);
    }

    [Fact]
    public void TooLong_FriendlyError_NotTruncated()
    {
        Assert.True(MailSearchQuery.TryParse(new string('a', MailSearchQuery.MaxInputLength), out _, out _));
        Assert.False(MailSearchQuery.TryParse(new string('a', MailSearchQuery.MaxInputLength + 1), out _, out var error));
        Assert.Contains("太长", error);
        Assert.False(MailSearchQuery.TryParse(string.Join(" ", Enumerable.Repeat("x", MailSearchQuery.MaxTerms + 1)), out _, out var e2));
        Assert.Contains("搜索词太多", e2);
    }

    [Fact]
    public void PlainTerms_AreAnded_PrefixMatched_Quoted()
    {
        var q = Parse("invoice  payment");
        Assert.Equal(2, q.Terms.Count);
        Assert.Equal("\"invoice\"* AND \"payment\"*", q.BuildFtsMatchExpression());
    }

    [Fact]
    public void Phrase_NoPrefix_SpacesNormalized()
    {
        var q = Parse("\"flight   confirmation\"");
        var t = Assert.Single(q.Terms);
        Assert.True(t.IsPhrase);
        Assert.Equal("flight confirmation", t.Text);
        Assert.Equal("\"flight confirmation\"", q.BuildFtsMatchExpression());
    }

    [Fact]
    public void UnclosedQuote_TreatedAsPhrase()
    {
        var t = Assert.Single(Parse("\"flight confirmation").Terms);
        Assert.True(t.IsPhrase);
        Assert.Equal("flight confirmation", t.Text);
    }

    [Fact]
    public void FieldFilters_Whitelisted()
    {
        var q = Parse("subject:invoice FROM:amazon subject:\"monthly bill\"");
        Assert.Equal(new[] { MailSearchField.Subject, MailSearchField.From, MailSearchField.Subject }, q.Terms.Select(t => t.Field));
        Assert.Equal("Subject : \"invoice\"* AND {SenderName SenderAddress} : \"amazon\"* AND Subject : \"monthly bill\"", q.BuildFtsMatchExpression());
    }

    [Theory]
    [InlineData("to:bob")]
    [InlineData("Snippet:abc")]
    [InlineData("rowid:1")]
    [InlineData("http://example.com")]
    public void UnknownField_IsPlainText_ColumnNeverFromInput(string input)
    {
        var q = Parse(input);
        var t = Assert.Single(q.Terms);
        Assert.Equal(MailSearchField.All, t.Field);
        Assert.Equal(input, t.Text);
        Assert.StartsWith("\"", q.BuildFtsMatchExpression());
    }

    [Theory]
    [InlineData("a\"b", "\"a\"* AND \"b\"")]
    [InlineData("AND OR NOT", "\"AND\"* AND \"OR\"* AND \"NOT\"*")]
    [InlineData("NEAR(a", "\"NEAR(a\"*")]
    [InlineData("x*y^", "\"x*y^\"*")]
    public void Operators_AreLiteralStrings(string input, string expected) =>
        Assert.Equal(expected, Parse(input).BuildFtsMatchExpression());

    [Fact]
    public void EmbeddedDoubleQuotes_Escaped()
    {
        // 字段值里出现双引号：只能通过 subject:"..." 之外的方式出现，这里直接构造检验转义
        var q = Parse("o'brien");
        Assert.Equal("\"o'brien\"*", q.BuildFtsMatchExpression());
    }

    [Fact]
    public void ControlCharacters_TreatedAsSpace()
    {
        // FTS5 按 C 字符串解析表达式，NUL 会截断字符串导致 “unterminated string”；解析阶段把控制字符当作空格
        var q = Parse("x\u0000y\u0007z");
        Assert.Equal(new[] { "x", "y", "z" }, q.Terms.Select(t => t.Text));
        Assert.DoesNotContain('\0', q.BuildFtsMatchExpression()!);
    }

    [Fact]
    public void Cjk_UsesSubstring_NotFts()
    {
        var q = Parse("发票 invoice subject:账单 from:京东");
        Assert.Equal(new[] { "发票", "账单", "京东" }, q.SubstringTerms.Select(t => t.Text));
        Assert.Equal("\"invoice\"*", q.BuildFtsMatchExpression());
        Assert.Null(Parse("发票").BuildFtsMatchExpression());
        Assert.True(MailSearchQuery.ContainsCjk("カタカナ"));
        Assert.True(MailSearchQuery.ContainsCjk("한국어"));
        Assert.True(MailSearchQuery.ContainsCjk("𠀀")); // 扩展 B（代理对）
        Assert.False(MailSearchQuery.ContainsCjk("café"));
    }

    [Theory]
    [InlineData("100%", "%100\\%%")]
    [InlineData("a_b", "%a\\_b%")]
    [InlineData("c:\\x", "%c:\\\\x%")]
    [InlineData("发票", "%发票%")]
    public void LikePattern_Escaped(string text, string expected) => Assert.Equal(expected, MailSearchQuery.ToLikePattern(text));
}
