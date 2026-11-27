using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConectaTI.App.Services.Updates;

public sealed class ReleaseComponentManifest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("executableName")]
    public string ExecutableName { get; set; } = string.Empty;

    [JsonPropertyName("length")]
    public long Length { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("sourceRepository")]
    public string SourceRepository { get; set; } = string.Empty;

    [JsonPropertyName("sourceCommit")]
    public string? SourceCommit { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("updateOrigin")]
    public string UpdateOrigin { get; set; } = AppUpdateService.OfficialGitHubRepo;
}

public sealed class ReleaseComponentsContainer
{
    [JsonPropertyName("conectaTi")]
    public ReleaseComponentManifest? ConectaTi { get; set; }

    [JsonPropertyName("hotKeyText")]
    public ReleaseComponentManifest? HotKeyText { get; set; }
}

public sealed class ReleasePackageManifest
{
    [JsonPropertyName("application")]
    public string Application { get; set; } = "ConectaTI";

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("suiteVersion")]
    public string SuiteVersion { get; set; } = string.Empty;

    [JsonPropertyName("updateOrigin")]
    public string UpdateOrigin { get; set; } = AppUpdateService.OfficialGitHubRepo;

    [JsonPropertyName("repositoryUrl")]
    public string RepositoryUrl { get; set; } = AppUpdateService.OfficialRepositoryUrl;

    [JsonPropertyName("runtime")]
    public string Runtime { get; set; } = "win-x64";

    [JsonPropertyName("framework")]
    public string Framework { get; set; } = "net10.0-windows";

    [JsonPropertyName("selfContained")]
    public bool SelfContained { get; set; } = true;

    [JsonPropertyName("singleFile")]
    public bool SingleFile { get; set; } = true;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 11;

    [JsonPropertyName("publishedAtUtc")]
    public string PublishedAtUtc { get; set; } = string.Empty;

    [JsonPropertyName("executableName")]
    public string ExecutableName { get; set; } = "ConectaTI.exe";

    [JsonPropertyName("executableLength")]
    public long ExecutableLength { get; set; }

    [JsonPropertyName("executableSha256")]
    public string ExecutableSha256 { get; set; } = string.Empty;

    [JsonPropertyName("zipName")]
    public string ZipName { get; set; } = string.Empty;

    [JsonPropertyName("zipLength")]
    public long ZipLength { get; set; }

    [JsonPropertyName("zipSha256")]
    public string ZipSha256 { get; set; } = string.Empty;

    [JsonPropertyName("components")]
    public ReleaseComponentsContainer? Components { get; set; }

    [JsonPropertyName("userDataFilesIncluded")]
    public List<string> UserDataFilesIncluded { get; set; } = [];
}

public sealed class RollbackMetadata
{
    [JsonPropertyName("backedUpAtUtc")]
    public string BackedUpAtUtc { get; set; } = string.Empty;

    [JsonPropertyName("previousVersion")]
    public string PreviousVersion { get; set; } = string.Empty;

    [JsonPropertyName("previousHotKeyTextVersion")]
    public string PreviousHotKeyTextVersion { get; set; } = string.Empty;

    [JsonPropertyName("conectaTiSha256")]
    public string ConectaTiSha256 { get; set; } = string.Empty;

    [JsonPropertyName("hotKeyTextSha256")]
    public string HotKeyTextSha256 { get; set; } = string.Empty;

    [JsonPropertyName("hadStandaloneHotKeyTextExecutable")]
    public bool HadStandaloneHotKeyTextExecutable { get; set; }
}

public sealed record UpdateCheckResult(
    bool Success,
    bool IsUpdateAvailable,
    string InstalledVersion,
    string AvailableVersion,
    string InstalledHotKeyTextVersion,
    string AvailableHotKeyTextVersion,
    string StatusMessage,
    string? PackageDownloadUrl = null,
    string? ManifestDownloadUrl = null,
    string? ReleaseHtmlUrl = null,
    string? ReleaseNotes = null,
    ReleasePackageManifest? Manifest = null,
    string? ErrorMessage = null);

public sealed record UpdateApplyResult(
    bool Success,
    bool RolledBack,
    string InstalledVersionBefore,
    string InstalledVersionAfter,
    string BackupPath,
    string Message,
    string? ErrorDetails = null);

/// <summary>
/// Serviço de consulta, validação criptográfica (autenticidade + SHA-256 + cabeçalho PE) e aplicação
/// transacional de atualizações do conjunto ConectaTI + HotKeyText a partir de MaxBSilva/ConectaTI.
/// </summary>
public sealed class AppUpdateService
{
    public const string OfficialGitHubRepo = "MaxBSilva/ConectaTI";
    public const string OfficialRepositoryUrl = "https://github.com/MaxBSilva/ConectaTI";
    public const string OfficialReleasesLatestApiUrl = "https://api.github.com/repos/MaxBSilva/ConectaTI/releases/latest";
    public const string UpToDateMessage = "Você já está na versão mais recente";

