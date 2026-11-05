using System.Text.Json;
using ConectaTI.App.Models;
namespace ConectaTI.App.Services.ImportExport;

internal static class ConnectionJsonCodec
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };


    public static string Write(IEnumerable<ConnectionEntry> connections)
        => JsonSerializer.Serialize(connections.Select(c => c.Clone()).ToList(), JsonOptions);
    public static List<ConnectionEntry>? Read(string text) => JsonSerializer.Deserialize<List<ConnectionEntry>>(text, JsonOptions);
}
