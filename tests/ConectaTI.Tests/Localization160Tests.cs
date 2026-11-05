using ConectaTI.App.Localization;
using ConectaTI.App.Models;
using ConectaTI.App.Services;
namespace ConectaTI.Tests;
public sealed class Localization160Tests
{
    [Theory]
    [InlineData(AppLanguage.Portuguese,"Copiar todos os dados","Servidor","Sim")]
    [InlineData(AppLanguage.English,"Copy all data","Server","Yes")]
    [InlineData(AppLanguage.Spanish,"Copiar todos los datos","Servidor","Sí")]
    public void NewActionsCopyAndValidationFollowUiLanguageWithoutTranslatingData(AppLanguage language,string copy,string server,string yes)
    {
        try
        {
            LocalizationService.Current.SetLanguage(language);
            Assert.Equal(copy,L.Text("Copiar todos os dados"));
            var entry=new ConnectionEntry{Name="Salvar Produção",Host="host.example.test",RdpAdminSession=true};
            entry.RdpOptions.GatewayHostname="gateway.example.test";entry.RdpOptions.GatewayCredentialsSource=2;
            var text=ConnectionCopyFormatter.Format(entry,"host.example.test","synthetic","SYNTHETIC_ONLY");
            Assert.Contains(server+": gateway.example.test",text);Assert.Contains("Salvar Produção",text);
            Assert.Contains("SYNTHETIC_ONLY",text);Assert.Contains(yes,text);
            if(language!=AppLanguage.Portuguese) {Assert.DoesNotContain("Modo de utilização",text);Assert.DoesNotContain("Origem das credenciais",text);}
            Assert.Contains("gatewaycredentialssource:i:2",RdpFileService.Build(entry));
            var error=Assert.Throws<FormatException>(()=>RdpFileService.Parse("enablecredsspsupport:i:00"));
            Assert.Contains("enablecredsspsupport",error.Message);
            if(language==AppLanguage.English)Assert.Contains("Invalid RDP property",error.Message);
            if(language==AppLanguage.Spanish)Assert.Contains("Propiedad RDP",error.Message);
        }
        finally {LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);}
    }
}