    private static readonly string[] ProhibitedUserDataEntries =
    [
        "sqlite.db",
        "sqlite.db-wal",
        "sqlite.db-shm",
        "settings.json",
        "textos.xml",
        "backups/",
        "logs/"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _releasesLatestUrl;
    private readonly bool _allowLocalFileUrls;

    public AppUpdateService(
        HttpClient? httpClient = null,
        string? releasesLatestUrl = null,
        bool allowLocalFileUrls = false)
    {
        _httpClient = httpClient ?? CreateDefaultHttpClient();
        _releasesLatestUrl = string.IsNullOrWhiteSpace(releasesLatestUrl)
            ? OfficialReleasesLatestApiUrl
            : releasesLatestUrl;
        _allowLocalFileUrls = allowLocalFileUrls;
    }

    public static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ConectaTI-Updater", GetInstalledVersion()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static string GetInstalledVersion(string? installDirectory = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(installDirectory))
            {
                var manifestPath = Path.Combine(installDirectory, "release-manifest.json");
                if (File.Exists(manifestPath))
                {
                    var manifest = JsonSerializer.Deserialize<ReleasePackageManifest>(File.ReadAllText(manifestPath), JsonOptions);
                    if (!string.IsNullOrWhiteSpace(manifest?.Version))
                        return NormalizeVersionString(manifest.Version);
                }

                var exePath = Path.Combine(installDirectory, "ConectaTI.exe");
                if (File.Exists(exePath))
                {
                    var info = FileVersionInfo.GetVersionInfo(exePath);
                    if (!string.IsNullOrWhiteSpace(info.ProductVersion))
                        return NormalizeVersionString(info.ProductVersion);
                    if (!string.IsNullOrWhiteSpace(info.FileVersion))
                        return NormalizeVersionString(info.FileVersion);
                }
            }
        }
        catch { }

        var asmVersion = typeof(AppUpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AppUpdateService).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";

