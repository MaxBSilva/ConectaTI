using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConectaTI.App.Data;
using ConectaTI.App.Helpers;
using ConectaTI.App.Models;
using ConectaTI.App.Security;
using ConectaTI.App.Services;
using ConectaTI.App.Services.Diagnostics;
using ConectaTI.App.Services.ImportExport;
using ConectaTI.App.Services.Launchers;

namespace ConectaTI.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public const string AllClientsFilterLabel = "Todos os Clientes";
    public const string AllEnvironmentsFilterLabel = "Todos os Ambientes";
    public const string AllProtocolsFilterLabel = "Todos os Tipos";

    private readonly AppPaths _paths;
    private readonly AppLogger _logger;
    private readonly SettingsService _settingsService;
    private readonly SqliteRepository _repository;
    private readonly CredentialVaultService _vaultService;
    private readonly ClipboardService _clipboardService;
    private readonly ConnectionLauncherService _launcherService;
    private readonly ImportExportService _importExportService;
    private readonly NetworkDiagnosticService _diagnosticService;
    private readonly AppDiagnosticService _appDiagnosticService;

    private List<ConnectionEntry> _allConnections = new();
    private List<ConnectionEntry> _trashConnections = new();
    private ConnectionEntry _originalSnapshot = new();
    private CancellationTokenSource? _passwordRevealCts;
    private CancellationTokenSource? _diagnosticCts;
    private bool _suppressSelectionGuard;
    private bool _updatingInlineFields;
    private readonly Dictionary<string, ConnectionEditSession> _editSessions = new();
    private ConnectionEditSession? _activeSession;
    private long _editorGeneration;
    private long _reloadGeneration;
    private long _dataRevision;
    private readonly ConnectionPersistenceService _persistence;
    private readonly System.Windows.Threading.Dispatcher? _uiDispatcher =
        SynchronizationContext.Current is System.Windows.Threading.DispatcherSynchronizationContext
            ? System.Windows.Threading.Dispatcher.CurrentDispatcher : null;
    public ObservableCollection<ConnectionEntry> NewDrafts { get; } = new();
    [ObservableProperty] private ConnectionEntry? _selectedNewDraft;
    partial void OnSelectedNewDraftChanged(ConnectionEntry? value)
    {
        if (!_suppressSelectionGuard && value != null && _editSessions.TryGetValue(value.Id, out var session))
        {
            CaptureSession();
            var previous = _suppressSelectionGuard;
            _suppressSelectionGuard = true;
            try { SelectedConnection = null; LoadSession(session); }
            finally { _suppressSelectionGuard = previous; }
        }
    }
    public bool HasPendingChanges => HasUnsavedChanges();
    public bool CanSave => !IsSaving && HasPendingChanges && !IsTrashMode;
    public bool CanDiscardChanges => !IsSaving && (HasPendingChanges || IsNewRecord);
    public string SaveButtonText => IsSaving ? L.Text("Salvando...") : HasPendingChanges ? L.Text("Salvar alterações") : L.Text("Salvar");
    public string PendingChangesText => HasPendingChanges
        ? L.Text("Alterações não salvas. Salve antes de fechar.")
        : _editSessions.Values.Any(d => d.IsDirty) ? L.Text("Há rascunhos não salvos. Salve antes de fechar.") : string.Empty;

    private void NotifyEditState()
    {
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanDiscardChanges));
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(PendingChangesText));
    }
    partial void OnIsSavingChanged(bool value) { NotifyEditState(); OnPropertyChanged(nameof(CanBatchEdit)); }
    private void EditingEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSelectionGuard) return;
        if (_activeSession != null) _activeSession.Revision++;
        if (!_updatingInlineFields)
        {
            var previous = _suppressSelectionGuard;
            _suppressSelectionGuard = true;
            try
            {
                if (e.PropertyName is nameof(ConnectionEntry.Host) or nameof(ConnectionEntry.Port))
                    InlineHostAndPort = HostPortParser.FormatDisplay(EditingEntry.Host, EditingEntry.Port, EditingEntry.Protocol);
                if (e.PropertyName is nameof(ConnectionEntry.Username) or nameof(ConnectionEntry.Domain))
                    InlineUserWithDomain = UserIdentityFormatter.FormatDisplay(EditingEntry.Username, EditingEntry.Domain);
            }
            finally { _suppressSelectionGuard = previous; }
        }
        CaptureSession();
        NotifyEditState();
        if (e.PropertyName == nameof(ConnectionEntry.CredentialId)) NotifyPasswordStatusProperties();
    }
    private void CaptureSession()
    {
        if (_activeSession == null) return;
        _activeSession.HostText = InlineHostAndPort;
        _activeSession.UserText = InlineUserWithDomain;
        _activeSession.Password = InlinePasswordInput;
        _activeSession.CredentialName = InlineCredentialName;
    }
    partial void OnEditingEntryChanged(ConnectionEntry? oldValue, ConnectionEntry newValue)
    {
        if (_suppressSelectionGuard || _activeSession == null) return;
        if (newValue.Id != _activeSession.Entry.Id)
            throw new InvalidOperationException(L.Text("Troque de acesso pela seleção, usando seu ID."));
        if (oldValue != null) oldValue.PropertyChanged -= EditingEntryChanged;
        _activeSession.Entry = newValue;
        _activeSession.Revision++;
        newValue.PropertyChanged += EditingEntryChanged;
        NotifyEditState();
    }
    partial void OnInlinePasswordInputChanged(string value)
    {
        if (!_suppressSelectionGuard && _activeSession != null)
        {
            _activeSession.Password = value;
            _activeSession.PasswordRevision++;
            _activeSession.Revision++;
            _activeSession.PasswordIntent = value.Length > 0
                ? value == _activeSession.LoadedPassword ? PasswordEditIntent.Keep : PasswordEditIntent.Replace
                : _activeSession.LoadedPassword != null && !string.IsNullOrEmpty(EditingEntry.CredentialId)
                    ? PasswordEditIntent.Remove : PasswordEditIntent.Keep;
            var previous = _suppressSelectionGuard;
            _suppressSelectionGuard = true;
            try { IsPasswordMarkedForRemoval = _activeSession.PasswordIntent == PasswordEditIntent.Remove; }
            finally { _suppressSelectionGuard = previous; }
        }
        NotifyPasswordStatusProperties();
    }
    // Called only for intentional edits, including erasing an actually revealed password.
    public void UpdatePasswordFromUser(string value)
    {
        if (value == InlinePasswordInput && value.Length == 0 && _activeSession?.LoadedPassword != null)
            MarkPasswordForRemoval();
        else InlinePasswordInput = value;
    }
    public void ClearSessionDrafts()
    {
        HideRevealedPassword();
        if (_activeSession != null) EditingEntry.PropertyChanged -= EditingEntryChanged;
        _activeSession = null;
        _editSessions.Clear();
        NewDrafts.Clear();
        InlinePasswordInput = string.Empty;
    }

    public Func<string, UnsavedChangesChoice>? ConfirmUnsavedChangesCallback { get; set; }
    public Func<string, bool>? ConfirmDiscardUnsavedChangesCallback { get; set; }
    public Func<string, string, bool>? ConfirmDeleteCallback { get; set; }
    public Action<string, string>? ShowErrorDialogCallback { get; set; }
    public Action? OpenExecutableSettingsCallback { get; set; }

    public ObservableCollection<NavigationNodeViewModel> NavigationNodes { get; } = new();
    [ObservableProperty]
    private ObservableCollection<ConnectionEntry> _filteredConnections = new();
    public ObservableCollection<SavedCredential> AvailableCredentials { get; } = new();
    public ObservableCollection<string> ClientFilterOptions { get; } = new() { AllClientsFilterLabel };

    public IReadOnlyList<SelectableOption<ConnectionTypeOption>> ConnectionTypeOptions { get; } =
    [
        new(ConnectionTypeOption.Mstsc, "Área de Trabalho Remota (MSTSC)"),
        new(ConnectionTypeOption.SshPutty, "SSH — PuTTY"),
        new(ConnectionTypeOption.SshOpenSsh, "SSH — OpenSSH"),
        new(ConnectionTypeOption.Vnc, "VNC"),
        new(ConnectionTypeOption.WinScpSftp, "WinSCP — SFTP"),
        new(ConnectionTypeOption.WinScpScp, "WinSCP — SCP")
    ];

    [ObservableProperty]
    private ConnectionTypeOption _selectedConnectionType = ConnectionTypeOption.Mstsc;

    public IReadOnlyList<string> EnvironmentFilterOptions { get; } =
    [
        AllEnvironmentsFilterLabel,
        "Produção",
        "Homologação",
        "Teste",
        "Não informado"
    ];

    public IReadOnlyList<string> ProtocolFilterOptions { get; } =
    [
        AllProtocolsFilterLabel,
        "Área de Trabalho Remota (MSTSC)",
        "SSH",
        "SFTP (WinSCP)",
        "SCP (WinSCP)",
        "VNC"
    ];

    public IReadOnlyList<SelectableOption<EnvironmentType>> EnvironmentSelectorOptions { get; } =
    [
        new(EnvironmentType.Production, "Produção"),
        new(EnvironmentType.Staging, "Homologação"),
        new(EnvironmentType.Testing, "Teste"),
        new(EnvironmentType.Unspecified, "Não informado")
    ];

    public IReadOnlyList<SelectableOption<ConnectionProtocol>> ProtocolSelectorOptions { get; } =
    [
        new(ConnectionProtocol.Rdp, "Área de Trabalho Remota (MSTSC)"),
        new(ConnectionProtocol.Ssh, "SSH"),
        new(ConnectionProtocol.Sftp, "SFTP (WinSCP)"),
        new(ConnectionProtocol.Scp, "SCP (WinSCP)"),
        new(ConnectionProtocol.Vnc, "VNC")
    ];

    public IReadOnlyList<SelectableOption<SshClientPreference>> SshClientSelectorOptions { get; } =
    [
        new(SshClientPreference.Default, "Padrão configurado"),
        new(SshClientPreference.Putty, "PuTTY"),
        new(SshClientPreference.OpenSsh, "OpenSSH do Windows")
    ];

    // Mantidos para compatibilidade de testes
    public IReadOnlyList<EnvironmentType> AllEnvironmentTypes { get; } = Enum.GetValues<EnvironmentType>();
    public IReadOnlyList<ConnectionProtocol> AllProtocolTypes { get; } = Enum.GetValues<ConnectionProtocol>();
    public IReadOnlyList<SshClientPreference> AllSshClientPreferences { get; } = Enum.GetValues<SshClientPreference>();

    [ObservableProperty]
    private NavigationNodeViewModel? _selectedNavigationNode;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedClientFilter = AllClientsFilterLabel;

    [ObservableProperty]
    private string _selectedEnvironmentFilter = AllEnvironmentsFilterLabel;

    [ObservableProperty]
    private string _selectedProtocolFilter = AllProtocolsFilterLabel;

    [ObservableProperty]
    private ConnectionSortOption _selectedSortOption = ConnectionSortOption.NameAsc;

    [ObservableProperty]
    private ConnectionEntry? _selectedConnection;

    [ObservableProperty]
    private ConnectionEntry _editingEntry = new();

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isNewRecord;

    [ObservableProperty]
    private string _inlinePasswordInput = string.Empty;

    [ObservableProperty]
    private string _inlineCredentialName = string.Empty;

    [ObservableProperty]
    private bool _isPasswordMarkedForRemoval;

    [ObservableProperty]
    private bool _isPasswordRevealed;

    [ObservableProperty]
    private string _revealedPasswordText = "••••••••••••";

    [ObservableProperty]
    private string _inlineHostAndPort = string.Empty;

    [ObservableProperty]
    private string _inlineUserWithDomain = string.Empty;

    public bool IsLocked => false;


    [ObservableProperty]
    private string _statusMessage = L.Text("Pronto.");

    [ObservableProperty]
    private string _securityBannerMessage = string.Empty;

    [ObservableProperty]
    private bool _isRunningDiagnostic;

    [ObservableProperty]
    private string _diagnosticResultTitle = string.Empty;

    [ObservableProperty]
    private string _diagnosticResultDetails = string.Empty;

    [ObservableProperty]
    private int _totalConnectionsCount;

    [ObservableProperty]
    private int _filteredConnectionsCount;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private ConnectionViewMode _currentViewMode = ConnectionViewMode.All;

    [ObservableProperty]
    private int _trashCount;

    [ObservableProperty]
    private int _favoritesCount;

    [ObservableProperty]
    private string _incrementalSearchStatus = string.Empty;

    public bool IsTrashMode => CurrentViewMode == ConnectionViewMode.Trash;
    public bool IsNotTrashMode => CurrentViewMode != ConnectionViewMode.Trash;

    public string FavoritesButtonLabel => L.Format("★ Favoritos ({0})", FavoritesCount);
    public string TrashButtonLabel => L.Format("🗑️ Lixeira ({0})", TrashCount);

    public AppPaths Paths => _paths;
    public SettingsService SettingsService => _settingsService;
    public SqliteRepository Repository => _repository;
    public CredentialVaultService VaultService => _vaultService;
    public ImportExportService ImportExportService => _importExportService;
    public AppDiagnosticService AppDiagnosticService => _appDiagnosticService;

    public bool HasSelectedOrNew => SelectedConnection != null || IsEditing;
    public bool IsViewMode => !IsEditing && SelectedConnection != null;
    public bool IsRdpSelected => EditingEntry.Protocol == ConnectionProtocol.Rdp;
    public bool IsSshSelected => EditingEntry.Protocol == ConnectionProtocol.Ssh;
    public bool IsWinScpSelected => EditingEntry.Protocol is ConnectionProtocol.Sftp or ConnectionProtocol.Scp;
    public bool IsVncSelected => EditingEntry.Protocol == ConnectionProtocol.Vnc;
    public bool SupportsPrivateKey => EditingEntry.Protocol is ConnectionProtocol.Ssh or ConnectionProtocol.Sftp or ConnectionProtocol.Scp;
    public bool IsDomainApplicable => EditingEntry.Protocol == ConnectionProtocol.Rdp || !string.IsNullOrWhiteSpace(EditingEntry.Domain);

    public bool HasSavedPassword => !string.IsNullOrWhiteSpace(EditingEntry.CredentialId) && !IsPasswordMarkedForRemoval;
    public bool CanRemoveSavedPassword => HasSavedPassword;
    public bool HasSavedPasswordOrRemoval => HasSavedPassword || IsPasswordMarkedForRemoval;

    private string? PasswordAvailabilityMessage => HasSavedPassword ? AvailableCredentials.FirstOrDefault(c=>c.Id==EditingEntry.CredentialId) is {} c
        ? _vaultService.AvailabilityMessage(c) : null : null;
    public bool PasswordRecoveryFailed => _activeSession?.CredentialRecoveryFailed == true || PasswordAvailabilityMessage != null;
    public bool HasPendingPasswordChange => IsPasswordMarkedForRemoval || _activeSession?.PasswordIntent == PasswordEditIntent.Replace;
    public System.Windows.Media.Brush PasswordStateBrush => HasPendingPasswordChange ? System.Windows.Media.Brushes.DarkGoldenrod
        : HasSavedPassword && !PasswordRecoveryFailed ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.IndianRed;
    public string SavedPasswordBadgeText => IsPasswordMarkedForRemoval ? L.Text("Remoção pendente — salvar")
        : _activeSession?.PasswordIntent == PasswordEditIntent.Replace ? L.Text("Senha alterada — salvar")
        : PasswordRecoveryFailed ? L.Refresh(_activeSession?.CredentialRecoveryMessage ?? PasswordAvailabilityMessage ?? L.Text("Falha ao recuperar credencial"))
        : HasSavedPassword ? L.Text("Senha salva") : L.Text("Sem senha salva");

    public string ThemeToggleIcon => ThemeService.ResolveIsDark(_settingsService.Current.Theme) ? "☀️" : "🌙";
    public string ThemeToggleToolTip => ThemeService.ResolveIsDark(_settingsService.Current.Theme) ? L.Text("Mudar para tema claro") : L.Text("Mudar para tema escuro");

    [RelayCommand]
    public void ToggleTheme()
    {
        var currentDark = ThemeService.ResolveIsDark(_settingsService.Current.Theme);
        var newMode = currentDark ? AppThemeMode.Light : AppThemeMode.Dark;
        _settingsService.Current.Theme = newMode;
        _settingsService.Save();
        ThemeService.ApplyTheme(newMode);
        OnPropertyChanged(nameof(ThemeToggleIcon));
        OnPropertyChanged(nameof(ThemeToggleToolTip));
    }

    // Propriedades diretas de leitura para o painel direito de resumo da conexão selecionada
    public bool HasSelectedConnection => SelectedConnection != null;
    public string SelectedName => SelectedConnection?.Name ?? string.Empty;
    public string SelectedClientName => SelectedConnection?.ClientName ?? string.Empty;
    public string SelectedProtocolDisplay => SelectedConnection?.ProtocolDisplay ?? string.Empty;
    public string SelectedEndpointDisplay => SelectedConnection?.EndpointDisplay ?? string.Empty;
    public string SelectedHost => SelectedConnection?.Host ?? string.Empty;
    public string SelectedUserDisplay => SelectedConnection?.EffectiveUserDisplay ?? L.Text("Não informado");
    public bool SelectedHasUser => SelectedConnection?.HasEffectiveUser == true;
    public bool SelectedHasSavedPassword => (SelectedConnection != null && !string.IsNullOrWhiteSpace(SelectedConnection.CredentialId)) || HasSavedPassword;
    public string SelectedPasswordStateText => SelectedHasSavedPassword ? L.Text("Senha salva") : L.Text("Sem senha salva");
    public bool SelectedShowNoPasswordNote => SelectedConnection != null && !SelectedHasSavedPassword;
    public string SelectedNotes => SelectedConnection?.Notes ?? string.Empty;
    public bool SelectedHasNotes => SelectedConnection != null && !string.IsNullOrWhiteSpace(SelectedConnection.Notes);

    public bool CanCopySelectedName => !string.IsNullOrWhiteSpace(EditingEntry?.Name) || (SelectedConnection != null && !string.IsNullOrWhiteSpace(SelectedConnection.Name));
    public bool CanCopySelectedHost => !string.IsNullOrWhiteSpace(InlineHostAndPort) || !string.IsNullOrWhiteSpace(EditingEntry?.Host) || (SelectedConnection != null && !string.IsNullOrWhiteSpace(SelectedConnection.Host));
    public bool CanCopySelectedUser => !string.IsNullOrWhiteSpace(InlineUserWithDomain) || !string.IsNullOrWhiteSpace(EditingEntry?.Username) || (SelectedConnection != null && SelectedConnection.HasEffectiveUser);
    public bool CanCopySelectedPassword => HasSavedPassword || !string.IsNullOrEmpty(InlinePasswordInput);
    public bool ShowCopyPasswordOption => SelectedHasSavedPassword;
    public bool CanCopyVpnName => !string.IsNullOrWhiteSpace(EditingEntry?.VpnName) || (SelectedConnection != null && !string.IsNullOrWhiteSpace(SelectedConnection.VpnName));
    public bool CanCopyVpnServer => !string.IsNullOrWhiteSpace(EditingEntry?.VpnServer) || (SelectedConnection != null && !string.IsNullOrWhiteSpace(SelectedConnection.VpnServer));

    public string PasswordStatusText => SavedPasswordBadgeText;

    public string PasswordFieldLabel => HasSavedPassword
        ? L.Text("Alterar senha")
        : L.Text("Senha");

    public MainViewModel(
        AppPaths paths,
        AppLogger logger,
        SettingsService settingsService,
        SqliteRepository repository,
        CredentialVaultService vaultService,
        ClipboardService clipboardService,
        ConnectionLauncherService launcherService,
        ImportExportService importExportService,
        NetworkDiagnosticService diagnosticService)
    {
        LocalizationService.Current.Observe(this);
        _statusTranslation = L.Capture(StatusMessage);
        _paths = paths;
        _logger = logger;
        _settingsService = settingsService;
        _repository = repository;
        _vaultService = vaultService;
        _vaultService.LockStateChanged += (_, _) =>
        {
            void RefreshVaultState()
            {
                if (IsLocked)
                {
                    foreach (var editSession in _editSessions.Values) editSession.LoadedPassword=null;
                    HideRevealedPassword();
                }
                OnPropertyChanged(nameof(IsLocked));
                NotifyPasswordStatusProperties();
            }
            if (_uiDispatcher != null && !_uiDispatcher.CheckAccess()) _uiDispatcher.InvokeAsync(RefreshVaultState);
            else RefreshVaultState();
        };
        _clipboardService = clipboardService;
        _launcherService = launcherService;
        _importExportService = importExportService;
        _diagnosticService = diagnosticService;
        _persistence = new ConnectionPersistenceService(repository, vaultService, logger);
        _appDiagnosticService = new AppDiagnosticService(_paths, _repository, _logger);
    }

    /// <summary>
    /// Inicializa o banco de dados e carrega os cadastros existentes.
    /// Por padrão (seedDemoIfEmpty = false), preserva a base existente do usuário sem recarregar exemplos fictícios.
    /// </summary>
    public async Task InitializeAsync(bool seedDemoIfEmpty = false)
    {
        await Task.Run(_repository.InitializeAndMigrate);
        await _persistence.RecoverVaultCleanupAsync();
        var existing = await _repository.GetAllConnectionsAsync();
        if (existing.Count == 0 && seedDemoIfEmpty)
        {
            var demo = SqliteRepository.CreateFictionalDemoConnections();
            await _repository.SaveConnectionsBatchAsync(demo);
            existing = await _repository.GetAllConnectionsAsync();
            StatusMessage = L.Text("Dados fictícios carregados para teste.");
        }

        await ReloadAllDataAsync(selectConnectionId: existing.FirstOrDefault()?.Id);
    }

    public async Task ReloadAllDataAsync(string? selectConnectionId = null)
    {
        var dispatcher = _uiDispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(() => ReloadAllDataAsync(selectConnectionId)).Task.Unwrap();
            return;
        }
        var generation = ++_reloadGeneration;
        var dataRevision = _dataRevision;
        var editorGeneration = _editorGeneration;
        try
        {
            // Do not publish any partial result when one of the reads fails.
            var all = await _repository.GetAllConnectionsAsync(includeDeleted: true);
            var credentials = await _repository.GetAllCredentialsAsync();
            if (generation != _reloadGeneration || dataRevision != _dataRevision) return;
            _allConnections = all.Where(c => !c.IsDeleted).ToList();
            _trashConnections = all.Where(c => c.IsDeleted).ToList();
            var previous = _suppressSelectionGuard;
            _suppressSelectionGuard = true;
            try
            {
                AvailableCredentials.Clear();
                AvailableCredentials.Add(new SavedCredential { Id = string.Empty, Name = L.Text("(Nenhuma senha salva)") });
                foreach (var credential in credentials) AvailableCredentials.Add(credential);
                RefreshCountsAndNavigation();
            }
            finally { _suppressSelectionGuard = previous; }
            ApplyFilterAndSort(editorGeneration == _editorGeneration ? selectConnectionId : null);
        }
        catch (Exception ex)
        {
            _logger.Warn("Reload", $"Falha de leitura: {ex.GetType().Name}");
            StatusMessage = L.Text("Não foi possível atualizar a lista. Os dados já carregados foram mantidos.");
        }
    }

    private void RefreshCountsAndNavigation()
    {
        TotalConnectionsCount = _allConnections.Count;
        TrashCount = _trashConnections.Count;
        FavoritesCount = _allConnections.Count(c => c.IsFavorite);
        OnPropertyChanged(nameof(FavoritesButtonLabel));
        OnPropertyChanged(nameof(TrashButtonLabel));
        RebuildNavigationAndClientFilters();
    }

    public bool HasUnsavedChanges() => _activeSession?.IsDirty == true;

    // Compatibility entry points: navigation only retains the in-memory draft.
    public Task<bool> ConfirmCanLeaveCurrentEditAsync(string actionDescription = "trocar de registro")
        => Task.FromResult(ConfirmCanLeaveCurrentEdit(actionDescription));
    public bool ConfirmCanLeaveCurrentEdit(string actionDescription = "trocar de registro")
    {
        CaptureSession();
        return true;
    }

    public Task<bool> RequestNavigateToConnectionAsync(ConnectionEntry? target)
    {
        if (target?.Id == _activeSession?.Entry.Id) return Task.FromResult(true);
        CaptureSession();
        var previous = _suppressSelectionGuard;
        _suppressSelectionGuard = true;
        try { SelectedConnection = target; LoadSelectedIntoViewer(target); }
        finally { _suppressSelectionGuard = previous; }
        return Task.FromResult(true);
    }

    [RelayCommand]
    public async Task SetViewModeAsync(ConnectionViewMode mode)
    {
        if (CurrentViewMode == mode)
            return;

        if (!await ConfirmCanLeaveCurrentEditAsync(L.Text("trocar o modo de visualização")))
            return;

        var previous = _suppressSelectionGuard;
        _suppressSelectionGuard = true;
        try { SelectedNavigationNode = NavigationNodes.FirstOrDefault(n => n.Kind == NavigationNodeKind.All); }
        finally { _suppressSelectionGuard = previous; }
        CurrentViewMode = mode;
        OnPropertyChanged(nameof(IsTrashMode));
        OnPropertyChanged(nameof(IsNotTrashMode));
        ApplyFilterAndSort();
    }

    partial void OnSelectedConnectionTypeChanged(ConnectionTypeOption oldValue, ConnectionTypeOption newValue)
    {
        if (_suppressSelectionGuard)
            return;

        var oldPort = oldValue.GetDefaultPort();
        var newPort = newValue.GetDefaultPort();

        EditingEntry.Protocol = newValue.ToProtocol();
        EditingEntry.SshClient = newValue.ToSshClientPreference();

        if (EditingEntry.Port == oldPort || EditingEntry.Port <= 0)
        {
            EditingEntry.Port = newPort;
        }

        InlineHostAndPort = HostPortParser.FormatDisplay(EditingEntry.Host, EditingEntry.Port, EditingEntry.Protocol);

        OnPropertyChanged(nameof(EditingEntry));
        NotifyProtocolDependentProperties();
    }

    partial void OnInlineHostAndPortChanged(string value)
    {
        if (_suppressSelectionGuard)
            return;

        _updatingInlineFields = true;
        try
        {
        var parsed = HostPortParser.Parse(value, EditingEntry.Protocol);
        if (parsed.IsValid)
        {
            EditingEntry.Host = parsed.CleanHost;
            EditingEntry.Port = parsed.Port;
        }
        else
        {
            EditingEntry.Host = value;
        }
        }
        finally { _updatingInlineFields = false; }
        OnPropertyChanged(nameof(EditingEntry));
        CaptureSession();
        NotifySelectedSummaryProperties();
    }

    partial void OnInlineUserWithDomainChanged(string value)
    {
        if (_suppressSelectionGuard)
            return;

        _updatingInlineFields = true;
        try
        {
            var norm = UserIdentityFormatter.Normalize(value, null);
            EditingEntry.Username = norm.IsUpn ? norm.RawUsername : norm.CleanUsername;
            EditingEntry.Domain = norm.IsUpn ? string.Empty : norm.Domain;
        }
        finally { _updatingInlineFields = false; }
        OnPropertyChanged(nameof(EditingEntry));
        CaptureSession();
        NotifySelectedSummaryProperties();
    }

    partial void OnSearchTextChanged(string value) { if (!_suppressSelectionGuard) ApplyFilterAndSort(); }
    partial void OnSelectedClientFilterChanged(string value) { if (!_suppressSelectionGuard) ApplyFilterAndSort(); }
    partial void OnSelectedEnvironmentFilterChanged(string value) { if (!_suppressSelectionGuard) ApplyFilterAndSort(); }
    partial void OnSelectedProtocolFilterChanged(string value) { if (!_suppressSelectionGuard) ApplyFilterAndSort(); }
    partial void OnSelectedSortOptionChanged(ConnectionSortOption value) { if (!_suppressSelectionGuard) ApplyFilterAndSort(); }

    partial void OnIsPasswordMarkedForRemovalChanged(bool value)
    {
        if (!_suppressSelectionGuard && _activeSession != null)
        {
            _activeSession.PasswordIntent = value ? PasswordEditIntent.Remove
                : InlinePasswordInput.Length > 0 ? PasswordEditIntent.Replace : PasswordEditIntent.Keep;
            _activeSession.Revision++;
            _activeSession.PasswordRevision++;
        }
        NotifyPasswordStatusProperties();
    }
    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasSelectedOrNew));
        OnPropertyChanged(nameof(IsViewMode));
        NotifyPasswordStatusProperties();
    }

    partial void OnSelectedNavigationNodeChanged(NavigationNodeViewModel? oldValue, NavigationNodeViewModel? newValue)
    {
        if (_suppressSelectionGuard || newValue == null) return;
        CaptureSession();
        CurrentViewMode = newValue.Kind switch
        {
            NavigationNodeKind.Favorites => ConnectionViewMode.Favorites,
            NavigationNodeKind.Recent => ConnectionViewMode.Recent,
            _ => ConnectionViewMode.All
        };
        OnPropertyChanged(nameof(IsTrashMode));
        OnPropertyChanged(nameof(IsNotTrashMode));
        ApplyFilterAndSort();
    }

    partial void OnSelectedConnectionChanged(ConnectionEntry? oldValue, ConnectionEntry? newValue)
    {
        if (_suppressSelectionGuard) return;
        if (newValue?.Id == _activeSession?.Entry.Id) { NotifySelectedSummaryProperties(); return; }
        CaptureSession();
        LoadSelectedIntoViewer(newValue);
    }

    private void LoadSelectedIntoViewer(ConnectionEntry? entry)
    {
        if (entry == null) { LoadSession(null); return; }
        if (!_editSessions.TryGetValue(entry.Id, out var session))
            _editSessions[entry.Id] = session = new ConnectionEditSession(entry);
        else if (!session.IsDirty && !IsSaving)
        {
            session.Entry = entry.Clone();
            session.Baseline = entry.Clone();
            session.HostText = HostPortParser.FormatDisplay(entry.Host, entry.Port, entry.Protocol);
            session.UserText = UserIdentityFormatter.FormatDisplay(entry.Username, entry.Domain);
        }
        LoadSession(session);
    }

    private void LoadSession(ConnectionEditSession? session)
    {
        var previous = _suppressSelectionGuard;
        _suppressSelectionGuard = true;
        try
        {
            _editorGeneration++;
            EditingEntry.PropertyChanged -= EditingEntryChanged;
            HideRevealedPassword();
            _activeSession = session;
            DiagnosticResultTitle = string.Empty;
            DiagnosticResultDetails = string.Empty;
            EditingEntry = session?.Entry ?? new ConnectionEntry();
            _originalSnapshot = session?.Baseline ?? EditingEntry.Clone();
            IsNewRecord = session?.IsNew == true;
            IsEditing = IsNewRecord;
            InlinePasswordInput = session?.Password ?? string.Empty;
            InlineCredentialName = session?.CredentialName ?? string.Empty;
            IsPasswordMarkedForRemoval = session?.PasswordIntent == PasswordEditIntent.Remove;
            SelectedNewDraft = IsNewRecord ? EditingEntry : null;
            SelectedConnectionType = EnumDisplayExtensions.FromProtocolAndSsh(EditingEntry.Protocol, EditingEntry.SshClient);
            InlineHostAndPort = session?.HostText ?? string.Empty;
            InlineUserWithDomain = session?.UserText ?? string.Empty;
            EditingEntry.PropertyChanged += EditingEntryChanged;
        }
        finally { _suppressSelectionGuard = previous; }
        NotifyProtocolDependentProperties();
        NotifyPasswordStatusProperties();
        NotifySelectedSummaryProperties();
        NotifyEditState();
    }

    public void NotifySelectedSummaryProperties()
    {
        CopyCompleteAccessCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCopySelectedAddress));
        OnPropertyChanged(nameof(CanCopyPersistedUser));
        OnPropertyChanged(nameof(HasSelectedConnection));
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedClientName));
        OnPropertyChanged(nameof(SelectedProtocolDisplay));
        OnPropertyChanged(nameof(SelectedEndpointDisplay));
        OnPropertyChanged(nameof(SelectedHost));
        OnPropertyChanged(nameof(SelectedUserDisplay));
        OnPropertyChanged(nameof(SelectedHasUser));
        OnPropertyChanged(nameof(SelectedHasSavedPassword));
        OnPropertyChanged(nameof(SelectedPasswordStateText));
        OnPropertyChanged(nameof(SelectedShowNoPasswordNote));
        OnPropertyChanged(nameof(SelectedNotes));
        OnPropertyChanged(nameof(SelectedHasNotes));
        OnPropertyChanged(nameof(CanCopySelectedName));
        OnPropertyChanged(nameof(CanCopySelectedHost));
        OnPropertyChanged(nameof(CanCopySelectedUser));
        OnPropertyChanged(nameof(CanCopySelectedPassword));
        OnPropertyChanged(nameof(ShowCopyPasswordOption));
        OnPropertyChanged(nameof(CanCopyVpnName));
        OnPropertyChanged(nameof(CanCopyVpnServer));
    }

    public void NotifyProtocolChanged(ConnectionProtocol oldProtocol, ConnectionProtocol newProtocol)
    {
        if (EditingEntry.Port == oldProtocol.GetDefaultPort() || EditingEntry.Port <= 0)
        {
            EditingEntry.Port = newProtocol.GetDefaultPort();
        }
        OnPropertyChanged(nameof(EditingEntry));
        NotifyProtocolDependentProperties();
    }

    private void NotifyProtocolDependentProperties()
    {
        OnPropertyChanged(nameof(HasSelectedOrNew));
        OnPropertyChanged(nameof(IsViewMode));
        OnPropertyChanged(nameof(IsRdpSelected));
        OnPropertyChanged(nameof(IsSshSelected));
        OnPropertyChanged(nameof(IsWinScpSelected));
        OnPropertyChanged(nameof(IsVncSelected));
        OnPropertyChanged(nameof(SupportsPrivateKey));
        OnPropertyChanged(nameof(IsDomainApplicable));
    }

    public void NotifyPasswordStatusProperties()
    {
        OnPropertyChanged(nameof(HasSavedPassword));
        OnPropertyChanged(nameof(CanRemoveSavedPassword));
        OnPropertyChanged(nameof(PasswordStatusText));
        OnPropertyChanged(nameof(PasswordFieldLabel));
        OnPropertyChanged(nameof(SavedPasswordBadgeText));
        OnPropertyChanged(nameof(PasswordStateBrush));
        OnPropertyChanged(nameof(PasswordRecoveryFailed));
        OnPropertyChanged(nameof(HasPendingPasswordChange));
        OnPropertyChanged(nameof(HasSavedPasswordOrRemoval));
        OnPropertyChanged(nameof(CanCopySelectedPassword));
        NotifyEditState();
    }

    // =========================================================================
    // Pesquisa, Filtros e Ordenação
    // =========================================================================

    public void ApplyFilterAndSort(string? targetSelectId = null)
    {
        IEnumerable<ConnectionEntry> query = CurrentViewMode == ConnectionViewMode.Trash
            ? _trashConnections
            : _allConnections;

        if (CurrentViewMode == ConnectionViewMode.Favorites)
        {
            query = query.Where(c => c.IsFavorite);
        }
        else if (CurrentViewMode == ConnectionViewMode.Recent)
        {
            query = query.Where(c => c.LastOpenedAtUtc.HasValue);
        }

        if (SelectedNavigationNode is { Kind: NavigationNodeKind.Client or NavigationNodeKind.Group } node)
        {
            query = query.Where(c => string.Equals(c.ClientName.Trim(), node.ClientName, StringComparison.OrdinalIgnoreCase));
            if (node.Kind == NavigationNodeKind.Group)
                query = query.Where(c => string.Equals(string.IsNullOrWhiteSpace(c.GroupName) ? "Geral" : c.GroupName.Trim(), node.GroupName, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrEmpty(SelectedClientFilter) && SelectedClientFilter != AllClientsFilterLabel)
            query = query.Where(c => string.Equals(c.ClientName.Trim(), SelectedClientFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(SelectedEnvironmentFilter) && SelectedEnvironmentFilter != AllEnvironmentsFilterLabel)
            query = query.Where(c => c.Environment.ToInvariantDisplayName() == SelectedEnvironmentFilter);
        if (!string.IsNullOrEmpty(SelectedProtocolFilter) && SelectedProtocolFilter != AllProtocolsFilterLabel)
            query = query.Where(c => c.Protocol.ToInvariantDisplayName() == SelectedProtocolFilter);

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var terms = SearchText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var term in terms)
            {
                var cleanTerm = StringNormalizationHelper.RemoveDiacritics(term);
                query = query.Where(c =>
                    StringNormalizationHelper.RemoveDiacritics(c.Name).Contains(cleanTerm, StringComparison.OrdinalIgnoreCase) ||
                    StringNormalizationHelper.RemoveDiacritics(c.ClientName).Contains(cleanTerm, StringComparison.OrdinalIgnoreCase) ||
                    StringNormalizationHelper.RemoveDiacritics(c.GroupName).Contains(cleanTerm, StringComparison.OrdinalIgnoreCase) ||
                    StringNormalizationHelper.RemoveDiacritics(c.Host).Contains(cleanTerm, StringComparison.OrdinalIgnoreCase) ||
                    StringNormalizationHelper.RemoveDiacritics(c.Username).Contains(cleanTerm, StringComparison.OrdinalIgnoreCase) ||
                    StringNormalizationHelper.RemoveDiacritics(c.Domain).Contains(cleanTerm, StringComparison.OrdinalIgnoreCase) ||
                    StringNormalizationHelper.RemoveDiacritics(c.Tags).Contains(cleanTerm, StringComparison.OrdinalIgnoreCase));
            }
        }

        var effectiveSort = CurrentViewMode == ConnectionViewMode.Recent || SelectedNavigationNode?.Kind == NavigationNodeKind.Recent
            ? ConnectionSortOption.LastOpenedDesc
            : SelectedSortOption;

        query = effectiveSort switch
        {
            ConnectionSortOption.ClientAsc => query
                .OrderBy(c => c.ClientName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.GroupName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase),

            ConnectionSortOption.LastOpenedDesc => query
                .OrderByDescending(c => c.LastOpenedAtUtc ?? DateTime.MinValue)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase),

            _ => query
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.ClientName, StringComparer.OrdinalIgnoreCase)
        };

        var currentSelectedId = targetSelectId ?? SelectedConnection?.Id;
        var results = query.ToList(); // Materialize before publishing a complete list.
        CaptureSession();
        var previous = _suppressSelectionGuard;
        _suppressSelectionGuard = true;
        try
        {
            var next = results.FirstOrDefault(c => c.Id == currentSelectedId) ?? results.FirstOrDefault();
            FilteredConnections = new ObservableCollection<ConnectionEntry>(results);
            FilteredConnectionsCount = results.Count;
            if (_activeSession?.IsNew == true) SelectedConnection = null;
            else
            {
                SelectedConnection = next;
                if (next?.Id != _activeSession?.Entry.Id ||
                    (_activeSession != null && !_activeSession.IsDirty && !IsSaving))
                    LoadSelectedIntoViewer(next);
            }
        }
        finally { _suppressSelectionGuard = previous; }
        NotifySelectedSummaryProperties();
        NotifyEditState();
    }

    private void RebuildNavigationAndClientFilters()
    {
        var previousGuard = _suppressSelectionGuard;
        _suppressSelectionGuard = true;
        try
        {
        var previousClientFilter = SelectedClientFilter;
        var previousNode = SelectedNavigationNode;

        var distinctClients = _allConnections
            .Select(c => c.ClientName.Trim())
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ClientFilterOptions.Clear();
        ClientFilterOptions.Add(AllClientsFilterLabel);
        foreach (var cl in distinctClients)
        {
            ClientFilterOptions.Add(cl);
        }

        SelectedClientFilter = ClientFilterOptions.Contains(previousClientFilter)
            ? previousClientFilter
            : AllClientsFilterLabel;

        NavigationNodes.Clear();

        var allNode = new NavigationNodeViewModel
        {
            Kind = NavigationNodeKind.All,
            Title = "Todas as Conexões",
            Count = _allConnections.Count,
            IconText = "🌐"
        };

        var favNode = new NavigationNodeViewModel
        {
            Kind = NavigationNodeKind.Favorites,
            Title = "Favoritos",
            Count = _allConnections.Count(c => c.IsFavorite),
            IconText = "★"
        };

        var recentNode = new NavigationNodeViewModel
        {
            Kind = NavigationNodeKind.Recent,
            Title = "Recentes",
            Count = _allConnections.Count(c => c.LastOpenedAtUtc.HasValue),
            IconText = "🕒"
        };

        NavigationNodes.Add(allNode);
        NavigationNodes.Add(favNode);
        NavigationNodes.Add(recentNode);

        foreach (var client in distinctClients)
        {
            var clientConns = _allConnections
                .Where(c => string.Equals(c.ClientName.Trim(), client, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var clientNode = new NavigationNodeViewModel
            {
                Kind = NavigationNodeKind.Client,
                Title = client,
                ClientName = client,
                Count = clientConns.Count,
                IconText = "🏢",
                IsExpanded = distinctClients.Count <= 25
            };

            var groups = clientConns
                .Select(c => string.IsNullOrWhiteSpace(c.GroupName) ? "Geral" : c.GroupName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase);

            foreach (var grp in groups)
            {
                var grpCount = clientConns.Count(c =>
                    string.Equals(string.IsNullOrWhiteSpace(c.GroupName) ? "Geral" : c.GroupName.Trim(), grp, StringComparison.OrdinalIgnoreCase));

                clientNode.Children.Add(new NavigationNodeViewModel
                {
                    Kind = NavigationNodeKind.Group,
                    Title = grp,
                    ClientName = client,
                    GroupName = grp,
                    Count = grpCount,
                    IconText = "📁"
                });
            }

            NavigationNodes.Add(clientNode);
        }

            if (previousNode != null)
            {
                SelectedNavigationNode = FindMatchingNode(NavigationNodes, previousNode) ?? allNode;
            }
            else
            {
                SelectedNavigationNode = allNode;
            }

            if (SelectedNavigationNode != null)
                SelectedNavigationNode.IsSelected = true;
        }
        finally { _suppressSelectionGuard = previousGuard; }
    }

    private static NavigationNodeViewModel? FindMatchingNode(
        IEnumerable<NavigationNodeViewModel> nodes,
        NavigationNodeViewModel target)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == target.Kind &&
                string.Equals(node.ClientName, target.ClientName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(node.GroupName, target.GroupName, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            var childMatch = FindMatchingNode(node.Children, target);
            if (childMatch != null)
                return childMatch;
        }
        return null;
    }

    // =========================================================================
    // Comandos Principais (Novo, Editar, Salvar, Cancelar, Duplicar, Excluir, Conectar)
    // =========================================================================

    private void NewConnectionInternal()
    {
        CaptureSession();
        var fresh = new ConnectionEntry
        {
            ClientName = SelectedNavigationNode?.ClientName ??
                (SelectedClientFilter != AllClientsFilterLabel ? SelectedClientFilter : string.Empty),
            GroupName = SelectedNavigationNode?.GroupName ?? "Geral"
        };
        OpenNewDraft(new ConnectionEditSession(fresh, isNew: true));
        StatusMessage = L.Text("Novo rascunho. Preencha os campos e clique em Salvar.");
    }

    private void OpenNewDraft(ConnectionEditSession session)
    {
        _editSessions[session.Entry.Id] = session;
        NewDrafts.Add(session.Entry);
        var previous = _suppressSelectionGuard;
        _suppressSelectionGuard = true;
        try { SelectedConnection = null; LoadSession(session); }
        finally { _suppressSelectionGuard = previous; }
    }

    [RelayCommand]
    public Task NewConnectionAsync() { NewConnectionInternal(); return Task.CompletedTask; }
    public void NewConnection() => NewConnectionInternal();

    [RelayCommand]
    public void BeginEdit() { IsEditing = _activeSession != null; }

    [RelayCommand]
    public void MarkPasswordForRemoval()
    {
        if (_activeSession == null) return;
        var previous = _suppressSelectionGuard;
        _suppressSelectionGuard = true;
        try
        {
            InlinePasswordInput = string.Empty;
            _activeSession.Password = string.Empty;
            _activeSession.PasswordIntent = string.IsNullOrEmpty(EditingEntry.CredentialId)
                ? PasswordEditIntent.Keep : PasswordEditIntent.Remove;
            IsPasswordMarkedForRemoval = _activeSession.PasswordIntent == PasswordEditIntent.Remove;
            _activeSession.Revision++;
            _activeSession.PasswordRevision++;
            HideRevealedPassword();
        }
        finally { _suppressSelectionGuard = previous; }
        NotifyPasswordStatusProperties();
    }

    [RelayCommand]
    public async Task<bool> SaveConnectionAsync()
    {
        var dispatcher = _uiDispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            return await dispatcher.InvokeAsync(SaveConnectionAsync).Task.Unwrap();
        if (IsSaving || _activeSession == null || !HasUnsavedChanges() || IsTrashMode) return false;
        CaptureSession();
        // All inputs and the identity are captured before the first asynchronous operation.
        var session = _activeSession;
        var snapshot = EditingEntry.Clone();
        var revision = session.Revision;
        var passwordRevision = session.PasswordRevision;
        var password = session.Password;
        var intent = session.PasswordIntent;
        var credentialName = session.CredentialName;
        using var redaction = _logger.Redact([password]);
        IsSaving = true;
        try
        {
            var parsed = HostPortParser.Parse(InlineHostAndPort, snapshot.Protocol);
            if (!parsed.IsValid) { StatusMessage = L.Format("Corrija o endereço/porta: {0}", (L.AsMessage(parsed.ErrorMessage))); return false; }
            snapshot.Host = parsed.CleanHost;
            snapshot.Port = parsed.Port;
            snapshot.Name = snapshot.Name.Trim();
            snapshot.ClientName = session.IsNew && string.IsNullOrWhiteSpace(snapshot.ClientName) ? "Geral" : snapshot.ClientName.Trim();
            snapshot.GroupName = session.IsNew && string.IsNullOrWhiteSpace(snapshot.GroupName) ? "Geral" : snapshot.GroupName.Trim();
            snapshot.PrivateKeyPath = snapshot.PrivateKeyPath.Trim();
            snapshot.Tags = snapshot.Tags.Trim();
            snapshot.VpnName = snapshot.VpnName.Trim();
            snapshot.VpnApp = snapshot.VpnApp.Trim();
            snapshot.VpnServer = snapshot.VpnServer.Trim();
            var errors = ConnectionValidator.ValidateConnection(snapshot);
            if (errors.Count > 0) { StatusMessage = L.Format("Corrija os campos: {0}", (L.AsMessage(errors[0]))); return false; }
            var result = await _persistence.SaveAsync(snapshot, intent, password, credentialName);
            snapshot = result.Entry;
            _dataRevision++;
            CaptureSession();
            session.Baseline = snapshot.Clone();
            session.IsNew = false;
            var unchanged = session.Revision == revision;
            var previousGuard = _suppressSelectionGuard;
            _suppressSelectionGuard = true;
            try
            {
            if (unchanged)
            {
                // Detach the old editor before replacing its object.
                if (ReferenceEquals(_activeSession, session))
                    session.Entry.PropertyChanged -= EditingEntryChanged;
                session.Entry = snapshot.Clone();
            }
            else
            {
                // Rebase only the committed credential link; keep every later field edit.
                session.Entry.CredentialId = snapshot.CredentialId;
                session.Entry.UpdatedAtUtc = snapshot.UpdatedAtUtc;
            }
            if (session.PasswordRevision == passwordRevision)
            {
                if (intent is PasswordEditIntent.Replace or PasswordEditIntent.Remove) { session.CredentialRecoveryFailed = false; session.CredentialRecoveryMessage = null; }
                session.Password = string.Empty;
                session.LoadedPassword = null;
                session.PasswordIntent = PasswordEditIntent.Keep;
            }
            else
            {
                // A later password edit is relative to the secret just committed.
                // In particular, erasing a submitted new password is still a removal.
                if (intent == PasswordEditIntent.Replace) session.LoadedPassword = password;
                else if (intent == PasswordEditIntent.Remove) session.LoadedPassword = null;
                session.PasswordIntent = session.Password.Length == 0
                    ? snapshot.CredentialId == null ? PasswordEditIntent.Keep : PasswordEditIntent.Remove
                    : session.Password == session.LoadedPassword ? PasswordEditIntent.Keep : PasswordEditIntent.Replace;
            }
            if (unchanged)
            {
                session.HostText = HostPortParser.FormatDisplay(snapshot.Host, snapshot.Port, snapshot.Protocol);
                session.UserText = UserIdentityFormatter.FormatDisplay(snapshot.Username, snapshot.Domain);
            }
            }
            finally { _suppressSelectionGuard = previousGuard; }
            NewDrafts.Remove(NewDrafts.FirstOrDefault(c => c.Id == snapshot.Id)!);
            ReplacePersistedEntry(snapshot);
            if (ReferenceEquals(_activeSession, session)) LoadSession(session);
            var previous = _suppressSelectionGuard;
            _suppressSelectionGuard = true;
            try { RefreshCountsAndNavigation(); }
            finally { _suppressSelectionGuard = previous; }
            ApplyFilterAndSort(ReferenceEquals(_activeSession, session) ? snapshot.Id : null);
            StatusMessage = result.CleanupPending
                ? L.Text("Conexão salva. Limpeza do cofre pendente; será tentada novamente.")
                : L.Format("Conexão '{0}' salva.", (snapshot.Name));
            return true;
        }
        catch (Exception ex)
        {
            _logger.RecordFailure("Save", ex);
            StatusMessage = L.Text("Não foi possível salvar. Suas alterações foram mantidas; tente novamente.");
            return false;
        }
        finally { IsSaving = false; NotifyPasswordStatusProperties(); }
    }

    private void ReplacePersistedEntry(ConnectionEntry entry)
    {
        _allConnections.RemoveAll(c => c.Id == entry.Id);
        _trashConnections.RemoveAll(c => c.Id == entry.Id);
        (entry.IsDeleted ? _trashConnections : _allConnections).Add(entry.Clone());
    }

    [RelayCommand]
    public void CancelEdit()
    {
        if (_activeSession == null || IsSaving) return;
        var session = _activeSession;
        _editSessions.Remove(session.Entry.Id);
        if (session.IsNew)
        {
            NewDrafts.Remove(session.Entry);
            LoadSession(null);
            ApplyFilterAndSort();
        }
        else
        {
            var persisted = _allConnections.Concat(_trashConnections).FirstOrDefault(c => c.Id == session.Entry.Id);
            LoadSelectedIntoViewer(persisted);
        }
        StatusMessage = L.Text("Alterações descartadas.");
    }

    [RelayCommand]
    public Task DuplicateConnectionAsync()
    {
        var target = SelectedConnection;
        if (target == null) return Task.CompletedTask;
        CaptureSession();
        var duplicate = target.CreateDuplicate();
        var session = new ConnectionEditSession(duplicate, isNew: true);
        session.Baseline.Name = string.Empty;
        OpenNewDraft(session);
        StatusMessage = L.Text("Cópia criada como rascunho. Clique em Salvar para gravar.");
        return Task.CompletedTask;
    }

    [RelayCommand]
    public async Task DeleteConnectionAsync()
    {
        var target = SelectedConnection ?? EditingEntry;
        if (target == null || string.IsNullOrWhiteSpace(target.Id))
        {
            StatusMessage = L.Text("Selecione uma conexão para excluir.");
            return;
        }

        if (CurrentViewMode == ConnectionViewMode.Trash)
        {
            if (ConfirmDeleteCallback != null)
            {
                var confirmed = ConfirmDeleteCallback(
                    L.Text("Excluir Definitivamente"),
                    L.Format("Deseja realmente excluir permanentemente a conexão '{0}'?\nEsta ação não poderá ser desfeita.", (target.Name)));
                if (!confirmed)
                    return;
            }

            IsEditing = false;
            IsNewRecord = false;
            await _repository.PermanentlyDeleteConnectionAsync(target.Id);
            await ReloadAllDataAsync();
            StatusMessage = L.Format("Conexão '{0}' excluída definitivamente.", (target.Name));
        }
        else
        {
            if (ConfirmDeleteCallback != null)
            {
                var confirmed = ConfirmDeleteCallback(
                    L.Text("Mover para a Lixeira"),
                    L.Format("Deseja mover a conexão '{0}' para a lixeira?", (target.Name)));
                if (!confirmed)
                    return;
            }

            IsEditing = false;
            IsNewRecord = false;
            await _repository.SoftDeleteConnectionAsync(target.Id);
            await ReloadAllDataAsync();
            StatusMessage = L.Format("Conexão '{0}' movida para a Lixeira.", (target.Name));
        }
    }

    [RelayCommand]
    public async Task RestoreSelectedConnectionAsync()
    {
        var target = SelectedConnection ?? EditingEntry;
        if (target == null || string.IsNullOrWhiteSpace(target.Id))
        {
            StatusMessage = L.Text("Selecione uma conexão na lixeira para restaurar.");
            return;
        }

        var conflict = _allConnections.FirstOrDefault(c => string.Equals(c.Name, target.Name, StringComparison.OrdinalIgnoreCase));
        if (conflict != null)
        {
            if (ConfirmDiscardUnsavedChangesCallback != null)
            {
                var rename = ConfirmDiscardUnsavedChangesCallback(
                    L.Format("Já existe uma conexão ativa com o nome '{0}'.\nDeseja restaurar renomeando automaticamente para '{1} (Restaurada)'?", (target.Name), (target.Name)));
                if (rename)
                {
                    target.Name = $"{target.Name} (Restaurada)";
                    await _repository.SaveConnectionAsync(target);
                }
                else
                {
                    StatusMessage = L.Text("Restauração cancelada.");
                    return;
                }
            }
        }

        await _repository.RestoreConnectionAsync(target.Id);
        await ReloadAllDataAsync(selectConnectionId: target.Id);
        StatusMessage = L.Format("Conexão '{0}' restaurada com sucesso.", (target.Name));
    }

    [RelayCommand]
    public async Task EmptyTrashAsync()
    {
        if (_trashConnections.Count == 0)
        {
            StatusMessage = L.Text("A lixeira já está vazia.");
            return;
        }

        if (ConfirmDeleteCallback != null)
        {
            var confirmed = ConfirmDeleteCallback(
                L.Text("Esvaziar Lixeira"),
                L.Format("Deseja realmente excluir definitivamente todos os {0} itens da lixeira?\nEsta ação não poderá ser desfeita.", (_trashConnections.Count)));
            if (!confirmed)
                return;
        }

        await _repository.EmptyRecycleBinAsync();
        await ReloadAllDataAsync();
        StatusMessage = L.Text("Lixeira esvaziada com sucesso.");
    }

    [RelayCommand]
    public async Task ConnectAsync()
    {
        var id = SelectedConnection?.Id;
        if (id == null) { StatusMessage = "Salve o novo cadastro antes de conectar."; return; }
        var target = await _repository.GetConnectionByIdAsync(id);
        if (target == null || target.IsDeleted) return;


        var result = await _launcherService.LaunchAsync(target);
        StatusMessage = result.StatusMessage;
        SecurityBannerMessage = result.SecurityNotice;

        if (!result.Started)
        {
            if (result.IsMissingExecutable)
            {
                ShowErrorDialogCallback?.Invoke(
                    L.Text("Cliente Externo Ausente"),
                    result.ErrorMessage + L.Text("\n\nUse o botão de Configurações (⚙) na barra superior para localizar o programa."));
                OpenExecutableSettingsCallback?.Invoke();
            }
            else
            {
                ShowErrorDialogCallback?.Invoke(L.Text("Não foi possível iniciar o cliente"), result.ErrorMessage);
            }
            return;
        }

        OnPropertyChanged(nameof(SelectedConnection));
        var listed = _allConnections.FirstOrDefault(c => c.Id == target.Id);
        if (listed != null) listed.LastOpenedAtUtc = target.LastOpenedAtUtc;
        if (EditingEntry.Id == target.Id) EditingEntry.LastOpenedAtUtc = target.LastOpenedAtUtc;
        OnPropertyChanged(nameof(EditingEntry));
        _dataRevision++;
        RebuildNavigationAndClientFilters();
        ApplyFilterAndSort();
    }

    [RelayCommand]
    public async Task ToggleFavoriteAsync(ConnectionEntry? entry)
    {
        var target = entry ?? SelectedConnection;
        if (target == null)
            return;

        if (IsTrashMode) return;
        await RequestNavigateToConnectionAsync(target);
        EditingEntry.IsFavorite = !EditingEntry.IsFavorite;
        StatusMessage = L.Text("Favorito alterado no rascunho. Clique em Salvar para gravar.");
    }

    // =========================================================================
    // Cópia Manual, Revelação Temporária de Senha e Bloqueio (Ctrl+L)
    // =========================================================================

    [RelayCommand]
    public Task CopyConnectionNameAsync() => CopyAccessFieldAsync(AccessCopyField.Name);

    [RelayCommand]
    public Task CopyHostAsync() => CopyAccessFieldAsync(AccessCopyField.Address);

    [RelayCommand]
    public Task CopyHostAndPortAsync() => CopyAccessFieldAsync(AccessCopyField.AddressWithPort);

    [RelayCommand]
    public Task CopyUsernameAsync() => CopyAccessFieldAsync(AccessCopyField.User);

    [RelayCommand]
    public async Task CopyVpnNameAsync()
    {
        var text = !string.IsNullOrWhiteSpace(EditingEntry?.VpnName)
            ? EditingEntry.VpnName
            : SelectedConnection?.VpnName;

        if (string.IsNullOrWhiteSpace(text))
            return;

        if (await _clipboardService.CopyTextWithRetryAsync(text))
            StatusMessage = L.Text("Nome da VPN copiado");
    }

    [RelayCommand]
    public async Task CopyVpnServerAsync()
    {
        var text = !string.IsNullOrWhiteSpace(EditingEntry?.VpnServer)
            ? EditingEntry.VpnServer
            : SelectedConnection?.VpnServer;

        if (string.IsNullOrWhiteSpace(text))
            return;

        if (await _clipboardService.CopyTextWithRetryAsync(text))
            StatusMessage = L.Text("Servidor da VPN copiado");
    }

    [RelayCommand]
    public Task CopyPasswordAsync() => CopyAccessFieldAsync(AccessCopyField.Password);

    private async Task<string?> RecoverPasswordAsync(SavedCredential credential)
        => await RecoverPasswordAsync(credential, _activeSession);

    private async Task<string?> RecoverPasswordAsync(SavedCredential credential, ConnectionEditSession? session)
    {
        try
        {
            var secret = await _vaultService.ReadSecretAsync(credential);
            if (string.IsNullOrEmpty(secret)) throw new System.Security.Cryptography.CryptographicException(L.Text("Credencial salva ilegível."));
            if (session != null) { session.CredentialRecoveryFailed = false; session.CredentialRecoveryMessage = null; }
            NotifyPasswordStatusProperties();
            return secret;
        }
        catch (Exception exception)
        {
            _logger.RecordFailure("Credentials.Read", exception);
            if (session != null) { session.CredentialRecoveryFailed = true; session.CredentialRecoveryMessage = CredentialVaultService.RecoveryMessage(exception); }
            if (session == _activeSession)
            {
                StatusMessage = CredentialVaultService.RecoveryMessage(exception);
                NotifyPasswordStatusProperties();
            }
            return null;
        }
    }

    [RelayCommand]
    public async Task TogglePasswordVisibilityAsync()
    {
        if (IsPasswordRevealed)
        {
            HideRevealedPassword();
            return;
        }

        if (!string.IsNullOrEmpty(InlinePasswordInput))
        {
            RevealedPasswordText = InlinePasswordInput;
            IsPasswordRevealed = true;
            return;
        }

        if (IsPasswordMarkedForRemoval) return;
        var generation = _editorGeneration;
        var session = _activeSession;
        var passwordRevision = session?.PasswordRevision;
        var credId = EditingEntry?.CredentialId;
        if (string.IsNullOrWhiteSpace(credId))
        {
            StatusMessage = L.Text("Nenhuma senha cadastrada.");
            return;
        }


        var cred = await _repository.GetCredentialByIdAsync(credId);
        if (cred == null)
        {
            if (_activeSession != null) _activeSession.CredentialRecoveryFailed = true;
            StatusMessage = L.Text("Falha ao recuperar credencial");
            NotifyPasswordStatusProperties();
            return;
        }

        var secret = await RecoverPasswordAsync(cred);
        if (generation != _editorGeneration || session != _activeSession || passwordRevision != session?.PasswordRevision) return;
        if (string.IsNullOrEmpty(secret)) return;

        _passwordRevealCts?.Cancel();
        _passwordRevealCts?.Dispose();
        var cts = new CancellationTokenSource();
        _passwordRevealCts = cts;

        if (session != null) session.LoadedPassword = secret;
        RevealedPasswordText = secret;
        IsPasswordRevealed = true;
        var seconds = Math.Max(5, _settingsService.Current.PasswordRevealSeconds);
        StatusMessage = L.Format("Senha exibida temporariamente por {0} segundos.", (seconds));

        _ = ScheduleAutoHidePasswordAsync(seconds, cts.Token);
    }

    private async Task ScheduleAutoHidePasswordAsync(int seconds, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), token);
            if (!token.IsCancellationRequested)
            {
                if (_uiDispatcher != null && !_uiDispatcher.CheckAccess())
                {
                    await _uiDispatcher.InvokeAsync(() =>
                    {
                        HideRevealedPassword();
                        StatusMessage = L.Text("Senha ocultada novamente.");
                    });
                }
                else
                {
                    HideRevealedPassword();
                    StatusMessage = L.Text("Senha ocultada novamente.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    public async Task RevealPasswordTemporarilyAsync()
    {
        await TogglePasswordVisibilityAsync();
    }

    public void HideRevealedPassword()
    {
        _passwordRevealCts?.Cancel();
        _passwordRevealCts?.Dispose();
        _passwordRevealCts = null;
        RevealedPasswordText = string.Empty;
        IsPasswordRevealed = false;
    }

    // =========================================================================
    // Diagnóstico de Rede (DNS + TCP com Timeout e Cancelamento)
    // =========================================================================

    [RelayCommand]
    public async Task RunDiagnosticAsync()
    {
        if (IsRunningDiagnostic)
            return;

        var target = SelectedConnection ?? EditingEntry;
        if (target == null || string.IsNullOrWhiteSpace(target.Host))
            return;

        _diagnosticCts?.Cancel();
        _diagnosticCts?.Dispose();
        var cts = new CancellationTokenSource();
        _diagnosticCts = cts;

        IsRunningDiagnostic = true;
        DiagnosticResultTitle = L.Text("Testando conexão...");
        DiagnosticResultDetails = L.Format("Verificando {0}:{1} (timeout: 4s)...", target.Host, target.Port);

        try
        {
            var result = await _diagnosticService.TestConnectivityAsync(
                target.Host,
                target.Port,
                timeoutMs: 4000,
                cancellationToken: cts.Token);

            DiagnosticResultTitle = result.SummaryTitle;
            DiagnosticResultDetails = result.DetailedMessage;
            StatusMessage = L.Format("Teste concluído: {0}", (result.SummaryTitle));
        }
        finally
        {
            IsRunningDiagnostic = false;
        }
    }

    [RelayCommand]
    public void CancelDiagnostic()
    {
        _diagnosticCts?.Cancel();
    }
}
