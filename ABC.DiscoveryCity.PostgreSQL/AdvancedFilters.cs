using Npgsql;

namespace ABC.DiscoveryCity.PostgreSQL;

/// <summary>
/// Bundles advanced document filter criteria (file types, date range, page range)
/// for threading through the search pipeline from controller to DB queries.
/// </summary>
public sealed record AdvancedFilters(
    List<string>? Extensions = null,
    DateTime? DateFrom = null,
    DateTime? DateTo = null,
    int? MinPages = null,
    int? MaxPages = null)
{
    public static readonly AdvancedFilters None = new();

    public bool HasAny => (Extensions is { Count: > 0 })
        || DateFrom.HasValue || DateTo.HasValue
        || MinPages.HasValue || MaxPages.HasValue;

    /// <summary>
    /// Builds SQL AND conditions (no leading AND) for a WHERE clause.
    /// docAlias must be the ParentDocuments table alias in the query.
    /// </summary>
    public string BuildWhereFragment(string docAlias = "p")
    {
        var parts = new List<string>();
        if (Extensions is { Count: > 0 })
            parts.Add($"{docAlias}.FileName ~* @extPattern");
        if (DateFrom.HasValue)
            parts.Add($"({docAlias}.Metadata->>'DeducedDate')::timestamp >= @advDateFrom");
        if (DateTo.HasValue)
            parts.Add($"({docAlias}.Metadata->>'DeducedDate')::timestamp <= @advDateTo");
        if (MinPages.HasValue)
            parts.Add($"COALESCE(({docAlias}.Metadata->>'PageCount')::int, 0) >= @advMinPages");
        if (MaxPages.HasValue)
            parts.Add($"COALESCE(({docAlias}.Metadata->>'PageCount')::int, 0) <= @advMaxPages");
        return string.Join(" AND ", parts);
    }

    /// <summary>Adds named parameters matching BuildWhereFragment to the command.</summary>
    public void ApplyParams(NpgsqlCommand cmd)
    {
        if (Extensions is { Count: > 0 })
        {
            var pattern = $@"\.({string.Join("|", Extensions)})$";
            cmd.Parameters.AddWithValue("extPattern", pattern);
        }
        if (DateFrom.HasValue) cmd.Parameters.AddWithValue("advDateFrom", DateFrom.Value);
        if (DateTo.HasValue)   cmd.Parameters.AddWithValue("advDateTo", DateTo.Value);
        if (MinPages.HasValue) cmd.Parameters.AddWithValue("advMinPages", MinPages.Value);
        if (MaxPages.HasValue) cmd.Parameters.AddWithValue("advMaxPages", MaxPages.Value);
    }

    /// <summary>Stable string for inclusion in cache keys and query hashes.</summary>
    public string ToFingerprint()
    {
        var parts = new List<string>();
        if (Extensions is { Count: > 0 })
            parts.Add("ext=" + string.Join(",", Extensions.OrderBy(e => e)));
        if (DateFrom.HasValue)
            parts.Add("from=" + DateFrom.Value.ToString("yyyy-MM-dd"));
        if (DateTo.HasValue)
            parts.Add("to=" + DateTo.Value.ToString("yyyy-MM-dd"));
        if (MinPages.HasValue) parts.Add("minpg=" + MinPages);
        if (MaxPages.HasValue) parts.Add("maxpg=" + MaxPages);
        return string.Join("|", parts);
    }
}
