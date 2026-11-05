using ConectaTI.App.Helpers;
using ConectaTI.App.Models;
namespace ConectaTI.App.Services.Connections;

internal sealed record ConnectionListScope(string? Client, string? Group, bool IsGroup);
internal sealed record ConnectionListCriteria(ConnectionViewMode ViewMode, ConnectionSortOption Sort,
    string Search = "", string? Client = null, string? Environment = null, string? Protocol = null,
    ConnectionListScope? Scope = null, ConnectionActivityFilter ActivityFilter = ConnectionActivityFilter.All);

// Pure displayed-order rules. ActivityFilter filters by persisted IsActive when explicitly selected.
internal static class ConnectionListQuery
{
    public static List<ConnectionEntry> Apply(IEnumerable<ConnectionEntry> connections, ConnectionListCriteria criteria)
    {
        IEnumerable<ConnectionEntry> query = criteria.ViewMode == ConnectionViewMode.Trash
            ? connections.Where(c => c.IsDeleted)
            : connections.Where(c => !c.IsDeleted);

        if (criteria.ViewMode == ConnectionViewMode.Favorites)
        {
            query = query.Where(c => c.IsFavorite);
        }
        else if (criteria.ViewMode == ConnectionViewMode.Recent)
        {
            query = query.Where(c => c.LastOpenedAtUtc.HasValue);
        }

        if (criteria.ActivityFilter == ConnectionActivityFilter.Active)
        {
            query = query.Where(c => c.IsActive);
        }
        else if (criteria.ActivityFilter == ConnectionActivityFilter.Inactive)
        {
            query = query.Where(c => !c.IsActive);
        }

        if (criteria.Scope != null)
        {
            query = query.Where(c => string.Equals(c.ClientName.Trim(), criteria.Scope.Client, StringComparison.OrdinalIgnoreCase));
            if (criteria.Scope.IsGroup)
                query = query.Where(c => string.Equals(string.IsNullOrWhiteSpace(c.GroupName) ? "Geral" : c.GroupName.Trim(), criteria.Scope.Group, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrEmpty(criteria.Client))
            query = query.Where(c => string.Equals(c.ClientName.Trim(), criteria.Client, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(criteria.Environment))
            query = query.Where(c => c.Environment.ToInvariantDisplayName() == criteria.Environment);
        if (!string.IsNullOrEmpty(criteria.Protocol))
            query = query.Where(c => c.Protocol.ToInvariantDisplayName() == criteria.Protocol);

        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var terms = criteria.Search.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var term in terms)
            {
                var cleanTerm = StringNormalizationHelper.RemoveDiacritics(term);
                query = query.Where(c =>
                    StringNormalizationHelper.RemoveDiacritics(c.Name).Contains(cleanTerm, StringComparison.OrdinalIgnoreCase) ||
                    StringNormalizationHelper.RemoveDiacritics(c.ClientName).Contains(cleanTerm, StringComparison.OrdinalIgnoreCase) ||
                    StringNormalizationHelper.RemoveDiacritics(c.GroupName).Contains(cleanTerm, StringComparison.OrdinalIgnoreCase) ||
                    StringNormalizationHelper.RemoveDiacritics(c.Host).Contains(cleanTerm, StringComparison.OrdinalIgnoreCase) ||
                    StringNormalizationHelper.RemoveDiacritics(c.Username).Contains(cleanTerm, StringComparison.OrdinalIgnoreCase) ||
                    StringNormalizationHelper.RemoveDiacritics(c.Domain).Contains(cleanTerm, StringComparison.OrdinalIgnoreCase) ||
                    StringNormalizationHelper.RemoveDiacritics(c.Tags).Contains(cleanTerm, StringComparison.OrdinalIgnoreCase));
            }
        }

        query = criteria.Sort switch
        {
            ConnectionSortOption.ClientAsc => query
                .OrderBy(c => c.ClientName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.GroupName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase),

            ConnectionSortOption.LastOpenedDesc => query
                .OrderByDescending(c => c.LastOpenedAtUtc ?? DateTime.MinValue)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase),

            _ => query
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.ClientName, StringComparer.OrdinalIgnoreCase)
        };


        return query.ToList();
    }

    public static ConnectionEntry? SelectById(IReadOnlyList<ConnectionEntry> displayed, string? id)
        => displayed.FirstOrDefault(c => c.Id == id) ?? displayed.FirstOrDefault();

    public static int MoveIndex(int count, int selectedIndex, int direction)
    {
        if (count == 0) return -1;
        return selectedIndex < 0 ? (direction > 0 ? 0 : count - 1)
            : Math.Clamp(selectedIndex + Math.Sign(direction), 0, count - 1);
    }
}
