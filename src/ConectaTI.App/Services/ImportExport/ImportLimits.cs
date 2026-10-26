using System.Xml;
using System.Xml.Linq;

namespace ConectaTI.App.Services.ImportExport;

public sealed record ImportProgress(string Stage, int Completed = 0, int Total = 0);

public static class ImportLimits
{
    public const long MaxFileBytes = 32 * 1024 * 1024;
    public const int MaxRecords = 20_000;
    public const int MaxFieldCharacters = 262_144;
    public static void CheckFile(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException(L.Text("O arquivo excede o limite de 32 MB."));
    }
    public static void CheckCount(int count)
    {
        if (count > MaxRecords) throw new InvalidDataException(L.Text("O arquivo excede o limite de 20.000 acessos."));
    }
    private static readonly System.Reflection.PropertyInfo[] TextFields = typeof(ConectaTI.App.Models.ConnectionEntry).GetProperties().Where(p => p.PropertyType == typeof(string) && p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true).Length == 0).ToArray();
    public static void CheckEntry(ConectaTI.App.Models.ConnectionEntry entry)
    {
        foreach (var property in TextFields)
            if (((string?)property.GetValue(entry))?.Length > MaxFieldCharacters)
                throw new InvalidDataException(L.Text("Um campo do arquivo excede o limite permitido."));
    }
    public static XDocument ReadXml(string text)
    {
        if (text.Length > MaxFileBytes) throw new InvalidDataException(L.Text("O XML excede o limite permitido."));
        var options = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaxFileBytes, MaxCharactersFromEntities = 0,
            IgnoreComments = true
        };
        // Bound the shape before allocating an XElement tree, even for unknown tags.
        using (var preflightInput = new StringReader(text))
        using (var preflight = XmlReader.Create(preflightInput, options))
        {
            var elements = 0;
            while (preflight.Read())
            {
                if (preflight.NodeType != XmlNodeType.Element) continue;
                elements++;
                if (elements % 256 == 0) AutoPuttyCodec.CheckCancellation();
                if (preflight.Depth > 64 || elements > MaxRecords * 32 + 100 || preflight.AttributeCount > 128)
                    throw new InvalidDataException(L.Text("A estrutura do XML excede os limites permitidos."));
            }
        }
        using var input = new StringReader(text);
        using var reader = XmlReader.Create(input, options);
        var doc = XDocument.Load(reader);
        foreach (var element in doc.Descendants())
        {
            if (!element.HasElements && element.Value.Length > MaxFieldCharacters && !element.Name.LocalName.Equals("List", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.Text("Um campo do arquivo excede o limite permitido."));
            foreach (var attribute in element.Attributes())
                if (attribute.Value.Length > MaxFieldCharacters) throw new InvalidDataException(L.Text("Um campo do arquivo excede o limite permitido."));
        }
        return doc;
    }
}
