namespace SapServer.Helpers;

public sealed class WhereClauseBuilder
{
    private const int MaxRowLength = 72;
    private readonly List<string> _conditions = new();

    /// <summary>Appends a WHERE condition. May be a complete condition or a fragment.</summary>
    public WhereClauseBuilder Add(string condition)
    {
        _conditions.Add(condition);
        return this;
    }

    /// <summary>Adds a condition only when <paramref name="value"/> is not null or whitespace.</summary>
    public WhereClauseBuilder AddIf(string? value, Func<string, string> conditionFactory)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _conditions.Add(conditionFactory(value));
        return this;
    }

    /// <summary>
    /// Builds the where_clause rows ready for InputTablesItems["where_clause"].
    /// Each condition is split at word boundaries so no TEXT value exceeds 72 characters.
    /// Subsequent conditions are prefixed with "AND " automatically.
    /// </summary>
    public List<Dictionary<string, object?>> Build()
    {
        var rows = new List<Dictionary<string, object?>>();

        foreach (var condition in _conditions)
        {
            foreach (var chunk in SplitToChunks(condition, MaxRowLength))
                rows.Add(new Dictionary<string, object?> { ["TEXT"] = chunk });
        }

        return rows;
    }

    private static IEnumerable<string> SplitToChunks(string text, int maxLen)
    {
        if (text.Length <= maxLen)
        {
            yield return text;
            yield break;
        }

        int start = 0;
        while (start < text.Length)
        {
            if (start + maxLen >= text.Length)
            {
                yield return text[start..];
                break;
            }

            int end     = start + maxLen;
            int splitAt = text.LastIndexOf(' ', end, end - start);

            if (splitAt <= start)
                splitAt = start + maxLen; // hard cut — no space found

            yield return text[start..splitAt].TrimEnd();
            start = splitAt + 1;
        }
    }
}
