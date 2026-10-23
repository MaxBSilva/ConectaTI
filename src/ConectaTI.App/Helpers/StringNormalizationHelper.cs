using System.Globalization;
using System.Text;

namespace ConectaTI.App.Helpers;

public static class StringNormalizationHelper
{
    public static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public static int GetIncrementalMatchScore(string clientName, string connectionName, string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return -1;

        var cleanTerm = RemoveDiacritics(term).Trim().ToLowerInvariant();
        if (cleanTerm.Length == 0)
            return -1;

        var cleanClient = RemoveDiacritics(clientName ?? string.Empty).Trim().ToLowerInvariant();
        var cleanConn = RemoveDiacritics(connectionName ?? string.Empty).Trim().ToLowerInvariant();

        // Pontuação 1: Prefixo exato no início do nome da conexão (Maior prioridade)
        if (cleanConn.StartsWith(cleanTerm))
            return 1;

        // Pontuação 2: Prefixo exato no início do nome do cliente
        if (cleanClient.StartsWith(cleanTerm))
            return 2;

        // Pontuação 3: Contém no nome da conexão
        if (cleanConn.Contains(cleanTerm))
            return 3;

        // Pontuação 4: Contém no nome do cliente
        if (cleanClient.Contains(cleanTerm))
            return 4;

        return -1;
    }

    public static Models.ConnectionEntry? FindBestMatch(IEnumerable<Models.ConnectionEntry> connections, string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return null;

        Models.ConnectionEntry? bestItem = null;
        int bestScore = int.MaxValue;

        foreach (var conn in connections)
        {
            var score = GetIncrementalMatchScore(conn.ClientName, conn.Name, term);
            if (score > 0 && score < bestScore)
            {
                bestScore = score;
                bestItem = conn;
                if (bestScore == 1)
                    break;
            }
        }

        return bestItem;
    }
}
