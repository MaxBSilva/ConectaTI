using ConectaTI.App.Helpers;
namespace ConectaTI.App.Services.ImportExport;

public partial class ImportExportService
{
    private static void ValidateTransferBundle(PortableEncryptedBundle bundle,bool metadataOnly)
    {
        if(bundle.FormatVersion is not ("ConectaTI-Portable-1.0" or "ConectaTI-Portable-2.0") ||
            bundle.Connections==null || bundle.Credentials==null)
            throw new InvalidDataException(L.Text("Formato de pacote não suportado."));
        if(bundle.Connections.Any(c=>c==null || string.IsNullOrWhiteSpace(c.Id) || c.IsDeleted ||
            ConnectionValidator.ValidateConnection(c).Count>0) ||
            bundle.Connections.GroupBy(c=>c.Id).Any(g=>g.Count()!=1) ||
            bundle.Credentials.Any(c=>c==null || string.IsNullOrWhiteSpace(c.Id)))
            throw new InvalidDataException(L.Text("Pacote com cadastros inválidos."));
        if(metadataOnly && (bundle.FormatVersion!="ConectaTI-Portable-2.0" || bundle.IncludesPasswords ||
            bundle.Credentials.Count>0 || bundle.Connections.Any(c=>!string.IsNullOrEmpty(c.CredentialId))))
            throw new InvalidDataException(L.Text("Pacote sem proteção não pode conter senhas."));
        if(bundle.FormatVersion=="ConectaTI-Portable-2.0")
        {
            var referenced=bundle.Connections.Select(c=>c.CredentialId).Where(id=>!string.IsNullOrEmpty(id)).ToHashSet();
            if(bundle.Credentials.Any(c=>!referenced.Contains(c.Id) || string.IsNullOrEmpty(c.PlainSecretForEncryptedBundleOnly)) ||
                (!bundle.IncludesPasswords && (bundle.Credentials.Count>0 || referenced.Count>0)))
                throw new InvalidDataException(L.Text("Pacote com vínculos de credenciais inválidos."));
        }
    }
}
