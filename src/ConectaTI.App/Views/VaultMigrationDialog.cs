using System.Windows;
using System.Windows.Controls;
using ConectaTI.App.Security;

namespace ConectaTI.App.Views;

public sealed class VaultMigrationDialog : Window
{
    private readonly PasswordBox _password = new() { Margin=new Thickness(0,12,0,12) };
    private readonly TextBlock _status = new() { TextWrapping=TextWrapping.Wrap };
    public VaultMigrationDialog(CredentialVaultService vault)
    {
        Title=L.Text("Migrar senhas do formato anterior"); Width=520; Height=300; MinWidth=420;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty,"AppBackgroundBrush");
        SetResourceReference(ForegroundProperty,"PrimaryTextBrush");
        var panel=new StackPanel {Margin=new Thickness(20)};
        panel.Children.Add(new TextBlock {Text=L.Text("Informe a senha mestra anterior uma única vez para migrar as senhas para a proteção automática deste usuário Windows. Cancelar preserva os dados e permite tentar novamente."),
            TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(_password);
        var buttons=new WrapPanel();
        var migrate=new Button {Content=L.Text("Migrar senhas"),Padding=new Thickness(12,6,12,6),IsDefault=true};
        var cancel=new Button {Content=L.Text("Cancelar"),Padding=new Thickness(12,6,12,6),Margin=new Thickness(8,0,0,0),IsCancel=true};
        buttons.Children.Add(migrate);buttons.Children.Add(cancel);panel.Children.Add(buttons);panel.Children.Add(_status);
        Content=panel;
        var busy=false;
        Closing+=(_,e)=>{if(busy)e.Cancel=true;};
        Closed+=(_,_)=>_password.Clear();
        migrate.Click+=async (_,_) =>
        {
            if(busy)return; busy=true; migrate.IsEnabled=false;cancel.IsEnabled=false;
            var success=false;
            try { await vault.MigratePreviousVaultAsync(_password.Password); success=true; }
            catch(Exception ex) { _status.Text=L.Text("Não foi possível migrar. Confira a senha anterior. Os dados originais foram preservados."); vault.RecordMigrationFailure(ex); }
            finally {busy=false;migrate.IsEnabled=true;cancel.IsEnabled=true;}
            if(success)DialogResult=true;
        };
    }
}
