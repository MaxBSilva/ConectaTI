param(
    [string]$Dotnet = "dotnet",
    [string]$OutputRoot = "dist",
    [string]$VersionOverride = "",
    [switch]$SkipTests,
    [switch]$Overwrite
)
$ErrorActionPreference = "Stop"
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
Push-Location -LiteralPath $projectRoot
try {
    if ($Dotnet -eq "dotnet" -and -not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        $localDotnet = Join-Path $env:LOCALAPPDATA "ConectaTI-Development\dotnet\dotnet.exe"
        if (Test-Path -LiteralPath $localDotnet) {
            $Dotnet = $localDotnet
        }
    }

    [xml]$project = Get-Content -LiteralPath "src/ConectaTI.App/ConectaTI.App.csproj"
    $releaseVersion = if ([string]::IsNullOrWhiteSpace($VersionOverride)) {
        $project.Project.PropertyGroup.Version | Select-Object -First 1
    } else {
        $VersionOverride.Trim()
    }
    if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid release version: $releaseVersion" }

    $resolvedOutputRoot = if ([IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot } else { Join-Path $projectRoot $OutputRoot }
    New-Item -ItemType Directory -Path $resolvedOutputRoot -Force | Out-Null

    $packageFolderName = "ConectaTI-$releaseVersion-win-x64"
    $destination = Join-Path $resolvedOutputRoot $packageFolderName
    $zipName = "$packageFolderName.zip"
    $zipPath = Join-Path $resolvedOutputRoot $zipName

    if (Test-Path -LiteralPath $destination) {
        if ($Overwrite) {
            Remove-Item -LiteralPath $destination -Recurse -Force
        } else {
            throw "Release folder already exists ($destination); preserve it or use a new OutputRoot/version."
        }
    }
    if ((Test-Path -LiteralPath $zipPath) -and $Overwrite) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    if (-not $SkipTests) {
        & $Dotnet build ConectaTI.slnx -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "Build failed." }
        & $Dotnet test ConectaTI.slnx -c Release --no-build --logger "trx;LogFileName=release-$releaseVersion.trx" --results-directory artifacts/test-results
        if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    }

    & $Dotnet publish src/ConectaTI.App/ConectaTI.App.csproj -c Release -r win-x64 --self-contained true `
        -p:Version=$releaseVersion `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $destination
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

    $publishedFiles = @(Get-ChildItem -LiteralPath $destination -Recurse -File | Sort-Object Name)
    $expectedNames = @("ConectaTI.exe")
    $actualNames = @($publishedFiles | ForEach-Object { $_.Name })
    if ($publishedFiles.Count -ne 1 -or ($actualNames -join ",") -ne ($expectedNames -join ",")) {
        throw "Unexpected files in publication ($($actualNames -join ', ')); expected exclusively ConectaTI.exe."
    }

    $conectaExe = Get-Item -LiteralPath (Join-Path $destination "ConectaTI.exe")

    if ($conectaExe.VersionInfo.FileVersion -ne "$releaseVersion.0") {
        throw "Published ConectaTI.exe version mismatch: $($conectaExe.VersionInfo.FileVersion) != $releaseVersion.0"
    }

    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path "$destination\ConectaTI.exe" -DestinationPath $zipPath -CompressionLevel Optimal
    $zipItem = Get-Item -LiteralPath $zipPath

    $conectaHash = (Get-FileHash -LiteralPath $conectaExe.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $zipHash = (Get-FileHash -LiteralPath $zipItem.FullName -Algorithm SHA256).Hash.ToLowerInvariant()

    $manifest = [ordered]@{
        application           = "ConectaTI"
        version               = $releaseVersion
        suiteVersion          = $releaseVersion
        updateOrigin          = "MaxBSilva/ConectaTI"
        repositoryUrl         = "https://github.com/MaxBSilva/ConectaTI"
        runtime               = "win-x64"
        framework             = "net10.0-windows"
        selfContained         = $true
        singleFile            = $true
        schemaVersion         = 10
        publishedAtUtc        = [DateTime]::UtcNow.ToString("o")
        executableName        = "ConectaTI.exe"
        executableLength      = $conectaExe.Length
        executableSha256      = $conectaHash
        zipName               = $zipName
        zipLength             = $zipItem.Length
        zipSha256             = $zipHash
        components            = [ordered]@{
            conectaTi  = [ordered]@{
                name             = "ConectaTI"
                version          = $releaseVersion
                executableName   = "ConectaTI.exe"
                length           = $conectaExe.Length
                sha256           = $conectaHash
                sourceRepository = "MaxBSilva/ConectaTI"
                author           = "Max Silva"
                updateOrigin     = "MaxBSilva/ConectaTI"
            }
            hotKeyText = [ordered]@{
                name             = "HotKeyText"
                version          = $releaseVersion
                executableName   = "ConectaTI.exe"
                length           = $conectaExe.Length
                sha256           = $conectaHash
                sourceRepository = "viniciusdaveigaa/HotKeyText"
                sourceCommit     = "7b5791b39e59c96a4203ed3a71ad7e73f8f125ac"
                author           = "Vin$([char]0x00ED)cius da Veiga"
                updateOrigin     = "MaxBSilva/ConectaTI"
            }
        }
        userDataFilesIncluded = @()
    }

    $manifestJson = $manifest | ConvertTo-Json -Depth 6
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $destination "release-manifest.json"), $manifestJson, $utf8NoBom)
    [IO.File]::WriteAllText((Join-Path $resolvedOutputRoot "release-manifest.json"), $manifestJson, $utf8NoBom)

    Write-Output "Published clean release: $destination (Zip: $zipPath)"
} finally {
    Pop-Location
}
