using System.Text;
namespace ConectaTI.App.Services.ImportExport;

// CSV syntax, quoting, bounds and read-progress cancellation, independent of connection fields.
internal static class CsvRecordCodec
{
    public static char DetectDelimiter(string content)
    {
        var firstLineEnd = content.IndexOfAny(['\r', '\n']);
        var firstLine = firstLineEnd >= 0 ? content[..firstLineEnd] : content;
        var semicolons = firstLine.Count(c => c == ';');
        var commas = firstLine.Count(c => c == ',');
        return semicolons >= commas ? ';' : ',';
    }

    public static List<string[]> Parse(string content, char delimiter)
    {
        if (content.Length > ImportLimits.MaxFileBytes) throw new InvalidDataException(L.Text("O arquivo excede o limite de 32 MB."));
        var rows = new List<string[]>();
        var currentRow = new List<string>();
        var currentCell = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < content.Length; i++)
        {
            if (i % 4096 == 0) AutoPuttyCodec.CheckReadProgress(Math.Max(0, rows.Count - 1));
            if (currentCell.Length > ImportLimits.MaxFieldCharacters || currentRow.Count > 256) throw new InvalidDataException(L.Text("Um campo do arquivo excede o limite permitido."));
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        currentCell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    currentCell.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == delimiter)
                {
                    currentRow.Add(currentCell.ToString());
                    currentCell.Clear();
                }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                        i++;

                    currentRow.Add(currentCell.ToString());
                    currentCell.Clear();
                    rows.Add(currentRow.ToArray());
                    ImportLimits.CheckCount(Math.Max(0, rows.Count - 1));
                    currentRow = new List<string>();
                }
                else
                {
                    currentCell.Append(c);
                }
            }
        }

        if (currentCell.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(currentCell.ToString());
            rows.Add(currentRow.ToArray());
        }

        return rows;
    }

    public static string Escape(string? value)
    {
        var s = value ?? string.Empty;
        if (s.Contains(';') || s.Contains(',') || s.Contains('"') || s.Contains('\r') || s.Contains('\n'))
        {
            return $"\"{s.Replace("\"", "\"\"")}\"";
        }
        return s;
    }

}