        return NormalizeVersionString(asmVersion);
    }

    public static string GetInstalledHotKeyTextVersion(string? installDirectory = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(installDirectory))
            {
                var manifestPath = Path.Combine(installDirectory, "release-manifest.json");
                if (File.Exists(manifestPath))
                {
                    var manifest = JsonSerializer.Deserialize<ReleasePackageManifest>(File.ReadAllText(manifestPath), JsonOptions);
                    if (!string.IsNullOrWhiteSpace(manifest?.Components?.HotKeyText?.Version))
                        return NormalizeVersionString(manifest.Components.HotKeyText.Version);
                }

                var legacyExe = Path.Combine(installDirectory, "HotKeyText.exe");
                if (File.Exists(legacyExe))
                {
                    var info = FileVersionInfo.GetVersionInfo(legacyExe);
                    if (!string.IsNullOrWhiteSpace(info.ProductVersion))
                        return NormalizeVersionString(info.ProductVersion);
                    if (!string.IsNullOrWhiteSpace(info.FileVersion))
                        return NormalizeVersionString(info.FileVersion);
                }
            }
        }
        catch { }

        return GetInstalledVersion(installDirectory);
    }

    public static string GetRollbackDirectory(string installDirectory)
        => Path.Combine(Path.GetFullPath(installDirectory), "update-rollback", "previous-install");

    public static string GetRollbackMetadataPath(string installDirectory)
        => Path.Combine(GetRollbackDirectory(installDirectory), "rollback-info.json");

    public static bool HasRollbackBackup(string? installDirectory = null)
    {
        var dir = string.IsNullOrWhiteSpace(installDirectory) ? AppContext.BaseDirectory : installDirectory;
        var rollbackDir = GetRollbackDirectory(dir);
        return File.Exists(GetRollbackMetadataPath(dir)) &&
               File.Exists(Path.Combine(rollbackDir, "ConectaTI.exe"));
    }

    public static RollbackMetadata? TryReadRollbackMetadata(string? installDirectory = null)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(installDirectory) ? AppContext.BaseDirectory : installDirectory;
            var metaPath = GetRollbackMetadataPath(dir);
            if (!File.Exists(metaPath))
                return null;
            return JsonSerializer.Deserialize<RollbackMetadata>(File.ReadAllText(metaPath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        string? installDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var installedVersion = GetInstalledVersion(installDirectory);
        var installedHotKeyVersion = GetInstalledHotKeyTextVersion(installDirectory);

        try
        {
            if (!IsAuthenticReleaseEndpoint(_releasesLatestUrl))
            {
                return new UpdateCheckResult(
                    Success: false,
                    IsUpdateAvailable: false,
                    InstalledVersion: installedVersion,
                    AvailableVersion: installedVersion,
                    InstalledHotKeyTextVersion: installedHotKeyVersion,
                    AvailableHotKeyTextVersion: installedHotKeyVersion,
                    StatusMessage: "Origem de atualização inválida.",
                    ErrorMessage: $"A origem de atualização deve ser exclusivamente {OfficialGitHubRepo}.");
            }

            using var response = await _httpClient.GetAsync(_releasesLatestUrl, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Quando o repositório ainda não tem Release publicada, considera a versão atual em dia.
                return new UpdateCheckResult(
                    Success: true,
                    IsUpdateAvailable: false,
                    InstalledVersion: installedVersion,
                    AvailableVersion: installedVersion,
                    InstalledHotKeyTextVersion: installedHotKeyVersion,
                    AvailableHotKeyTextVersion: installedHotKeyVersion,
                    StatusMessage: UpToDateMessage);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    Success: false,
                    IsUpdateAvailable: false,
                    InstalledVersion: installedVersion,
                    AvailableVersion: installedVersion,
                    InstalledHotKeyTextVersion: installedHotKeyVersion,
                    AvailableHotKeyTextVersion: installedHotKeyVersion,
                    StatusMessage: "Não foi possível consultar atualizações.",
                    ErrorMessage: $"O GitHub retornou HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True)
            {
                return new UpdateCheckResult(
                    Success: true,
                    IsUpdateAvailable: false,
                    InstalledVersion: installedVersion,
                    AvailableVersion: installedVersion,
                    InstalledHotKeyTextVersion: installedHotKeyVersion,
                    AvailableHotKeyTextVersion: installedHotKeyVersion,
                    StatusMessage: UpToDateMessage);
            }

            if (root.TryGetProperty("prerelease", out var preProp) && preProp.ValueKind == JsonValueKind.True)
            {
                return new UpdateCheckResult(
                    Success: true,
                    IsUpdateAvailable: false,
                    InstalledVersion: installedVersion,
                    AvailableVersion: installedVersion,
                    InstalledHotKeyTextVersion: installedHotKeyVersion,
                    AvailableHotKeyTextVersion: installedHotKeyVersion,
                    StatusMessage: UpToDateMessage);
            }

            var htmlUrl = root.TryGetProperty("html_url", out var htmlProp) ? htmlProp.GetString() : null;
            if (!string.IsNullOrWhiteSpace(htmlUrl) &&
                !IsOfficialGitHubPath(htmlUrl, "github.com", $"/{OfficialGitHubRepo}/releases/tag/"))
            {
                return new UpdateCheckResult(
                    Success: false,
                    IsUpdateAvailable: false,
                    InstalledVersion: installedVersion,
                    AvailableVersion: installedVersion,
                    InstalledHotKeyTextVersion: installedHotKeyVersion,
                    AvailableHotKeyTextVersion: installedHotKeyVersion,
                    StatusMessage: "Origem de Release não autorizada.",
                    ErrorMessage: $"A Release retornada ('{htmlUrl}') não pertence a {OfficialGitHubRepo}.");
            }

            var tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return new UpdateCheckResult(
                    Success: false,
                    IsUpdateAvailable: false,
                    InstalledVersion: installedVersion,
                    AvailableVersion: installedVersion,
                    InstalledHotKeyTextVersion: installedHotKeyVersion,
                    AvailableHotKeyTextVersion: installedHotKeyVersion,
                    StatusMessage: "Resposta inválida do GitHub.",
                    ErrorMessage: "A Release não informa tag_name.");
            }

            var remoteVersion = NormalizeVersionString(tagName);
            var releaseNotes = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;

            string? zipUrl = null;
            string? manifestUrl = null;

            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                    var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(downloadUrl))
                        continue;
                    if (!IsAuthenticDownloadUrl(downloadUrl))
                        throw new InvalidDataException($"URL de anexo fora das Releases de {OfficialGitHubRepo}.");

                    if (string.Equals(name, "release-manifest.json", StringComparison.OrdinalIgnoreCase))
                        manifestUrl = downloadUrl;
                    else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                             name.StartsWith("ConectaTI", StringComparison.OrdinalIgnoreCase))
                        zipUrl = downloadUrl;
                }
            }

            ReleasePackageManifest? manifest = null;
            if (!string.IsNullOrWhiteSpace(manifestUrl))
            {
                manifest = await FetchManifestAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
            }

            var availableHotKeyVersion = manifest?.Components?.HotKeyText?.Version ?? installedHotKeyVersion;

            if (CompareVersions(remoteVersion, installedVersion) <= 0)
            {
                return new UpdateCheckResult(
                    Success: true,
                    IsUpdateAvailable: false,
                    InstalledVersion: installedVersion,
                    AvailableVersion: remoteVersion,
                    InstalledHotKeyTextVersion: installedHotKeyVersion,
                    AvailableHotKeyTextVersion: availableHotKeyVersion,
                    StatusMessage: UpToDateMessage,
                    PackageDownloadUrl: zipUrl,
                    ManifestDownloadUrl: manifestUrl,
                    ReleaseHtmlUrl: htmlUrl,
                    ReleaseNotes: releaseNotes,
                    Manifest: manifest);
            }

            var statusMsg = $"Nova versão disponível: v{remoteVersion} (instalada: v{installedVersion})";
            return new UpdateCheckResult(
                Success: true,
                IsUpdateAvailable: true,
                InstalledVersion: installedVersion,
                AvailableVersion: remoteVersion,
                InstalledHotKeyTextVersion: installedHotKeyVersion,
                AvailableHotKeyTextVersion: availableHotKeyVersion,
                StatusMessage: statusMsg,
                PackageDownloadUrl: zipUrl,
                ManifestDownloadUrl: manifestUrl,
                ReleaseHtmlUrl: htmlUrl,
                ReleaseNotes: releaseNotes,
                Manifest: manifest);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                Success: false,
                IsUpdateAvailable: false,
                InstalledVersion: installedVersion,
                AvailableVersion: installedVersion,
                InstalledHotKeyTextVersion: installedHotKeyVersion,
                AvailableHotKeyTextVersion: installedHotKeyVersion,
                StatusMessage: "Falha ao consultar atualizações no GitHub.",
                ErrorMessage: ex.Message);
        }
    }

    public async Task<UpdateApplyResult> DownloadValidateAndApplyAsync(
        UpdateCheckResult checkResult,
        string targetInstallDirectory,
        string hotKeyTextInstancePrefix = "",
        Action<string>? progressCallback = null,
        Action<string>? faultInjectionAfterConectaCopy = null,
        CancellationToken cancellationToken = default)
    {
        var fullTargetDir = Path.GetFullPath(targetInstallDirectory);
        var versionBefore = GetInstalledVersion(fullTargetDir);
        var rollbackDir = GetRollbackDirectory(fullTargetDir);

        if (string.IsNullOrWhiteSpace(checkResult.PackageDownloadUrl))
        {
            return new UpdateApplyResult(
                Success: false,
                RolledBack: false,
                InstalledVersionBefore: versionBefore,
                InstalledVersionAfter: versionBefore,
                BackupPath: rollbackDir,
                Message: "A Release não possui um pacote .zip anexado para atualização.",
                ErrorDetails: "PackageDownloadUrl vazio.");
        }

        if (!IsAuthenticDownloadUrl(checkResult.PackageDownloadUrl) ||
            (!string.IsNullOrWhiteSpace(checkResult.ManifestDownloadUrl) && !IsAuthenticDownloadUrl(checkResult.ManifestDownloadUrl!)))
        {
            return new UpdateApplyResult(
                Success: false,
                RolledBack: false,
                InstalledVersionBefore: versionBefore,
                InstalledVersionAfter: versionBefore,
                BackupPath: rollbackDir,
                Message: "Pacote recusado na validação de autenticidade.",
                ErrorDetails: $"A URL de download não pertence ao repositório oficial {OfficialGitHubRepo}.");
        }

        var tempWorkRoot = Path.Combine(Path.GetTempPath(), "ConectaTI-Update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempWorkRoot);

        try
        {
            progressCallback?.Invoke("Baixando manifesto e pacote da Release...");

            var manifest = checkResult.Manifest;
            if (manifest is null && !string.IsNullOrWhiteSpace(checkResult.ManifestDownloadUrl))
            {
                manifest = await FetchManifestAsync(checkResult.ManifestDownloadUrl!, cancellationToken).ConfigureAwait(false);
            }

            if (manifest is null)
            {
                return new UpdateApplyResult(
                    Success: false,
                    RolledBack: false,
                    InstalledVersionBefore: versionBefore,
                    InstalledVersionAfter: versionBefore,
                    BackupPath: rollbackDir,
                    Message: "A Release não possui o manifesto criptográfico (release-manifest.json).",
                    ErrorDetails: "Manifesto ausente.");
            }

            var zipFilePath = Path.Combine(tempWorkRoot, "package.zip");
            await DownloadUrlToFileAsync(checkResult.PackageDownloadUrl, zipFilePath, cancellationToken).ConfigureAwait(false);

            progressCallback?.Invoke("Validando autenticidade e integridade (SHA-256)...");
            var stagingExtractDir = Path.Combine(tempWorkRoot, "extracted");
            Directory.CreateDirectory(stagingExtractDir);

            ValidatePackageAuthenticityAndIntegrity(manifest, zipFilePath, stagingExtractDir);

            progressCallback?.Invoke("Preparando ponto de recuperação da instalação atual...");
            CreateRollbackBackup(fullTargetDir);

            progressCallback?.Invoke("Encerrando processos auxiliares para substituição segura...");
            HotKeyTextService.StopRunningInstancesInDirectory(
                fullTargetDir,
                out var hotKeyWasRunning,
                instancePrefix: hotKeyTextInstancePrefix,
                timeoutMs: 5000);

            try
            {
                progressCallback?.Invoke("Aplicando nova versão dos componentes...");
                InstallStagedFiles(stagingExtractDir, manifest, fullTargetDir, faultInjectionAfterConectaCopy);

                if (hotKeyWasRunning)
                {
                    TryStartUpdatedHotKeyTextInTray(fullTargetDir, hotKeyTextInstancePrefix);
                }

                var versionAfter = GetInstalledVersion(fullTargetDir);
                return new UpdateApplyResult(
                    Success: true,
                    RolledBack: false,
                    InstalledVersionBefore: versionBefore,
                    InstalledVersionAfter: versionAfter,
                    BackupPath: rollbackDir,
                    Message: $"Atualização para v{versionAfter} concluída com sucesso.");
            }
            catch (Exception installEx)
            {
                progressCallback?.Invoke("Falha na instalação. Restaurando versão anterior automaticamente...");

                var restored = RestorePreviousInstallation(fullTargetDir, hotKeyTextInstancePrefix, out var restoreError);
                if (hotKeyWasRunning && restored)
                {
                    TryStartUpdatedHotKeyTextInTray(fullTargetDir, hotKeyTextInstancePrefix);
                }

                var versionAfterRollback = GetInstalledVersion(fullTargetDir);
                return new UpdateApplyResult(
                    Success: false,
                    RolledBack: restored,
                    InstalledVersionBefore: versionBefore,
                    InstalledVersionAfter: versionAfterRollback,
                    BackupPath: rollbackDir,
                    Message: restored
                        ? $"Falha ao aplicar a atualização ({installEx.Message}). A instalação anterior (v{versionAfterRollback}) foi restaurada automaticamente."
                        : $"Falha ao aplicar a atualização ({installEx.Message}) e erro ao restaurar backup ({restoreError}).",
                    ErrorDetails: installEx.ToString());
            }
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(
                Success: false,
                RolledBack: false,
                InstalledVersionBefore: versionBefore,
                InstalledVersionAfter: GetInstalledVersion(fullTargetDir),
                BackupPath: rollbackDir,
                Message: $"A atualização foi bloqueada na validação: {ex.Message}",
                ErrorDetails: ex.ToString());
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempWorkRoot))
                    Directory.Delete(tempWorkRoot, recursive: true);
            }
            catch { }
        }
    }

    public static void ValidatePackageAuthenticityAndIntegrity(
        ReleasePackageManifest manifest,
        string zipFilePath,
        string extractDirectory)
    {
        if (!string.Equals(manifest.Application, "ConectaTI", StringComparison.Ordinal))
            throw new InvalidDataException($"Manifesto inválido: aplicação '{manifest.Application}' diferente de 'ConectaTI'.");

        var origin = string.IsNullOrWhiteSpace(manifest.UpdateOrigin) ? OfficialGitHubRepo : manifest.UpdateOrigin;
        if (!string.Equals(origin, OfficialGitHubRepo, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Origem do manifesto ('{origin}') não autorizada. Origem exclusiva exigida: '{OfficialGitHubRepo}'.");

        if (!string.IsNullOrWhiteSpace(manifest.RepositoryUrl) &&
            !manifest.RepositoryUrl.Contains(OfficialGitHubRepo, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"URL de repositório não autorizada no manifesto: '{manifest.RepositoryUrl}'.");
        }

        if (manifest.UserDataFilesIncluded is { Count: > 0 })
            throw new InvalidDataException("O manifesto declara arquivos de dados de usuário, o que é proibido.");

        if (manifest.Components?.ConectaTi is null || manifest.Components?.HotKeyText is null)
            throw new InvalidDataException("O manifesto deve declarar as versões e hashes dos dois componentes (ConectaTI e HotKeyText).");

        if (!string.Equals(manifest.Components.HotKeyText.SourceRepository, HotKeyTextService.SourceRepository, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Referência de código-fonte do HotKeyText inválida: '{manifest.Components.HotKeyText.SourceRepository}'.");

        if (!string.Equals(manifest.Components.HotKeyText.UpdateOrigin, OfficialGitHubRepo, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Origem de atualização do HotKeyText inválida: '{manifest.Components.HotKeyText.UpdateOrigin}'.");

        var zipInfo = new FileInfo(zipFilePath);
        if (!zipInfo.Exists || zipInfo.Length <= 0)
            throw new InvalidDataException("O arquivo .zip baixado está vazio ou inexistente.");

        if (manifest.ZipLength > 0 && zipInfo.Length != manifest.ZipLength)
            throw new InvalidDataException($"Tamanho do pacote .zip ({zipInfo.Length} bytes) diverge do manifesto ({manifest.ZipLength} bytes).");

        var actualZipHash = ComputeFileSha256(zipFilePath);
        if (string.IsNullOrWhiteSpace(manifest.ZipSha256) ||
            !string.Equals(actualZipHash, manifest.ZipSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Hash SHA-256 do pacote .zip ({actualZipHash}) diverge do manifesto ({manifest.ZipSha256}).");
        }

        var fullExtractRoot = Path.GetFullPath(extractDirectory);
        Directory.CreateDirectory(fullExtractRoot);

        using (var archive = ZipFile.OpenRead(zipFilePath))
        {
            foreach (var entry in archive.Entries)
            {
                var normalizedEntry = entry.FullName.Replace('\\', '/');
                foreach (var prohibited in ProhibitedUserDataEntries)
                {
                    if (normalizedEntry.Equals(prohibited, StringComparison.OrdinalIgnoreCase) ||
                        normalizedEntry.EndsWith("/" + prohibited, StringComparison.OrdinalIgnoreCase) ||
                        normalizedEntry.StartsWith(prohibited, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"O pacote contém item de dados de usuário proibido: '{entry.FullName}'.");
                    }
                }

                var destinationPath = Path.GetFullPath(Path.Combine(fullExtractRoot, entry.FullName));
                if (!destinationPath.StartsWith(fullExtractRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Entrada ZIP insegura detectada: '{entry.FullName}'.");

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                entry.ExtractToFile(destinationPath, overwrite: true);
            }
        }

        var conectaExe = Path.Combine(fullExtractRoot, "ConectaTI.exe");
        var hotKeyExe = Path.Combine(fullExtractRoot, "HotKeyText.exe");

        if (!File.Exists(conectaExe))
            throw new InvalidDataException("O pacote não contém ConectaTI.exe.");

        var conectaHash = ComputeFileSha256(conectaExe);
        var expectedConectaHash = !string.IsNullOrWhiteSpace(manifest.Components.ConectaTi.Sha256)
            ? manifest.Components.ConectaTi.Sha256
            : manifest.ExecutableSha256;

        if (string.IsNullOrWhiteSpace(expectedConectaHash) ||
            !string.Equals(conectaHash, expectedConectaHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Hash SHA-256 de ConectaTI.exe ({conectaHash}) diverge do manifesto ({expectedConectaHash}).");
        }

        ValidateWindowsPortableExecutable(conectaExe, manifest.Components.ConectaTi.Version);

        var expectsSeparateHotKeyExe = string.Equals(
            manifest.Components.HotKeyText.ExecutableName,
            "HotKeyText.exe",
            StringComparison.OrdinalIgnoreCase);

        if (expectsSeparateHotKeyExe)
        {
            if (!File.Exists(hotKeyExe))
                throw new InvalidDataException("O manifesto declara HotKeyText.exe separado, mas o pacote não o contém.");

            var hotKeyHash = ComputeFileSha256(hotKeyExe);
            var expectedHotKeyHash = manifest.Components.HotKeyText.Sha256;
            if (string.IsNullOrWhiteSpace(expectedHotKeyHash) ||
                !string.Equals(hotKeyHash, expectedHotKeyHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Hash SHA-256 de HotKeyText.exe ({hotKeyHash}) diverge do manifesto ({expectedHotKeyHash}).");
            }

            ValidateWindowsPortableExecutable(hotKeyExe, manifest.Components.HotKeyText.Version);
        }
        else if (File.Exists(hotKeyExe))
        {
            throw new InvalidDataException("Pacote de executável único (ConectaTI.exe) não deve conter HotKeyText.exe separado.");
        }
    }

    public static void ValidateWindowsPortableExecutable(string exePath, string expectedVersion)
    {
        using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (fs.Length < 256)
                throw new InvalidDataException($"O executável '{Path.GetFileName(exePath)}' é pequeno demais para um binário PE válido.");

            Span<byte> header = stackalloc byte[64];
            if (fs.Read(header) < 64 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                throw new InvalidDataException($"O arquivo '{Path.GetFileName(exePath)}' não possui assinatura MZ válida.");

            var peOffset = BitConverter.ToInt32(header.Slice(60, 4));
            if (peOffset < 64 || peOffset + 4 > fs.Length)
                throw new InvalidDataException($"Cabeçalho PE inválido em '{Path.GetFileName(exePath)}'.");

            fs.Position = peOffset;
            Span<byte> peSig = stackalloc byte[4];
            if (fs.Read(peSig) < 4 || peSig[0] != (byte)'P' || peSig[1] != (byte)'E' || peSig[2] != 0 || peSig[3] != 0)
                throw new InvalidDataException($"Assinatura PE ausente em '{Path.GetFileName(exePath)}'.");
        }

        if (!string.IsNullOrWhiteSpace(expectedVersion))
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var rawVer = info.ProductVersion ?? info.FileVersion;
            if (string.IsNullOrWhiteSpace(rawVer))
            {
                var companionDll = Path.ChangeExtension(exePath, ".dll");
                if (File.Exists(companionDll))
                {
                    var dllInfo = FileVersionInfo.GetVersionInfo(companionDll);
                    rawVer = dllInfo.ProductVersion ?? dllInfo.FileVersion;
                }
            }

            if (!string.IsNullOrWhiteSpace(rawVer))
            {
                var fileVer = NormalizeVersionString(rawVer);
                var normExpected = NormalizeVersionString(expectedVersion);
                if (fileVer != "0.0.0" && CompareVersions(fileVer, normExpected) != 0)
                {
                    throw new InvalidDataException(
                        $"A versão do binário '{Path.GetFileName(exePath)}' ({fileVer}) diverge da versão declarada no manifesto ({normExpected}).");
                }
            }
        }
    }

    public static void CreateRollbackBackup(string installDirectory)
    {
        var fullTargetDir = Path.GetFullPath(installDirectory);
        var rollbackDir = GetRollbackDirectory(fullTargetDir);
        Directory.CreateDirectory(rollbackDir);

        var conectaSrc = Path.Combine(fullTargetDir, "ConectaTI.exe");
        var hotKeySrc = Path.Combine(fullTargetDir, "HotKeyText.exe");
        var manifestSrc = Path.Combine(fullTargetDir, "release-manifest.json");

        if (!File.Exists(conectaSrc))
            throw new FileNotFoundException("ConectaTI.exe não encontrado na pasta de instalação para criar backup de recuperação.", conectaSrc);

        var conectaDst = Path.Combine(rollbackDir, "ConectaTI.exe");
        File.Copy(conectaSrc, conectaDst, overwrite: true);

        var hotKeyDst = Path.Combine(rollbackDir, "HotKeyText.exe");
        var hotKeyHash = string.Empty;
        var hadStandaloneHotKey = File.Exists(hotKeySrc);
        if (hadStandaloneHotKey)
        {
            File.Copy(hotKeySrc, hotKeyDst, overwrite: true);
            hotKeyHash = ComputeFileSha256(hotKeyDst);
        }
        else if (File.Exists(hotKeyDst))
        {
            try { File.Delete(hotKeyDst); } catch { }
        }

        if (File.Exists(manifestSrc))
        {
            File.Copy(manifestSrc, Path.Combine(rollbackDir, "release-manifest.json"), overwrite: true);
        }

        var meta = new RollbackMetadata
        {
            BackedUpAtUtc = DateTime.UtcNow.ToString("o"),
            PreviousVersion = GetInstalledVersion(fullTargetDir),
            PreviousHotKeyTextVersion = GetInstalledHotKeyTextVersion(fullTargetDir),
            ConectaTiSha256 = ComputeFileSha256(conectaDst),
            HotKeyTextSha256 = hotKeyHash,
            HadStandaloneHotKeyTextExecutable = hadStandaloneHotKey
        };

        File.WriteAllText(
            GetRollbackMetadataPath(fullTargetDir),
            JsonSerializer.Serialize(meta, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static bool RestorePreviousInstallation(
        string installDirectory,
        string hotKeyTextInstancePrefix,
        out string? errorMessage)
    {
        errorMessage = null;
        var fullTargetDir = Path.GetFullPath(installDirectory);
        var rollbackDir = GetRollbackDirectory(fullTargetDir);
        var metaPath = GetRollbackMetadataPath(fullTargetDir);

        if (!File.Exists(metaPath))
        {
            errorMessage = "Não há metadados de ponto de recuperação disponíveis.";
            return false;
        }

        try
        {
            var meta = JsonSerializer.Deserialize<RollbackMetadata>(File.ReadAllText(metaPath), JsonOptions)
                ?? throw new InvalidDataException("Metadados de rollback corrompidos.");

            HotKeyTextService.StopRunningInstancesInDirectory(
                fullTargetDir,
                out _,
                instancePrefix: hotKeyTextInstancePrefix,
                timeoutMs: 4000);

            var backupConecta = Path.Combine(rollbackDir, "ConectaTI.exe");
            var backupHotKey = Path.Combine(rollbackDir, "HotKeyText.exe");
            var backupManifest = Path.Combine(rollbackDir, "release-manifest.json");

            if (!File.Exists(backupConecta))
                throw new FileNotFoundException("Backup de ConectaTI.exe ausente.", backupConecta);

            var targetConecta = Path.Combine(fullTargetDir, "ConectaTI.exe");
            var targetHotKey = Path.Combine(fullTargetDir, "HotKeyText.exe");
            ReplaceExecutableSafely(backupConecta, targetConecta, meta.ConectaTiSha256);

            if ((meta.HadStandaloneHotKeyTextExecutable || !string.IsNullOrWhiteSpace(meta.HotKeyTextSha256)) && File.Exists(backupHotKey))
            {
                ReplaceExecutableSafely(backupHotKey, targetHotKey, meta.HotKeyTextSha256);
            }
            else if (!meta.HadStandaloneHotKeyTextExecutable && string.IsNullOrWhiteSpace(meta.HotKeyTextSha256) && File.Exists(targetHotKey))
            {
                try { File.Delete(targetHotKey); } catch { }
            }

            if (File.Exists(backupManifest))
            {
                File.Copy(backupManifest, Path.Combine(fullTargetDir, "release-manifest.json"), overwrite: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static void InstallStagedFiles(
        string stagingExtractDir,
        ReleasePackageManifest manifest,
        string fullTargetDir,
        Action<string>? faultInjectionAfterConectaCopy)
    {
        var stagedConecta = Path.Combine(stagingExtractDir, "ConectaTI.exe");
        var stagedHotKey = Path.Combine(stagingExtractDir, "HotKeyText.exe");
        var stagedManifest = Path.Combine(stagingExtractDir, "release-manifest.json");

        var targetConecta = Path.Combine(fullTargetDir, "ConectaTI.exe");
        var targetHotKey = Path.Combine(fullTargetDir, "HotKeyText.exe");
        var targetManifest = Path.Combine(fullTargetDir, "release-manifest.json");

        var expectedConectaHash = !string.IsNullOrWhiteSpace(manifest.Components?.ConectaTi?.Sha256)
            ? manifest.Components!.ConectaTi!.Sha256
            : manifest.ExecutableSha256;

        ReplaceExecutableSafely(stagedConecta, targetConecta, expectedConectaHash);

        // Permite simular falha no meio da atualização durante testes automatizados para validar reversão transacional
        faultInjectionAfterConectaCopy?.Invoke(fullTargetDir);

        var expectsSeparateHotKeyExe = string.Equals(
            manifest.Components?.HotKeyText?.ExecutableName,
            "HotKeyText.exe",
            StringComparison.OrdinalIgnoreCase);

        if (expectsSeparateHotKeyExe && File.Exists(stagedHotKey))
        {
            var expectedHotKeyHash = manifest.Components!.HotKeyText!.Sha256;
            ReplaceExecutableSafely(stagedHotKey, targetHotKey, expectedHotKeyHash);
        }
        else if (File.Exists(targetHotKey))
        {
            // Migração de entrega anterior (2 executáveis) para a entrega atual (executável único ConectaTI.exe):
            // remove o HotKeyText.exe avulso antigo da raiz de instalação (mantendo-o apenas no ponto de rollback).
            File.Delete(targetHotKey);
        }

        if (File.Exists(stagedManifest))
        {
            File.Copy(stagedManifest, targetManifest, overwrite: true);
        }
        else
        {
            File.WriteAllText(
                targetManifest,
                JsonSerializer.Serialize(manifest, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static void ReplaceExecutableSafely(string sourcePath, string targetPath, string expectedSha256)
    {
        var runningOldPath = targetPath + ".running-old";
        try
        {
            if (File.Exists(runningOldPath))
            {
                try { File.Delete(runningOldPath); } catch { }
            }

            try
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                // No Windows, um executável em execução pode ser renomeado na mesma partição mesmo com a imagem carregada
                File.Move(targetPath, runningOldPath, overwrite: true);
                File.Copy(sourcePath, targetPath, overwrite: true);
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var installedHash = ComputeFileSha256(targetPath);
                if (!string.Equals(installedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        $"Verificação pós-cópia falhou para '{Path.GetFileName(targetPath)}': hash {installedHash} != {expectedSha256}.");
                }
            }
        }
        finally
        {
            if (File.Exists(runningOldPath))
            {
                try { File.Delete(runningOldPath); } catch { }
            }
        }
    }

    private static void TryStartUpdatedHotKeyTextInTray(string installDirectory, string instancePrefix)
    {
        try
        {
            var hotKeyExe = Path.Combine(installDirectory, "HotKeyText.exe");
            if (!File.Exists(hotKeyExe))
                return;

            var psi = new ProcessStartInfo
            {
                FileName = hotKeyExe,
                WorkingDirectory = installDirectory,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("/bandeja");
            if (!string.IsNullOrWhiteSpace(instancePrefix))
            {
                psi.ArgumentList.Add("--instance-prefix");
                psi.ArgumentList.Add(instancePrefix);
            }

            Process.Start(psi);
        }
        catch { }
    }

    private async Task<ReleasePackageManifest?> FetchManifestAsync(string url, CancellationToken cancellationToken)
    {
        if (_allowLocalFileUrls && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var text = await File.ReadAllTextAsync(uri.LocalPath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ReleasePackageManifest>(text, JsonOptions);
        }

        using var resp = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ReleasePackageManifest>(json, JsonOptions);
    }

    private async Task DownloadUrlToFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        if (_allowLocalFileUrls && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            File.Copy(uri.LocalPath, destinationPath, overwrite: true);
            return;
        }

        using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var netStream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await netStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
    }

    private bool IsAuthenticReleaseEndpoint(string url)
    {
        if (_allowLocalFileUrls && Uri.TryCreate(url, UriKind.Absolute, out var fileUri) && fileUri.IsFile)
            return true;

        return string.Equals(url, OfficialReleasesLatestApiUrl, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAuthenticDownloadUrl(string url)
    {
        if (_allowLocalFileUrls && Uri.TryCreate(url, UriKind.Absolute, out var fileUri) && fileUri.IsFile)
            return true;

        return IsOfficialGitHubPath(url, "github.com", $"/{OfficialGitHubRepo}/releases/download/");
    }

    private static bool IsOfficialGitHubPath(string url, string host, string pathPrefix)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
           string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase) &&
           uri.AbsolutePath.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase);

    public static string ComputeFileSha256(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string NormalizeVersionString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "0.0.0";

        var v = raw.Trim();
        if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            v = v[1..].Trim();

        var plusIdx = v.IndexOf('+');
        if (plusIdx >= 0)
            v = v[..plusIdx];

        var dashIdx = v.IndexOf('-');
        if (dashIdx >= 0)
            v = v[..dashIdx];

        return v.Trim();
    }

    public static int CompareVersions(string left, string right)
    {
        var lNorm = NormalizeVersionString(left);
        var rNorm = NormalizeVersionString(right);

        if (Version.TryParse(PadVersion(lNorm), out var vLeft) &&
            Version.TryParse(PadVersion(rNorm), out var vRight))
        {
            return vLeft.CompareTo(vRight);
        }

        return string.Compare(lNorm, rNorm, StringComparison.OrdinalIgnoreCase);
    }

    private static string PadVersion(string v)
    {
        var parts = v.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0.0",
            2 => $"{parts[0]}.{parts[1]}.0.0",
            3 => $"{parts[0]}.{parts[1]}.{parts[2]}.0",
            _ => $"{parts[0]}.{parts[1]}.{parts[2]}.{parts[3]}"
        };
    }
}
