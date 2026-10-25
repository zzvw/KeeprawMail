using KeeprawMail.Core.Search;
using Microsoft.Data.Sqlite;

namespace KeeprawMail.Core.Storage;

/// <summary>一页搜索结果。<see cref="TotalCount"/> 是全部匹配数，界面据此提示“共 N 封，已显示 M 封”，不会无提示截断。</summary>
public sealed record MailSearchPage(IReadOnlyList<StoredEmail> Items, int TotalCount, int Offset, int Limit)
{
    public bool HasMore => Offset + Items.Count < TotalCount;
}

public sealed partial class EmailStore
{
    /// <summary>每页默认结果数。</summary>
    public const int DefaultSearchPageSize = 200;

    /// <summary>单次查询最多返回的结果数（防止一次加载过多邮件导致界面卡顿）。</summary>
    public const int MaxSearchPageSize = 1000;

    /// <summary>
    /// 本地邮件搜索：所有账号、所有已同步文件夹（不限于首页的 INBOX / 每账号 100 封），只读本地 SQLite，不访问网络。
    /// 不含中日韩字符的词使用 FTS5 MATCH（Emails_FTS 外部内容表，由现有触发器维护）；含中日韩字符的词按子串匹配同样四列（见 <see cref="MailSearchQuery"/>）。
    /// 过滤、按真实时间倒序（<see cref="ListOrderSql"/>，julianday）、LIMIT / OFFSET 都在 SQLite 中完成，一次查询直接返回邮件记录（无 N+1）。
    /// Id 是主键、FTS 条件用 rowid IN (...)，同一封邮件不会重复返回；不按主题 / 发件人 / 日期推断或合并邮件。
    /// </summary>
    /// <exception cref="MailSearchException">SQLite 拒绝查询时（消息可直接显示，不含原始错误）。</exception>
    public async Task<MailSearchPage> SearchAsync(MailSearchQuery query, int limit = DefaultSearchPageSize, int offset = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        limit = Math.Clamp(limit, 1, MaxSearchPageSize);
        offset = Math.Max(0, offset);

        using var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);

        using var count = c.CreateCommand();
        using var select = c.CreateCommand();
        var where = BuildSearchWhere(query, count, select);
        count.CommandText = $"SELECT COUNT(*) FROM Emails WHERE {where}";
        select.CommandText = $"SELECT {Columns} FROM Emails WHERE {where} ORDER BY {ListOrderSql} LIMIT $Limit OFFSET $Offset";
        select.Parameters.AddWithValue("$Limit", limit);
        select.Parameters.AddWithValue("$Offset", offset);

        try
        {
            var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct).ConfigureAwait(false));
            var items = total == 0 ? new List<StoredEmail>() : await ReadAsync(select, ct).ConfigureAwait(false);
            return new MailSearchPage(items, total, offset, limit);
        }
        catch (SqliteException ex)
        {
            // 正常情况下不会发生（表达式由 MailSearchQuery 按固定格式生成）；保底不把 SQLite 原始错误显示给用户
            throw new MailSearchException("搜索条件无法执行，请修改搜索内容后重试。", ex);
        }
    }

    /// <summary>生成 WHERE 子句并在两个命令上绑定相同参数。SQL 中只出现固定文本、白名单列名与参数名。</summary>
    private static string BuildSearchWhere(MailSearchQuery query, SqliteCommand a, SqliteCommand b)
    {
        var conditions = new List<string>();
        var fts = query.BuildFtsMatchExpression();
        if (fts != null)
        {
            conditions.Add("rowid IN (SELECT rowid FROM Emails_FTS WHERE Emails_FTS MATCH $Fts)");
            a.Parameters.AddWithValue("$Fts", fts);
            b.Parameters.AddWithValue("$Fts", fts);
        }

        var n = 0;
        foreach (var term in query.SubstringTerms)
        {
            var name = "$L" + n++;
            var columns = term.Field switch
            {
                MailSearchField.Subject => new[] { "Subject" },
                MailSearchField.From => new[] { "SenderName", "SenderAddress" },
                _ => new[] { "Subject", "SenderName", "SenderAddress", "Snippet" },
            };
            conditions.Add("(" + string.Join(" OR ", columns.Select(col => $"IFNULL({col}, '') LIKE {name} ESCAPE '\\'")) + ")");
            var pattern = MailSearchQuery.ToLikePattern(term.Text);
            a.Parameters.AddWithValue(name, pattern);
            b.Parameters.AddWithValue(name, pattern);
        }

        // MailSearchQuery 保证至少有一个词
        return string.Join(" AND ", conditions);
    }
}
