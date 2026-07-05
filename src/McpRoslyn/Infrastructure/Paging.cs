using System.Text;

namespace McpRoslyn.Infrastructure;

public static class Paging
{
    public const int DefaultMax = 50;
    public const int MaxAllowed = 200;

    /// <summary>
    /// Renders a counts-first, paginated list:
    /// "N label (showing A–B). Call again with page=k for more." followed by one line per item.
    /// </summary>
    public static string Render<T>(
        IReadOnlyList<T> items,
        int page,
        int maxResults,
        string label,
        Func<T, string> renderLine,
        string? emptyMessage = null,
        string? footer = null)
    {
        maxResults = Math.Clamp(maxResults <= 0 ? DefaultMax : maxResults, 1, MaxAllowed);
        page = Math.Max(1, page);

        if (items.Count == 0)
            return emptyMessage ?? $"0 {label}.";

        var start = (page - 1) * maxResults;
        if (start >= items.Count)
            return $"{items.Count} {label}, but page {page} is past the end (last page: {LastPage(items.Count, maxResults)}).";

        var slice = items.Skip(start).Take(maxResults).ToList();
        var sb = new StringBuilder();
        sb.Append(items.Count).Append(' ').Append(label);
        if (items.Count > slice.Count)
        {
            sb.Append($" (showing {start + 1}–{start + slice.Count}");
            if (start + slice.Count < items.Count)
                sb.Append($"; page={page + 1} for more");
            sb.Append(')');
        }
        sb.AppendLine(":");

        foreach (var item in slice)
            sb.AppendLine(renderLine(item));

        if (footer is not null)
            sb.AppendLine(footer);

        return sb.ToString().TrimEnd();
    }

    private static int LastPage(int count, int max) => (count + max - 1) / max;
}
