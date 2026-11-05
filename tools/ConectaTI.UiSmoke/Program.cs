using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using ConectaTI.App;
using ConectaTI.App.Data;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Diagnostics;
using ConectaTI.App.Services.ImportExport;
using ConectaTI.App.Services.Launchers;
using ConectaTI.App.ViewModels;

// Runs the real window with isolated fictional data, bypassing legacy-data discovery.
internal static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ProductionApp.xaml"))
            .Replace("clr-namespace:ConectaTI.App.Helpers", "clr-namespace:ConectaTI.App.Helpers;assembly=ConectaTI");
        var dictionary = XDocument.Parse(source).Descendants()
            .First(e => e.Name.LocalName == "ResourceDictionary");
        dictionary.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        dictionary.SetAttributeValue(XNamespace.Xmlns + "helpers", "clr-namespace:ConectaTI.App.Helpers;assembly=ConectaTI");
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var navigationTest = Environment.GetCommandLineArgs().Contains("--navigation");
                var refactorTest = Environment.GetCommandLineArgs().Contains("--refactor-checks");
                var activityTest = Environment.GetCommandLineArgs().Contains("--activity");
                var captureArg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--capture-181="));
                var captureDir = captureArg != null ? captureArg["--capture-181=".Length..] : null;
                var paths = new AppPaths(refactorTest || captureDir != null
                    ? Path.Combine(Path.GetTempPath(), "ConectaTI-Refactor-Ui-" + Guid.NewGuid().ToString("N"))
                    : Path.Combine(AppContext.BaseDirectory, Environment.GetCommandLineArgs().Contains("--delivery160") ? "delivery160-data" : activityTest ? "activity-data" : navigationTest ? "navigation-data" : "fictional-data"));
                var logger = new AppLogger(paths);
                var settings = new SettingsService(paths, logger);
                settings.Load();
                var args = Environment.GetCommandLineArgs();
                var fontArg = args.FirstOrDefault(a => a.StartsWith("--font="));
                if (fontArg != null) settings.Current.FontSize = double.Parse(fontArg[7..], System.Globalization.CultureInfo.InvariantCulture);
                if (args.Contains("--english")) settings.Current.Language = AppLanguage.English;
                if (args.Contains("--spanish")) settings.Current.Language = AppLanguage.Spanish;
                if (args.Contains("--dark")) settings.Current.Theme = AppThemeMode.Dark;
                ThemeService.ApplyTheme(settings.Current.Theme);
                ConectaTI.App.Localization.LocalizationService.Current.SetLanguage(settings.Current.Language);
                ViewPreferencesService.Apply(settings.Current);
                var repository = new SqliteRepository(paths, logger);
                await Task.Run(repository.InitializeAndMigrate);
                if (activityTest && (await repository.GetAllConnectionsAsync(true)).Count == 0)
                    await repository.SaveConnectionsBatchAsync([
                        new ConnectionEntry { Id="activity-a", Name="Avícola Ativo", Host="active.example.test", ClientName="Cliente fictício", IsActive=true },
                        new ConnectionEntry { Id="activity-b", Name="Avícola Inativo", Host="inactive.example.test", ClientName="Cliente fictício", IsActive=false, Environment=EnvironmentType.Testing },
                        new ConnectionEntry { Id="activity-c", Name="Servidor ativo", Host="other.example.test", ClientName="Cliente fictício", IsActive=true }
                    ]);
                if (navigationTest && (await repository.GetAllConnectionsAsync()).Count == 0)
                    await repository.SaveConnectionsBatchAsync(Enumerable.Range(0, 180).Select(i => new ConnectionEntry
                    {
                        Id = $"nav-{i:D3}", Name = i < 60 ? $"Acesso {i:D3}" : i < 70 ? $"Avícola {i:D3}" : $"Servidor {i:D3}",
                        Host = $"test-{i}.example.test", ClientName = "Cliente fictício", IsFavorite = i % 2 == 0, IsActive = i % 3 != 0
                    }));
                if (captureDir != null && (await repository.GetAllConnectionsAsync(true)).Count == 0)
                {
                    await repository.SaveConnectionsBatchAsync(Enumerable.Range(1, 24).Select(i => new ConnectionEntry
                    {
                        Id = $"cap-{i:D2}",
                        Name = i <= 6 ? $"Avícola Unidade {i:D2}" : $"Servidor Suporte {i:D2}",
                        Host = $"10.40.{i}.10",
                        Port = i % 4 == 0 ? 22 : 3389,
                        Protocol = i % 4 == 0 ? ConnectionProtocol.Ssh : ConnectionProtocol.Rdp,
                        Username = "suporte.operador",
                        Domain = i % 4 == 0 ? "" : "CORP",
                        ClientName = i <= 6 ? "Avícola Fictícia" : "Matriz Operações",
                        GroupName = i <= 6 ? "Expedição" : "Infraestrutura",
                        Environment = i % 3 == 0 ? EnvironmentType.Staging : EnvironmentType.Production,
                        IsFavorite = i <= 4,
                        IsActive = i != 3 && i != 6,
                        Notes = "Ambiente fictício de homologação 1.8.1 (executável único ConectaTI.exe)."
                    }));
                }
                if ((await repository.GetAllConnectionsAsync(true)).Count == 0)
                    await repository.SaveConnectionsBatchAsync([
                        new ConnectionEntry { Id = "ui-alpha", Name = "Alpha fictício", Host = "alpha.example.test", ClientName = "Cliente teste", IsFavorite = true, RdpOptions = new() { GatewayHostname = "gateway.example.test" }, VpnName = "VPN fictícia" },
                        new ConnectionEntry { Id = "ui-beta", Name = "Beta fictício", Host = "beta.example.test", ClientName = "Cliente teste", LastOpenedAtUtc = DateTime.UtcNow },
                        new ConnectionEntry { Id = "ui-trash", Name = "Lixeira fictícia", Host = "trash.example.test", ClientName = "Cliente teste", IsDeleted = true, DeletedAtUtc = DateTime.UtcNow }
                    ]);
                var vault = new CredentialVaultService(settings, repository, logger, useWindowsVault: false);
                string? copied = null;
                var clipboard = refactorTest
                    ? new ClipboardService(logger, text => { copied = text; return true; }, () => copied, () => copied = null)
                    : new ClipboardService(logger);
                var vm = new MainViewModel(paths, logger, settings, repository, vault, clipboard,
                    new ConnectionLauncherService(paths, settings, repository, vault, logger),
                    new ImportExportService(repository, vault, logger), new NetworkDiagnosticService());
                await vm.InitializeAsync();
                HotKeyTextService? captureQuickTexts = null;
                if (captureDir != null)
                {
                    var qtDir = Path.Combine(paths.RootDirectory, "HotKeyText");
                    Directory.CreateDirectory(qtDir);
                    File.WriteAllText(Path.Combine(qtDir, "textos.xml"), """
                        <?xml version="1.0" encoding="utf-8"?>
                        <Config>
                          <ModoColar>false</ModoColar>
                          <TemaEscuro>false</TemaEscuro>
                          <Textos>
                            <TextoItem>
                              <Titulo>Saudação Atendimento</Titulo>
                              <Conteudo>Olá! Aqui é a equipe de suporte técnico. Como posso ajudar?</Conteudo>
                              <Atalho>1</Atalho>
                              <OcultarTexto>false</OcultarTexto>
                            </TextoItem>
                            <TextoItem>
                              <Titulo>Diagnóstico de Rede</Titulo>
                              <Conteudo>ipconfig /all &amp;&amp; ping 10.40.1.10 -n 4</Conteudo>
                              <Atalho>2</Atalho>
                              <OcultarTexto>false</OcultarTexto>
                            </TextoItem>
                            <TextoItem>
                              <Titulo>Credencial Homologação</Titulo>
                              <Conteudo>TokenFicticio#Homologacao181</Conteudo>
                              <Atalho>Shift+3</Atalho>
                              <OcultarTexto>true</OcultarTexto>
                            </TextoItem>
                            <TextoItem>
                              <Titulo>Encerramento Chamado</Titulo>
                              <Conteudo>Acesso validado e chamado concluído com sucesso.</Conteudo>
                              <Atalho>4</Atalho>
                              <OcultarTexto>false</OcultarTexto>
                            </TextoItem>
                          </Textos>
                        </Config>
                        """);
                    captureQuickTexts = new HotKeyTextService(
                        customDataDirectory: qtDir,
                        instancePrefix: "CTI_Cap181_" + Guid.NewGuid().ToString("N")[..8] + "_",
                        customRunValueName: "ConectaTI_CapRun_" + Guid.NewGuid().ToString("N")[..8],
                        installGlobalHooks: false);
                }
                var window = captureQuickTexts != null
                    ? new MainWindow(vm, captureQuickTexts) { Title = "ConectaTI 1.8.1", Width = 1120, Height = 740, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 }
                    : new MainWindow(vm) { Title = "ConectaTI — Teste isolado", Width = args.Contains("--narrow") ? 620 : 1000, Height = 760 };
                app.MainWindow = window;
                window.Closed += (_, _) => { captureQuickTexts?.Dispose(); app.Shutdown(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); };
                window.Show();
                if (activityTest || refactorTest || args.Contains("--delivery160")) { window.Left = 10; window.Top = 10; }
                if (captureDir != null)
                {
                    Directory.CreateDirectory(captureDir);
                    async Task FlushRenderAsync()
                    {
                        window.UpdateLayout();
                        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                        await Task.Delay(120);
                        window.UpdateLayout();
                    }
                    void SaveElementPng(FrameworkElement elem, string filePath, double dpi = 144.0)
                    {
                        var w = Math.Max(1, (int)Math.Ceiling(elem.ActualWidth * dpi / 96.0));
                        var h = Math.Max(1, (int)Math.Ceiling(elem.ActualHeight * dpi / 96.0));
                        var rtb = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
                        rtb.Render(elem);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(rtb));
                        using var fs = File.Create(filePath);
                        enc.Save(fs);
                    }

                    var rootGrid = (FrameworkElement)window.Content;

                    // 1. Tema Claro - Painel Recolhido
                    settings.Current.Theme = AppThemeMode.Light;
                    ThemeService.ApplyTheme(AppThemeMode.Light, app.Resources);
                    vm.RefreshThemeProperties();
                    window.CollapseQuickTextsPanel();
                    await FlushRenderAsync();
                    SaveElementPng(rootGrid, Path.Combine(captureDir, "1-tema-claro-painel-recolhido.png"));

                    // 2. Tema Claro - Painel Aberto
                    window.OpenQuickTextsPanel();
                    await FlushRenderAsync();
                    SaveElementPng(rootGrid, Path.Combine(captureDir, "2-tema-claro-painel-aberto.png"));

                    // 3. Tema Escuro - Painel Recolhido
                    settings.Current.Theme = AppThemeMode.Dark;
                    ThemeService.ApplyTheme(AppThemeMode.Dark, app.Resources);
                    vm.RefreshThemeProperties();
                    window.CollapseQuickTextsPanel();
                    await FlushRenderAsync();
                    SaveElementPng(rootGrid, Path.Combine(captureDir, "3-tema-escuro-painel-recolhido.png"));

                    // 4. Tema Escuro - Painel Aberto
                    window.OpenQuickTextsPanel();
                    await FlushRenderAsync();
                    SaveElementPng(rootGrid, Path.Combine(captureDir, "4-tema-escuro-painel-aberto.png"));

                    // 5. Janela Estreita (620px) - Painel sobre os detalhes mantendo a lista acessível
                    window.Width = 620;
                    window.UpdateAdaptiveHeaderAndPaneBounds(620);
                    await FlushRenderAsync();
                    SaveElementPng(rootGrid, Path.Combine(captureDir, "5-janela-estreita-painel-sobre-detalhes.png"));

                    window.Close();
                    return;
                }
                if (refactorTest)
                {
                    await ConectaTI.UiSmoke.RefactorChecks.RunAsync(window, vm, repository, vault, () => copied);
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "refactor-checks.json"),
                        System.Text.Json.JsonSerializer.Serialize(new { Result = "PASS", DataDirectory = paths.RootDirectory,
                            Checks = new[] { "drafts-by-id", "selection-and-secret-isolation", "copy-collapsed-unsaved-data", "explicit-save",
                                "credential-dpapi", "rdp-safe-preview", "reactivate-inactivate", "new-draft", "side-by-side-font-11-18", "themes", "discard-without-autosave" } }));
                    window.Close();
                }
                if (args.Contains("--navigation-checks"))
                {
                    await ConectaTI.UiSmoke.NavigationChecks.RunAsync(window, vm);
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "navigation-checks.txt"), "PASS: search, focus, arrows, timer, virtualized distant selection, bounds, filtered view, Search, extended selection, editor focus.");
                    window.Close();
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-smoke-error.txt"), ex.ToString());
                Environment.ExitCode = 1;
                app.Shutdown(1);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
    }

}





