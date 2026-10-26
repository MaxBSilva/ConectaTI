using ConectaTI.App.Security;
namespace ConectaTI.Tests;
internal static class VaultTestSupport
{
    internal const string Password = "Synthetic master #2026";
    // Automatic protection intentionally has no setup or unlock. Legacy fixtures create their vault explicitly.
    internal static void Initialize(CredentialVaultService vault,string password=Password) { }
}
