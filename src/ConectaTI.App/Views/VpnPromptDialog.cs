using System.Windows;
using System.Windows.Controls;
using ConectaTI.App.Localization;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services.Diagnostics;
using ConectaTI.App.Services.Launchers;

namespace ConectaTI.App.Views;

public sealed class VpnPromptDialog : Window
{
    public VpnUnreachableDecision Decision { get; private set; } = VpnUnreachableDecision.Cancel;

    public VpnPromptDialog(
        ConnectionEntry entry,
        NetworkDiagnosticResult probeResult,
        string resolvedClientDisplayName,
        ITextClipboard? clipboard = null)
    {
        LocalizedWindow.Attach(this);
        Title = L.Text("ConectaTI — VPN do Cliente");
        Width = 540;
        MinWidth = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        SetResourceReference(FontSizeProperty, "AppFontSize");

        var root = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };

        var titleText = new TextBlock
        {
            Text = L.Text("Servidor sem resposta — VPN configurada para este cliente"),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        titleText.SetResourceReference(TextBlock.FontSizeProperty, "Font14");
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        root.Children.Add(titleText);

        var targetSummary = new TextBlock
        {
            Text = L.Format(
                "O acesso '{0}' ({1}:{2}) não respondeu na verificação rápida ({3}). Caso a VPN esteja desconectada, abra o cliente VPN antes de iniciar a sessão.",
                string.IsNullOrWhiteSpace(entry.Name) ? entry.Host : entry.Name,
                entry.Host,
                entry.Port,
                probeResult.SummaryTitle),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        targetSummary.SetResourceReference(TextBlock.FontSizeProperty, "Font12");
        targetSummary.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        root.Children.Add(targetSummary);

        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 14)
        };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var cardStack = new StackPanel();
        var copyFeedback = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed
        };
        copyFeedback.SetResourceReference(TextBlock.FontSizeProperty, "Font11");
        copyFeedback.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");

        void AddInfoRow(string label, string? value, string? copyValue = null)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
            if (!string.IsNullOrWhiteSpace(copyValue) && clipboard is not null)
            {
                var copyBtn = new Button
                {
                    Content = "📋",
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = L.Text("Copiar")
                };
                copyBtn.Click += async (_, _) =>
                {
                    if (await clipboard.CopyTextWithRetryAsync(copyValue!))
                    {
                        copyFeedback.Text = L.Format("Copiado: {0}", copyValue!);
                        copyFeedback.Visibility = Visibility.Visible;
                    }
                };
                DockPanel.SetDock(copyBtn, Dock.Right);
                row.Children.Add(copyBtn);
            }

            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            line.Inlines.Add(new System.Windows.Documents.Run($"{label}: ") { FontWeight = FontWeights.SemiBold });
            line.Inlines.Add(new System.Windows.Documents.Run(value.Trim()));
            row.Children.Add(line);
            cardStack.Children.Add(row);
        }

        AddInfoRow(L.Text("Nome da VPN"), entry.VpnName, entry.VpnName);
        var appDisplay = string.IsNullOrWhiteSpace(entry.VpnApp)
            ? resolvedClientDisplayName
            : (string.Equals(entry.VpnApp.Trim(), resolvedClientDisplayName, StringComparison.OrdinalIgnoreCase)
                ? resolvedClientDisplayName
                : $"{entry.VpnApp.Trim()} ({resolvedClientDisplayName})");
        AddInfoRow(L.Text("Aplicativo VPN"), appDisplay);
        AddInfoRow(L.Text("Servidor VPN"), entry.VpnServer, entry.VpnServer);
        AddInfoRow(L.Text("Usuário VPN"), entry.VpnUsername, entry.VpnUsername);
        AddInfoRow(L.Text("Observações VPN"), entry.VpnNotes);
        cardStack.Children.Add(copyFeedback);

        card.Child = cardStack;
        root.Children.Add(card);

        var footerDock = new DockPanel();
        var cancelBtn = new Button
        {
            Content = L.Text("Cancelar"),
            IsCancel = true,
            Padding = new Thickness(12, 6, 12, 6)
        };
        cancelBtn.Click += (_, _) =>
        {
            Decision = VpnUnreachableDecision.Cancel;
            DialogResult = false;
            Close();
        };
        DockPanel.SetDock(cancelBtn, Dock.Right);

        var actionsWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        var openVpnBtn = new Button
        {
            Content = L.Text("Conectar VPN"),
            IsDefault = true,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 8, 0),
            FontWeight = FontWeights.SemiBold
        };
        if (Application.Current?.TryFindResource("PrimaryButtonStyle") is Style primaryStyle)
            openVpnBtn.Style = primaryStyle;
        openVpnBtn.Click += (_, _) =>
        {
            Decision = VpnUnreachableDecision.OpenVpn;
            DialogResult = true;
            Close();
        };

        var connectAnywayBtn = new Button
        {
            Content = L.Text("Conectar mesmo assim"),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0)
        };
        connectAnywayBtn.Click += (_, _) =>
        {
            Decision = VpnUnreachableDecision.ConnectAnyway;
            DialogResult = true;
            Close();
        };

        actionsWrap.Children.Add(openVpnBtn);
        actionsWrap.Children.Add(connectAnywayBtn);

        footerDock.Children.Add(cancelBtn);
        footerDock.Children.Add(actionsWrap);
        root.Children.Add(footerDock);

        Content = root;
    }
}
