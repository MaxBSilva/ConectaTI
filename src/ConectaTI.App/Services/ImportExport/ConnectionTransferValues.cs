using ConectaTI.App.Models;
namespace ConectaTI.App.Services.ImportExport;

internal static class ConnectionTransferValues
{
    public static SshClientPreference ParseSshClient(string value) =>
        Enum.TryParse<SshClientPreference>(value, true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : SshClientPreference.Default;
    public static bool ParseAgent(string value) => string.IsNullOrWhiteSpace(value) || ParseActive(value);

    public static bool ParseActive(string value) => value.Trim().ToLowerInvariant() switch
    {
        "" or "true" or "1" or "sim" or "ativo" => true,
        "false" or "0" or "não" or "nao" or "inativo" => false,
        _ => throw new FormatException(L.Text("Status Ativo/Inativo inválido."))
    };
}
