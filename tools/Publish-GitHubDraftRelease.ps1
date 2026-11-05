param(
    [string]$PackageDir = "dist",
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-fA-F0-9]{40}$')]
    [string]$TargetCommitish,
    [Parameter(Mandatory)]
    [string]$NotesPath,
    [string]$GitHubToken = $env:GITHUB_TOKEN
)
$ErrorActionPreference = "Stop"
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$resolvedPkgDir = if ([IO.Path]::IsPathRooted($PackageDir)) { $PackageDir } else { Join-Path $projectRoot $PackageDir }

$repository = "MaxBSilva/ConectaTI"
$manifestPath = Join-Path $resolvedPkgDir "release-manifest.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$releaseVersion = $manifest.version
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$' -or
    $manifest.suiteVersion -ne $releaseVersion -or
    $manifest.components.conectaTi.version -ne $releaseVersion -or
    $manifest.components.hotKeyText.version -ne $releaseVersion -or
    $manifest.updateOrigin -ne $repository -or
    $manifest.zipName -ne "ConectaTI-$releaseVersion-win-x64.zip") {
    throw "Manifesto incompatível com a publicação de $repository."
}
$tagName = "v$releaseVersion"
$zipPath = Join-Path $resolvedPkgDir $manifest.zipName

foreach ($f in @($zipPath, $manifestPath, $notesPath)) {
    if (-not (Test-Path -LiteralPath $f)) {
        throw "Arquivo obrigatório para a Release em rascunho não encontrado: $f"
    }
}

if ([string]::IsNullOrWhiteSpace($GitHubToken)) {
    throw "Informe -GitHubToken ou defina a variável de ambiente GITHUB_TOKEN com permissão 'Contents: Read and write' em $Repository."
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$notes = [IO.File]::ReadAllText($notesPath, $utf8)

$headers = @{
    "Authorization"        = "Bearer $GitHubToken"
    "Accept"               = "application/vnd.github+json"
    "X-GitHub-Api-Version" = "2022-11-28"
    "User-Agent"           = "ConectaTI-ReleasePublisher/$releaseVersion"
}

$bodyObj = [ordered]@{
    tag_name         = $TagName
    target_commitish = $TargetCommitish
    name             = "ConectaTI $tagName"
    body             = $notes
    draft            = $true
    prerelease       = $false
}
$bodyJson = $utf8.GetBytes(($bodyObj | ConvertTo-Json -Depth 5))

$releaseResp = Invoke-RestMethod -Method Post `
    -Uri "https://api.github.com/repos/$Repository/releases" `
    -Headers $headers `
    -ContentType "application/json; charset=utf-8" `
    -Body $bodyJson

$uploadBaseUrl = ($releaseResp.upload_url -replace '\{\?name,label\}$', '')

foreach ($assetPath in @($zipPath, $manifestPath)) {
    $fileName = [IO.Path]::GetFileName($assetPath)
    $contentType = if ($fileName.EndsWith(".zip")) { "application/zip" } else { "application/json" }
    $uploadUri = "${uploadBaseUrl}?name=$([Uri]::EscapeDataString($fileName))"
    $asset = Invoke-RestMethod -Method Post `
        -Uri $uploadUri `
        -Headers $headers `
        -ContentType $contentType `
        -InFile $assetPath
    if ($asset.state -ne "uploaded" -or $asset.size -ne (Get-Item -LiteralPath $assetPath).Length) {
        throw "Upload incompleto do anexo $fileName."
    }
    Write-Output "Anexado ativo na Release em rascunho: $fileName"
}

Write-Output "Release em rascunho criada com sucesso para revisão: $($releaseResp.html_url)"
