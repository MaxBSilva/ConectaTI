using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ConectaTI.App;
using ConectaTI.App.Data;
using ConectaTI.App.Helpers;
using ConectaTI.App.Localization;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Diagnostics;
using ConectaTI.App.Services.ImportExport;
using ConectaTI.App.Services.Launchers;
using ConectaTI.App.Services.Updates;
using ConectaTI.App.ViewModels;
using ConectaTI.App.Views;
using Microsoft.Data.Sqlite;

namespace ConectaTI.Tests;

public sealed class ActivityFilterHotKeyAndUpdateTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "ConectaTI-Suite180-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly AppLogger _logger;
    private readonly SqliteRepository _repo;
    private readonly SettingsService _settings;
    private readonly CredentialVaultService _vault;
    private readonly ImportExportService _transfer;
    private readonly ClipboardService _clipboard;

    public ActivityFilterHotKeyAndUpdateTests()
    {
        Directory.CreateDirectory(_tempRoot);
        LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);
        _paths = new AppPaths(Path.Combine(_tempRoot, "data"));
        _logger = new AppLogger(_paths);
        _repo = new SqliteRepository(_paths, _logger);
        _repo.InitializeAndMigrate();
        _settings = new SettingsService(_paths, _logger);
        _settings.Load();
        _vault = new CredentialVaultService(_settings, _repo, _logger, useWindowsVault: false);
        _transfer = new ImportExportService(_repo, _vault, _logger);
        _clipboard = new ClipboardService(_logger, _ => true, () => null, () => { });
    }

    private MainViewModel CreateVm() => new(
        _paths,
        _logger,
        _settings,
        _repo,
        _vault,
        _clipboard,
        new ConnectionLauncherService(_paths, _settings, _repo, _vault, _clipboard, _logger),
        _transfer,
        new NetworkDiagnosticService());

    [Fact]
    public async Task ActivityFilter_DefaultsToAll_CombinesWithSearchAndViewModes_AvicolaInactiveExample()
    {
        var entries = new[]
        {
            new ConnectionEntry
            {
                Name = "Avícola Alfa Matriz",
                Host = "10.20.1.10",
                IsActive = true,
                IsFavorite = true
            },
            new ConnectionEntry
            {
                Name = "Avícola Beta Filial",
                Host = "10.20.1.11",
                IsActive = false,
                IsFavorite = true,
                LastOpenedAtUtc = DateTime.UtcNow.AddMinutes(-10)
            },
            new ConnectionEntry
            {
                Name = "Avícola Gama Expedição",
                Host = "10.20.1.12",
                IsActive = false,
                IsFavorite = false
            },
            new ConnectionEntry
            {
                Name = "Frigorífico Sul Inativo",
                Host = "10.20.2.50",
                IsActive = false,
                IsFavorite = true
            },
            new ConnectionEntry
            {
                Name = "Avícola Descartada Lixeira",
                Host = "10.20.1.99",
                IsActive = false,
                IsDeleted = true
            }
        };

        foreach (var e in entries)
            await _repo.SaveConnectionAsync(e);

        var vm = CreateVm();
        await vm.InitializeAsync(seedDemoIfEmpty: false);

        // Padrão deve ser Todos (ConnectionActivityFilter.All)
        Assert.Equal(ConnectionActivityFilter.All, vm.CurrentActivityFilter);
        Assert.Equal(4, vm.FilteredConnections.Count);

        // Exemplo explícito: pesquisar "avicola" e escolher Inativos mostra somente os acessos inativos que correspondem à pesquisa
        vm.SearchText = "avicola";
        vm.SetActivityFilterCommand.Execute(ConnectionActivityFilter.Inactive);

        Assert.Equal(ConnectionActivityFilter.Inactive, vm.CurrentActivityFilter);
        Assert.Equal(2, vm.FilteredConnections.Count);
        Assert.Equal(["Avícola Beta Filial", "Avícola Gama Expedição"], vm.FilteredConnections.Select(c => c.Name).ToArray());
        Assert.All(vm.FilteredConnections, c =>
        {
            Assert.False(c.IsActive);
            Assert.Equal("Inativo", c.ActivityDisplay);
        });

        // Combinar com Favoritos + pesquisa "avicola" + Inativos
        vm.SetViewModeCommand.Execute(ConnectionViewMode.Favorites);
        Assert.Single(vm.FilteredConnections);
        Assert.Equal("Avícola Beta Filial", vm.FilteredConnections[0].Name);

        // Combinar com Favoritos + pesquisa "avicola" + Ativos (independente de AvailabilityState == Offline)
        vm.SetActivityFilterCommand.Execute(ConnectionActivityFilter.Active);
        Assert.Single(vm.FilteredConnections);
        Assert.Equal("Avícola Alfa Matriz", vm.FilteredConnections[0].Name);

        // Combinar com Recentes + Inativos
        vm.SearchText = string.Empty;
        vm.SetViewModeCommand.Execute(ConnectionViewMode.Recent);
        vm.SetActivityFilterCommand.Execute(ConnectionActivityFilter.Inactive);
        Assert.Single(vm.FilteredConnections);
        Assert.Equal("Avícola Beta Filial", vm.FilteredConnections[0].Name);

        // Independente da Lixeira: na visão Lixeira mostra apenas itens excluídos conforme o filtro
        vm.SetViewModeCommand.Execute(ConnectionViewMode.Trash);
        vm.SetActivityFilterCommand.Execute(ConnectionActivityFilter.Inactive);
        Assert.Single(vm.FilteredConnections);
        Assert.Equal("Avícola Descartada Lixeira", vm.FilteredConnections[0].Name);
    }

    [Fact]
    public async Task ActivityFilter_SelectionFocusScrollDrafts_ArrowNavigation_AndSaveStatusUpdate()
    {
        var items = new[]
        {
            new ConnectionEntry { Name = "Avícola 01 Ativa", Host = "10.0.0.1", IsActive = true },
            new ConnectionEntry { Name = "Avícola 02 Inativa", Host = "10.0.0.2", IsActive = false },
            new ConnectionEntry { Name = "Avícola 03 Ativa", Host = "10.0.0.3", IsActive = true },
            new ConnectionEntry { Name = "Avícola 04 Inativa", Host = "10.0.0.4", IsActive = false },
            new ConnectionEntry { Name = "Avícola 05 Inativa", Host = "10.0.0.5", IsActive = false }
        };

        foreach (var item in items)
            await _repo.SaveConnectionAsync(item);

        Exception? staException = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

                RunAsyncOnSta(async () =>
                {
                    var vm = CreateVm();
                    await vm.InitializeAsync(seedDemoIfEmpty: false);

                    var listBox = new ListBox
                    {
                        Width = 240,
                        Height = 140,
                        SelectionMode = SelectionMode.Extended
                    };
                    listBox.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding(nameof(MainViewModel.FilteredConnections))
                    {
                        Source = vm,
                        Mode = System.Windows.Data.BindingMode.OneWay
                    });
                    listBox.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new System.Windows.Data.Binding(nameof(MainViewModel.SelectedConnection))
                    {
                        Source = vm,
                        Mode = System.Windows.Data.BindingMode.TwoWay
                    });

                    vm.FilteredListUpdated += () =>
                    {
                        if (vm.SelectedConnection is not null && vm.FilteredConnections.Contains(vm.SelectedConnection))
                            ConnectionListNavigation.SelectAndFocus(listBox, vm.SelectedConnection);
                        else
                        {
                            if (listBox.SelectionMode != SelectionMode.Single)
                                listBox.SelectedItems.Clear();
                            listBox.SelectedItem = null;
                            listBox.SelectedIndex = -1;
                        }
                    };

                    listBox.Measure(new System.Windows.Size(240, 140));
                    listBox.Arrange(new System.Windows.Rect(0, 0, 240, 140));
                    listBox.UpdateLayout();

                    // Seleciona "Avícola 01 Ativa" e cria rascunho não salvo em memória
                    vm.SelectedConnection = vm.FilteredConnections[0];
                    ConnectionListNavigation.SelectAndFocus(listBox, vm.SelectedConnection);
                    vm.EditingEntry.Name = "Avícola 01 Ativa (Rascunho Pendente)";
                    vm.UpdatePasswordFromUser("SegredoPendenteFicticio");
                    Assert.True(vm.CanSave);
                    Assert.True(vm.HasAnyUnsavedDrafts);

                    // Ao filtrar por Ativos, "Avícola 01 Ativa" continua visível e permanece selecionada com seu rascunho
                    vm.SetActivityFilterCommand.Execute(ConnectionActivityFilter.Active);
                    Assert.Equal("Avícola 01 Ativa", vm.SelectedConnection?.Name);
                    Assert.Equal("Avícola 01 Ativa (Rascunho Pendente)", vm.EditingEntry.Name);

                    // Ao mudar para Inativos, "Avícola 01 Ativa" deixa de estar visível -> seleção vai para o 1º inativo visível ("Avícola 02 Inativa")
                    // e o rascunho de "Avícola 01 Ativa" permanece preservado em memória!
                    vm.SetActivityFilterCommand.Execute(ConnectionActivityFilter.Inactive);
                    Assert.Equal(3, vm.FilteredConnections.Count);
                    Assert.Equal("Avícola 02 Inativa", vm.SelectedConnection?.Name);
                    Assert.Equal("Avícola 02 Inativa", listBox.SelectedItem is ConnectionEntry ce ? ce.Name : null);

                    // As setas devem mover um resultado visível por vez
                    ConnectionListNavigation.Move(listBox, +1);
                    Assert.Equal("Avícola 04 Inativa", vm.SelectedConnection?.Name);
                    ConnectionListNavigation.Move(listBox, +1);
                    Assert.Equal("Avícola 05 Inativa", vm.SelectedConnection?.Name);
                    ConnectionListNavigation.Move(listBox, -1);
                    Assert.Equal("Avícola 04 Inativa", vm.SelectedConnection?.Name);

                    // Ao salvar uma alteração de situação (Inativo -> Ativo) enquanto o filtro Inativos está ativo,
                    // o acesso sai da lista filtrada de Inativos e a seleção ajusta para um item visível
                    vm.EditingEntry.IsActive = true;
                    Assert.True(await vm.SaveConnectionAsync());
                    Assert.Equal(2, vm.FilteredConnections.Count);
                    Assert.DoesNotContain(vm.FilteredConnections, c => c.Name == "Avícola 04 Inativa");
                    Assert.NotNull(vm.SelectedConnection);
                    Assert.Contains(vm.SelectedConnection!, vm.FilteredConnections);

                    // Voltando para Todos, "Avícola 01 Ativa" recupera seu rascunho e senha pendente intactos!
                    vm.SetActivityFilterCommand.Execute(ConnectionActivityFilter.All);
                    var avicola01 = vm.FilteredConnections.First(c => c.Id == items[0].Id);
                    vm.SelectedConnection = avicola01;
                    Assert.Equal("Avícola 01 Ativa (Rascunho Pendente)", vm.EditingEntry.Name);
                    Assert.Equal("SegredoPendenteFicticio", vm.InlinePasswordInput);

                    // Se a pesquisa deixar a lista vazia, limpa a seleção
                    vm.SearchText = "inexistente-xyz-999";
                    Assert.Empty(vm.FilteredConnections);
                    Assert.Null(vm.SelectedConnection);
                    Assert.Equal(-1, listBox.SelectedIndex);
                }, dispatcher);
            }
            catch (Exception ex)
            {
                staException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (staException is not null)
            throw new AggregateException("Falha no teste STA de filtro/navegação.", staException);
    }

    [Fact]
    public void InProcessHotKeyText_Panel_ConflictDetection_TargetInsertion_AndThemeGearScrollbarAdjustments()
    {
        // Confirma que a compilação normal gera apenas ConectaTI.exe (sem HotKeyText.exe avulso na saída)
        var strayHotKeyExe = Path.Combine(AppContext.BaseDirectory, "HotKeyText.exe");
        Assert.False(File.Exists(strayHotKeyExe), $"HotKeyText.exe não deve existir na saída de build ('{strayHotKeyExe}').");

        Exception? staException = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var app = new ConectaTI.App.App { IsIsolatedHost = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.InitializeComponent();
                }

                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

                RunAsyncOnSta(async () =>
                {
                    var paths = new AppPaths(Path.Combine(_tempRoot, "ui-inprocess-hotkey"));
                    var logger = new AppLogger(paths);
                    var settings = new SettingsService(paths, logger);
                    settings.Load();
                    LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);
                    ThemeService.ApplyTheme(AppThemeMode.Dark, Application.Current!.Resources);
                    ViewPreferencesService.Apply(settings.Current);

                    var repository = new SqliteRepository(paths, logger);
                    repository.InitializeAndMigrate();
                    var vault = new CredentialVaultService(settings, repository, logger, useWindowsVault: false);
                    var clipboard = new ClipboardService(logger, _ => true, () => string.Empty, () => { });
                    var launcher = new ConnectionLauncherService(paths, settings, repository, vault, logger);
                    var importExport = new ImportExportService(repository, vault, logger);
                    var diagnostics = new NetworkDiagnosticService();

                    // Prepara um textos.xml pré-existente compatível com %APPDATA%\HotKeyText\textos.xml
                    var quickTextsDir = Path.Combine(_tempRoot, "appdata-hotkeytext");
                    Directory.CreateDirectory(quickTextsDir);
                    var preExistingXml = Path.Combine(quickTextsDir, "textos.xml");
                    File.WriteAllText(preExistingXml, """
                        <?xml version="1.0" encoding="utf-8"?>
                        <Config>
                          <ModoColar>false</ModoColar>
                          <TemaEscuro>true</TemaEscuro>
                          <Textos>
                            <TextoItem>
                              <Titulo>Saudação Suporte</Titulo>
                              <Conteudo>Olá, equipe fictícia!</Conteudo>
                              <Atalho>1</Atalho>
                              <OcultarTexto>false</OcultarTexto>
                            </TextoItem>
                            <TextoItem>
                              <Titulo>Token Homologação</Titulo>
                              <Conteudo>SegredoFicticio#2026</Conteudo>
                              <Atalho>Shift+1</Atalho>
                              <OcultarTexto>true</OcultarTexto>
                            </TextoItem>
                          </Textos>
                        </Config>
                        """, Encoding.UTF8);

                    var prefix = "CTI_InProc_" + Guid.NewGuid().ToString("N")[..8] + "_";
                    var runKeyName = "ConectaTI_TestRun_" + Guid.NewGuid().ToString("N")[..8];

                    // 1. Simula edição avulsa do HotKeyText já em execução segurando o Mutex
                    using var simulatedStandaloneMutex = new Mutex(true, prefix + HotKeyTextService.DefaultMutexName, out var createdStandalone);
                    Assert.True(createdStandalone);

                    using var quickTextsService = new HotKeyTextService(
                        customDataDirectory: quickTextsDir,
                        instancePrefix: prefix,
                        customRunValueName: runKeyName,
                        installGlobalHooks: false);

                    // Ao tentar ativar com a edição avulsa aberta, deve identificar o conflito e NÃO ativar os atalhos
                    Assert.False(quickTextsService.TryActivateShortcuts(installNativeHook: false));
                    Assert.True(quickTextsService.HasStandaloneConflict);
                    Assert.False(quickTextsService.IsHookActive);
                    Assert.Contains("HotKeyText", quickTextsService.ConflictMessage);

                    // Verifica preservação dos dados de textos.xml (incluindo OcultarTexto / prévia mascarada)
                    Assert.Equal(2, quickTextsService.Config.Textos.Count);
                    Assert.Equal("Saudação Suporte", quickTextsService.Config.Textos[0].Titulo);
                    Assert.Equal("••••••••", quickTextsService.Config.Textos[1].DisplayPreview);
                    Assert.True(quickTextsService.Config.Textos[1].OcultarTexto);

                    var vm = new MainViewModel(paths, logger, settings, repository, vault, clipboard, launcher, importExport, diagnostics);
                    await vm.InitializeAsync(seedDemoIfEmpty: true);

                    var window = new MainWindow(vm, quickTextsService)
                    {
                        Width = 1040,
                        Height = 700
                    };
                    window.Show();
                    await dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);

                    // 2. Valida ajustes de Tema, Configurações (engrenagem com dentes e círculo central) e barra central
                    var themeBtnLabel = (TextBlock)window.FindName("ThemeButtonLabel");
                    var menuThemeItem = (MenuItem)window.FindName("MenuToggleThemeItem");
                    var gearBtn = (Button)window.FindName("SettingsGearButton");
                    var gearPath = (System.Windows.Shapes.Path)window.FindName("SettingsGearIconPath");
                    var splitter = (GridSplitter)window.FindName("MainPanelsSplitter");

                    Assert.Equal("Tema", themeBtnLabel.Text);
                    Assert.Equal("Tema", menuThemeItem.Header.ToString());
                    Assert.Equal("Configurações", gearBtn.ToolTip.ToString());
                    Assert.NotNull(gearPath.Data);
                    Assert.Equal(6, splitter.Width);

                    var settingsDlg = new ExecutablesSettingsDialog(settings, vault, vm, ExecutablesSettingsDialog.TabAppearanceIndex);
                    var tabs = (TabControl)settingsDlg.FindName("SettingsTabs");
                    var themeTab = (TabItem)tabs.Items[ExecutablesSettingsDialog.TabAppearanceIndex];
                    Assert.Equal("Tema", themeTab.Header.ToString());
                    settingsDlg.Close();

                    // Cria um rascunho de edição em memória no cadastro selecionado e foca o campo Nome
                    var nameBox = (TextBox)window.FindName("ConnectionNameTextBox");
                    nameBox.Focus();
                    vm.EditingEntry.Name = "Rascunho Preservado Durante Textos Rápidos";
                    Assert.True(vm.HasPendingChanges);

                    // 3. Abre o painel pela aba da borda direita (em janela larga: fica lado a lado na Coluna 3)
                    var edgeTabBtn = (Button)window.FindName("QuickTextsEdgeTabButton");
                    edgeTabBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);

                    Assert.True(window.IsQuickTextsPanelOpen);
                    Assert.False(window.IsQuickTextsOverlayingDetails);
                    var subheader = (TextBlock)window.FindName("QuickTextsSubheaderText");
                    var version = AppUpdateService.GetInstalledVersion();
                    Assert.Equal($"Scroll Lock - versão {version}", subheader.Text);
                    LocalizationService.Current.SetLanguage(AppLanguage.English);
                    Assert.Equal($"Scroll Lock - version {version}", subheader.Text);
                    LocalizationService.Current.SetLanguage(AppLanguage.Spanish);
                    Assert.Equal($"Scroll Lock - versión {version}", subheader.Text);
                    LocalizationService.Current.SetLanguage(AppLanguage.Portuguese);
                    var conflictBanner = (Border)window.FindName("QuickTextsConflictBanner");
                    Assert.Equal(Visibility.Visible, conflictBanner.Visibility);

                    // Em janela estreita (620px), o painel abre sobre os detalhes (Coluna 2) mantendo a lista (Coluna 0) acessível
                    window.Width = 620;
                    window.UpdateAdaptiveHeaderAndPaneBounds(620);
                    await dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);
                    Assert.True(window.IsQuickTextsOverlayingDetails);
                    var listBox = (ListBox)window.FindName("ConnectionsListBox");
                    Assert.True(listBox.ActualWidth >= 130, $"A lista de conexões deve permanecer acessível em janela estreita (ActualWidth={listBox.ActualWidth}).");

                    // Volta para janela larga
                    window.Width = 1040;
                    window.UpdateAdaptiveHeaderAndPaneBounds(1040);
                    await dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);
                    Assert.False(window.IsQuickTextsOverlayingDetails);

                    // Encerra a instância avulsa simulada e clica em "Ativar atalhos integrados"
                    simulatedStandaloneMutex.ReleaseMutex();
                    simulatedStandaloneMutex.Dispose();

                    var retryBtn = (Button)window.FindName("QuickTextsRetryActivateButton");
                    retryBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(quickTextsService.HasStandaloneConflict);
                    Assert.True(quickTextsService.IsHookActive);
                    Assert.Equal(Visibility.Collapsed, conflictBanner.Visibility);

                    // 4. Dentro do painel: cadastra novo texto, edita título/conteúdo, configura atalho e ordena
                    var newBtn = (Button)window.FindName("QuickTextsNewButton");
                    var titleBox = (TextBox)window.FindName("QuickTextTitleBox");
                    var contentBox = (TextBox)window.FindName("QuickTextContentBox");
                    var moveUpBtn = (Button)window.FindName("QuickTextsMoveUpButton");
                    var showContentCheck = (CheckBox)window.FindName("QuickTextShowContentCheckBox");

                    newBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(3, quickTextsService.Config.Textos.Count);
                    titleBox.Text = "Comando Diagnóstico";
                    contentBox.Text = "ipconfig /all";
                    quickTextsService.AssignShortcut(2, "Ctrl+2");

                    // Move o item para cima e verifica ordenação
                    moveUpBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("Comando Diagnóstico", quickTextsService.Config.Textos[1].Titulo);

                    // Oculta prévia e verifica máscara
                    showContentCheck.IsChecked = false;
                    showContentCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(quickTextsService.Config.Textos[1].OcultarTexto);
                    Assert.Equal("••••••••", quickTextsService.Config.Textos[1].DisplayPreview);

                    // 5. Recolhe o painel: deve apenas ocultar a interface, mantendo Scroll Lock/atalhos ativos e restaurando foco e rascunho
                    var collapseBtn = (Button)window.FindName("QuickTextsCollapseButton");
                    collapseBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);

                    Assert.False(window.IsQuickTextsPanelOpen);
                    Assert.True(quickTextsService.IsHookActive);
                    Assert.Equal("Rascunho Preservado Durante Textos Rápidos", vm.EditingEntry.Name);
                    Assert.True(vm.HasPendingChanges);
                    Assert.Same(nameBox, Keyboard.FocusedElement);

                    // 6. Valida inserção na janela de destino correta mesmo após abrir o painel para gerenciar os textos
                    // Cria uma janela externa simulando o aplicativo de destino (ex.: Bloco de Notas / cliente externo)
                    var targetWin = new Window
                    {
                        Title = "Janela Destino Externa",
                        Width = 360,
                        Height = 180,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = 40,
                        Top = 40
                    };
                    var targetTextBox = new TextBox { AcceptsReturn = true, AcceptsTab = true };
                    targetWin.Content = targetTextBox;
                    targetWin.Show();
                    targetWin.Activate();
                    targetTextBox.Focus();
                    await dispatcher.InvokeAsync(() => targetWin.UpdateLayout(), DispatcherPriority.ContextIdle);

                    var targetHwnd = new System.Windows.Interop.WindowInteropHelper(targetWin).Handle;
                    quickTextsService.RecordExternalWindowIfValid(targetHwnd);
                    quickTextsService.ExplicitTargetWindowForTest = targetHwnd;

                    // Mesmo abrindo e recolhendo o painel no ConectaTI depois que a janela externa esteve ativa,
                    // o acionamento do Scroll Lock + atalho deve inserir na janela de destino correta
                    window.OpenQuickTextsPanel();
                    window.CollapseQuickTextsPanel();

                    var receivedBuffer = new StringBuilder();
                    quickTextsService.SendInputInterceptorForTest = inputs =>
                    {
                        var ctrlDown = false;
                        foreach (var inp in inputs)
                        {
                            if (inp.type != 1) continue;
                            var isUp = (inp.u.ki.dwFlags & 2) != 0;
                            var isUni = (inp.u.ki.dwFlags & 4) != 0;
                            if (isUni)
                            {
                                if (!isUp) receivedBuffer.Append((char)inp.u.ki.wScan);
                            }
                            else if (inp.u.ki.wVk == 0x11)
                            {
                                ctrlDown = !isUp;
                            }
                            else if (inp.u.ki.wVk == 0x56 && !isUp && ctrlDown)
                            {
                                receivedBuffer.Append(Clipboard.GetText());
                            }
                        }
                    };

                    // Modo Digitar local via Scroll Lock + atalho Ctrl+2
                    quickTextsService.Config.ModoColar = false;
                    quickTextsService.ForceWtsModeForTest = false;
                    receivedBuffer.Clear();
                    quickTextsService.TogglePopup();
                    Assert.True(quickTextsService.IsPopupOpen);
                    quickTextsService.HandlePopupKey(0x32, ctrl: true, alt: false, shift: false); // Ctrl+2
                    Assert.False(quickTextsService.IsPopupOpen);
                    Assert.Equal("ipconfig /all", receivedBuffer.ToString());

                    // Modo Colar local via Scroll Lock + atalho 1
                    quickTextsService.Config.ModoColar = true;
                    quickTextsService.ForceWtsModeForTest = false;
                    receivedBuffer.Clear();
                    quickTextsService.TogglePopup();
                    quickTextsService.HandlePopupKey(0x31, ctrl: false, alt: false, shift: false); // 1
                    Assert.Equal("Olá, equipe fictícia!", receivedBuffer.ToString());

                    // Modo Colar automático em sessão WTS/RDP (mesmo com ModoColar = false) via Scroll Lock + Shift+1
                    quickTextsService.Config.ModoColar = false;
                    quickTextsService.ForceWtsModeForTest = true;
                    receivedBuffer.Clear();
                    quickTextsService.TogglePopup();
                    quickTextsService.HandlePopupKey(0x31, ctrl: false, alt: false, shift: true); // Shift+1
                    Assert.Equal("SegredoFicticio#2026", receivedBuffer.ToString());

                    targetWin.Close();
                    window.Close();

                    // Ao fechar a janela, o hook deve ser liberado
                    Assert.False(quickTextsService.IsHookActive);
                }, dispatcher);
            }
            catch (Exception ex)
            {
                staException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (staException is not null)
            throw new AggregateException("Falha no teste STA de Textos rápidos integrado.", staException);
    }

    [Fact]
    public async Task AppUpdateService_MigrationFromTwoExecutablesToSingleExecutable_RollbackOnFailure_AndSuccessPreservingUserData()
    {
        var bundledConecta = Path.Combine(AppContext.BaseDirectory, "ConectaTI.exe");
        Assert.True(File.Exists(bundledConecta), $"ConectaTI.exe não encontrado em '{bundledConecta}'.");

        var currentBinVersion = AppUpdateService.GetInstalledVersion();

        // Prepara pasta de instalação simulando a homologação anterior (1.8.0) com DOIS executáveis (ConectaTI.exe + HotKeyText.exe)
        var installDir = Path.Combine(_tempRoot, "install-v180-two-exes");
        Directory.CreateDirectory(installDir);
        File.Copy(bundledConecta, Path.Combine(installDir, "ConectaTI.exe"), overwrite: true);
        File.Copy(bundledConecta, Path.Combine(installDir, "HotKeyText.exe"), overwrite: true);

        // Cria bytes distintos nos binários da instalação 1.8.0 mantendo cabeçalho PE válido
        File.AppendAllBytes(Path.Combine(installDir, "ConectaTI.exe"), Encoding.ASCII.GetBytes("OLD_INSTALL_CONECTA_180"));
        File.AppendAllBytes(Path.Combine(installDir, "HotKeyText.exe"), Encoding.ASCII.GetBytes("OLD_INSTALL_HOTKEY_130"));
        var initialConectaHash = AppUpdateService.ComputeFileSha256(Path.Combine(installDir, "ConectaTI.exe"));
        var initialHotKeyHash = AppUpdateService.ComputeFileSha256(Path.Combine(installDir, "HotKeyText.exe"));

        var initialManifest = new ReleasePackageManifest
        {
            Application = "ConectaTI",
            Version = "1.8.0",
            SuiteVersion = "1.8.0",
            UpdateOrigin = AppUpdateService.OfficialGitHubRepo,
            ExecutableSha256 = initialConectaHash,
            Components = new ReleaseComponentsContainer
            {
                ConectaTi = new ReleaseComponentManifest
                {
                    Name = "ConectaTI",
                    Version = "1.8.0",
                    ExecutableName = "ConectaTI.exe",
                    Sha256 = initialConectaHash
                },
                HotKeyText = new ReleaseComponentManifest
                {
                    Name = "HotKeyText",
                    Version = "1.3.0",
                    ExecutableName = "HotKeyText.exe",
                    Sha256 = initialHotKeyHash,
                    SourceRepository = HotKeyTextService.SourceRepository,
                    SourceCommit = HotKeyTextService.SourceCommit
                }
            }
        };
        File.WriteAllText(Path.Combine(installDir, "release-manifest.json"), JsonSerializer.Serialize(initialManifest));

        // Cria banco SQLite real, credenciais DPAPI, configurações, backups e textos rápidos na instalação 1.8.0
        var installPaths = new AppPaths(installDir);
        var installLogger = new AppLogger(installPaths);
        var installSettings = new SettingsService(installPaths, installLogger);
        installSettings.Load();
        installSettings.Current.Theme = AppThemeMode.Dark;
        installSettings.Save();
        var installRepo = new SqliteRepository(installPaths, installLogger);
        installRepo.InitializeAndMigrate();
        var installVault = new CredentialVaultService(installSettings, installRepo, installLogger, useWindowsVault: false);
        var installVm = new MainViewModel(
            installPaths,
            installLogger,
            installSettings,
            installRepo,
            installVault,
            _clipboard,
            new ConnectionLauncherService(installPaths, installSettings, installRepo, installVault, _clipboard, installLogger),
            new ImportExportService(installRepo, installVault, installLogger),
            new NetworkDiagnosticService());
        await installVm.InitializeAsync(seedDemoIfEmpty: false);
        installVm.NewConnection();
        installVm.EditingEntry.Name = "Servidor Migrado 1.8.0";
        installVm.EditingEntry.Host = "10.90.0.10";
        installVm.InlineUserWithDomain = "operador.teste";
        installVm.UpdatePasswordFromUser("SenhaFicticiaMigracao180!");
        Assert.True(await installVm.SaveConnectionAsync());

        var userDbFile = installPaths.DatabasePath;
        var userSettingsFile = installPaths.SettingsPath;
        var userBackupFile = Path.Combine(installPaths.BackupsDirectory, "backup-ficticio.db");
        var userTextsFile = Path.Combine(installDir, "HotKeyText", "textos.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(userBackupFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(userTextsFile)!);
        SqliteConnection.ClearAllPools();
        File.Copy(userDbFile, userBackupFile, overwrite: true);
        File.WriteAllText(userTextsFile, "<Config><ModoColar>true</ModoColar><Textos><TextoItem><Titulo>Ficticio</Titulo><Atalho>F7</Atalho><Conteudo>Texto migrado 180</Conteudo><OcultarTexto>true</OcultarTexto></TextoItem></Textos></Config>");

        // Prepara pacote da nova entrega (1.8.1) com UM ÚNICO EXECUTÁVEL (ConectaTI.exe)
        var releaseServerDir = Path.Combine(_tempRoot, "release-v181-single-exe");
        Directory.CreateDirectory(releaseServerDir);
        var pkgStagingDir = Path.Combine(releaseServerDir, "pkg");
        Directory.CreateDirectory(pkgStagingDir);
        var pkgConecta = Path.Combine(pkgStagingDir, "ConectaTI.exe");
        File.Copy(bundledConecta, pkgConecta, overwrite: true);

        var zipPath = Path.Combine(releaseServerDir, $"ConectaTI-{currentBinVersion}-win-x64.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(pkgConecta, "ConectaTI.exe");
        }

        var newConectaHash = AppUpdateService.ComputeFileSha256(pkgConecta);
        var zipHash = AppUpdateService.ComputeFileSha256(zipPath);
        var zipLen = new FileInfo(zipPath).Length;

        var newManifest = new ReleasePackageManifest
        {
            Application = "ConectaTI",
            Version = currentBinVersion,
            SuiteVersion = currentBinVersion,
            UpdateOrigin = AppUpdateService.OfficialGitHubRepo,
            RepositoryUrl = AppUpdateService.OfficialRepositoryUrl,
            ExecutableName = "ConectaTI.exe",
            ExecutableLength = new FileInfo(pkgConecta).Length,
            ExecutableSha256 = newConectaHash,
            ZipName = Path.GetFileName(zipPath),
            ZipLength = zipLen,
            ZipSha256 = zipHash,
            Components = new ReleaseComponentsContainer
            {
                ConectaTi = new ReleaseComponentManifest
                {
                    Name = "ConectaTI",
                    Version = currentBinVersion,
                    ExecutableName = "ConectaTI.exe",
                    Length = new FileInfo(pkgConecta).Length,
                    Sha256 = newConectaHash,
                    SourceRepository = AppUpdateService.OfficialGitHubRepo,
                    UpdateOrigin = AppUpdateService.OfficialGitHubRepo
                },
                HotKeyText = new ReleaseComponentManifest
                {
                    Name = "HotKeyText",
                    Version = currentBinVersion,
                    ExecutableName = "ConectaTI.exe",
                    Length = new FileInfo(pkgConecta).Length,
                    Sha256 = newConectaHash,
                    SourceRepository = HotKeyTextService.SourceRepository,
                    SourceCommit = HotKeyTextService.SourceCommit,
                    Author = HotKeyTextService.AuthorCredit,
                    UpdateOrigin = AppUpdateService.OfficialGitHubRepo
                }
            },
            UserDataFilesIncluded = []
        };

        var manifestPath = Path.Combine(releaseServerDir, "release-manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(newManifest));

        var zipUri = new Uri(zipPath).AbsoluteUri;
        var manifestUri = new Uri(manifestPath).AbsoluteUri;

        var releaseApiJson = JsonSerializer.Serialize(new
        {
            tag_name = $"v{currentBinVersion}",
            draft = false,
            prerelease = false,
            html_url = $"https://github.com/MaxBSilva/ConectaTI/releases/tag/v{currentBinVersion}",
            body = "Release de homologação ConectaTI (executável único com HotKeyText integrado)",
            assets = new object[]
            {
                new { name = Path.GetFileName(zipPath), browser_download_url = zipUri },
                new { name = "release-manifest.json", browser_download_url = manifestUri }
            }
        });

        using var httpClient = new HttpClient(new FakeGitHubHttpMessageHandler(releaseApiJson));
        var updateService = new AppUpdateService(httpClient, allowLocalFileUrls: true);

        // 1. Consulta de atualização a partir da instalação 1.8.0 deve detectar a nova versão 1.8.1
        var check = await updateService.CheckForUpdatesAsync(installDir);
        Assert.True(check.Success);
        Assert.True(check.IsUpdateAvailable);
        Assert.Equal("1.8.0", check.InstalledVersion);
        Assert.Equal(currentBinVersion, check.AvailableVersion);
        Assert.Equal(currentBinVersion, check.AvailableHotKeyTextVersion);

        // 2. Simula falha de instalação no meio da substituição e valida reversão automática (restaurando ambos os executáveis da 1.8.0)
        var failedApply = await updateService.DownloadValidateAndApplyAsync(
            check,
            installDir,
            faultInjectionAfterConectaCopy: _ => throw new IOException("Falha simulada de escrita durante atualização"));

        Assert.False(failedApply.Success);
        Assert.True(failedApply.RolledBack);
        Assert.Equal("1.8.0", failedApply.InstalledVersionAfter);
        Assert.Equal(initialConectaHash, AppUpdateService.ComputeFileSha256(Path.Combine(installDir, "ConectaTI.exe")));
        Assert.True(File.Exists(Path.Combine(installDir, "HotKeyText.exe")));
        Assert.Equal(initialHotKeyHash, AppUpdateService.ComputeFileSha256(Path.Combine(installDir, "HotKeyText.exe")));

        // 3. Executa migração completa da entrega de 2 executáveis (1.8.0) para a entrega de 1 executável (1.8.1)
        var successApply = await updateService.DownloadValidateAndApplyAsync(check, installDir);
        Assert.True(successApply.Success, successApply.Message);
        Assert.False(successApply.RolledBack);
        Assert.Equal(currentBinVersion, successApply.InstalledVersionAfter);
        Assert.Equal(newConectaHash, AppUpdateService.ComputeFileSha256(Path.Combine(installDir, "ConectaTI.exe")));
        // Confirma que o HotKeyText.exe antigo foi removido da raiz da instalação, restando apenas ConectaTI.exe
        Assert.False(File.Exists(Path.Combine(installDir, "HotKeyText.exe")));

        // 4. Quando já está atualizado, CheckForUpdatesAsync deve retornar "Você já está na versão mais recente"
        var upToDateCheck = await updateService.CheckForUpdatesAsync(installDir);
        Assert.True(upToDateCheck.Success);
        Assert.False(upToDateCheck.IsUpdateAvailable);
        Assert.Equal(AppUpdateService.UpToDateMessage, upToDateCheck.StatusMessage);

        // 5. Dados de usuário preservados integralmente (conexões, credenciais, configurações, backups, textos e salvamento explícito)
        Assert.True(File.Exists(userDbFile));
        Assert.True(File.Exists(userSettingsFile));
        Assert.True(File.Exists(userBackupFile));
        Assert.Contains("<Titulo>Ficticio</Titulo>", File.ReadAllText(userTextsFile));

        var postMigrationSettings = new SettingsService(installPaths, installLogger);
        postMigrationSettings.Load();
        Assert.Equal(AppThemeMode.Dark, postMigrationSettings.Current.Theme);

        var postMigrationRepo = new SqliteRepository(installPaths, installLogger);
        postMigrationRepo.InitializeAndMigrate();
        var postMigrationVault = new CredentialVaultService(postMigrationSettings, postMigrationRepo, installLogger, useWindowsVault: false);
        var postMigrationVm = new MainViewModel(
            installPaths,
            installLogger,
            postMigrationSettings,
            postMigrationRepo,
            postMigrationVault,
            _clipboard,
            new ConnectionLauncherService(installPaths, postMigrationSettings, postMigrationRepo, postMigrationVault, _clipboard, installLogger),
            new ImportExportService(postMigrationRepo, postMigrationVault, installLogger),
            new NetworkDiagnosticService());
        await postMigrationVm.InitializeAsync(seedDemoIfEmpty: false);

        var loadedAfterMigration = Assert.Single(postMigrationVm.FilteredConnections);
        Assert.Equal("Servidor Migrado 1.8.0", loadedAfterMigration.Name);
        Assert.False(string.IsNullOrWhiteSpace(loadedAfterMigration.CredentialId));
        var migratedCred = await postMigrationRepo.GetCredentialByIdAsync(loadedAfterMigration.CredentialId!);
        Assert.NotNull(migratedCred);
        Assert.Equal("SenhaFicticiaMigracao180!", postMigrationVault.UnprotectSecret(migratedCred!));

        // Valida salvamento explícito (Salvar / Ctrl+S) após a migração
        postMigrationVm.SelectedConnection = loadedAfterMigration;
        postMigrationVm.EditingEntry.Name = "Servidor Migrado 1.8.1 (Salvo)";
        Assert.True(postMigrationVm.CanSave);
        Assert.True(await postMigrationVm.SaveConnectionAsync());
        Assert.False(postMigrationVm.CanSave);
        Assert.Equal("Servidor Migrado 1.8.1 (Salvo)", postMigrationVm.SelectedConnection?.Name);
        var savedCredAfterEdit = await postMigrationRepo.GetCredentialByIdAsync(postMigrationVm.SelectedConnection!.CredentialId!);
        Assert.NotNull(savedCredAfterEdit);
        Assert.Equal("SenhaFicticiaMigracao180!", postMigrationVault.UnprotectSecret(savedCredAfterEdit!));

        // 6. Recuperação manual da versão anterior (RestorePreviousInstallation restaura a 1.8.0 com seus 2 executáveis)
        Assert.True(AppUpdateService.HasRollbackBackup(installDir));
        Assert.True(AppUpdateService.RestorePreviousInstallation(installDir, string.Empty, out var restoreErr), restoreErr);
        Assert.Equal("1.8.0", AppUpdateService.GetInstalledVersion(installDir));
        Assert.Equal(initialConectaHash, AppUpdateService.ComputeFileSha256(Path.Combine(installDir, "ConectaTI.exe")));
        Assert.True(File.Exists(Path.Combine(installDir, "HotKeyText.exe")));
        Assert.Equal(initialHotKeyHash, AppUpdateService.ComputeFileSha256(Path.Combine(installDir, "HotKeyText.exe")));

        // 7. Rejeita origem não autorizada (diferente de MaxBSilva/ConectaTI)
        var untrustedApiJson = JsonSerializer.Serialize(new
        {
            tag_name = "v9.9.9",
            draft = false,
            prerelease = false,
            html_url = "https://github.com/OutroUsuario/ConectaTI/releases/tag/v9.9.9",
            assets = Array.Empty<object>()
        });
        using var untrustedClient = new HttpClient(new FakeGitHubHttpMessageHandler(untrustedApiJson));
        var untrustedService = new AppUpdateService(untrustedClient, allowLocalFileUrls: true);
        var untrustedCheck = await untrustedService.CheckForUpdatesAsync(installDir);
        Assert.False(untrustedCheck.Success);

        var foreignAssetApiJson = JsonSerializer.Serialize(new
        {
            tag_name = $"v{currentBinVersion}",
            html_url = $"https://github.com/MaxBSilva/ConectaTI/releases/tag/v{currentBinVersion}",
            assets = new[] { new { name = "release-manifest.json", browser_download_url = "https://github.com/OutroUsuario/ConectaTI/releases/download/v2.0.0/release-manifest.json" } }
        });
        using var foreignAssetClient = new HttpClient(new FakeGitHubHttpMessageHandler(foreignAssetApiJson));
        var foreignAssetCheck = await new AppUpdateService(foreignAssetClient).CheckForUpdatesAsync(installDir);
        Assert.False(foreignAssetCheck.Success);
        Assert.Contains("URL de anexo fora das Releases", foreignAssetCheck.ErrorMessage);

        // 8. Rejeita pacote contendo arquivo de usuário proibido (sqlite.db)
        SqliteConnection.ClearAllPools();
        var badZipPath = Path.Combine(releaseServerDir, "bad-user-data.zip");
        using (var badZip = ZipFile.Open(badZipPath, ZipArchiveMode.Create))
        {
            badZip.CreateEntryFromFile(pkgConecta, "ConectaTI.exe");
            badZip.CreateEntryFromFile(userBackupFile, "sqlite.db");
        }
        var badManifest = JsonSerializer.Deserialize<ReleasePackageManifest>(JsonSerializer.Serialize(newManifest))!;
        badManifest.ZipSha256 = AppUpdateService.ComputeFileSha256(badZipPath);
        badManifest.ZipLength = new FileInfo(badZipPath).Length;

        Assert.Throws<InvalidDataException>(() =>
            AppUpdateService.ValidatePackageAuthenticityAndIntegrity(
                badManifest,
                badZipPath,
                Path.Combine(_tempRoot, "bad-extract")));
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return;
            Thread.Sleep(50);
        }
    }

    private static void RunAsyncOnSta(Func<Task> action, Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        Exception? failure = null;
        _ = InvokeAsync();
        Dispatcher.PushFrame(frame);
        if (failure is not null)
            throw failure;

        async Task InvokeAsync()
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                frame.Continue = false;
            }
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    private sealed class FakeGitHubHttpMessageHandler(string releaseJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
