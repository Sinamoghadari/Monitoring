# deploy-update.ps1
# Publish a single-file agent, SHA256 it, SCP to the update share, then PATCH
# only the Update block of the single live app_configuration row.
#
# Secrets: set ERGONOMY_SCP_PASSWORD (and optional ERGONOMY_SETTINGS_API_KEY).
# Do not commit passwords.

[CmdletBinding()]
param (
    [Parameter(Mandatory = $true, HelpMessage = "Semver, e.g. 1.0.1")]
    [ValidatePattern('^\d+\.\d+\.\d+([.-].*)?$')]
    [string]$Version,

    [string]$ProjectPath = "E:\Sisco_Projects\HSE\Ergonomy\Demo\Ergonomy_V2\Ergonomy.csproj",
    [string]$BaseOutputDir = "E:\Sisco_Projects\HSE\Ergonomy\Demo",
    [string]$ServerIP = "172.17.214.38",
    [string]$ServerUser = "sisco",
    [string]$ServerPassword = $(if ($env:ERGONOMY_SCP_PASSWORD) { $env:ERGONOMY_SCP_PASSWORD } else { "" }),
    [string]$ServerRemotePath = "/home/sisco/HSE_Monitoring/Updates",
    [string]$FastApiUrl = "https://siscoeye.sirjansteel.com/api/settings",
    [string]$DownloadUrlBase = "https://siscoeye.sirjansteel.com/api/updates",
    [int]$CheckIntervalMinutes = 1,
    [switch]$SkipPublish,
    [switch]$SkipUpload
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
$ProgressPreference = "SilentlyContinue"

function Assert-LastExit([string]$Step) {
    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE"
    }
}

function Invoke-JsonRequest {
    param (
        [string]$Uri,
        [string]$Method,
        [hashtable]$Headers,
        [string]$Body,
        [int]$TimeoutSec = 60
    )
    $params = @{
        Uri             = $Uri
        Method          = $Method
        Headers         = $Headers
        TimeoutSec      = $TimeoutSec
        UseBasicParsing = $true
    }
    if ($Body) {
        $params.Body = [System.Text.Encoding]::UTF8.GetBytes($Body)
        $params.ContentType = "application/json; charset=utf-8"
    }
    return Invoke-RestMethod @params
}

# -----------------------------------------------------------------------------
$ProjectItem = Get-Item -LiteralPath $ProjectPath
$ProjectDir  = if ($ProjectItem.PSIsContainer) { $ProjectItem.FullName } else { $ProjectItem.DirectoryName }
$CsprojFile  = if ($ProjectItem.PSIsContainer) { Join-Path $ProjectPath "Ergonomy.csproj" } else { $ProjectItem.FullName }

$ReleaseName = "Ergonomy_V-$Version"
$OutDir      = Join-Path $BaseOutputDir $ReleaseName
$ZipFile     = Join-Path $BaseOutputDir "$ReleaseName.zip"
$HashFile    = Join-Path $BaseOutputDir "$ReleaseName.sha256"
$DownloadUrl = "$DownloadUrlBase/$ReleaseName.zip"
$MergeUrl    = if ($FastApiUrl.TrimEnd("/").EndsWith("/settings")) {
    ($FastApiUrl.TrimEnd("/") + "/merge")
} else {
    ($FastApiUrl.TrimEnd("/") + "/settings/merge")
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Project File:    $CsprojFile"
Write-Host "Target Version:  $Version"
Write-Host "Publish Folder:  $OutDir"
Write-Host "Artifact ZIP:    $ZipFile"
Write-Host "Download URL:    $DownloadUrl"
Write-Host "Merge API:       $MergeUrl"
Write-Host "==========================================" -ForegroundColor Cyan

# -----------------------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Host "`n>>> [1/4] Publishing Project..." -ForegroundColor Yellow
    foreach ($path in @("$ProjectDir\bin", "$ProjectDir\obj", $OutDir)) {
        if (Test-Path $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }

    & dotnet publish $CsprojFile `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:Version=$Version `
        -p:AssemblyVersion="$Version.0" `
        -p:FileVersion="$Version.0" `
        -p:InformationalVersion=$Version `
        -p:IncludeSourceRevisionInInformationalVersion=false `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -o $OutDir
    Assert-LastExit "dotnet publish"

    $publishedExe = Get-ChildItem -LiteralPath $OutDir -Filter "Ergonomy*.exe" -File | Select-Object -First 1
    if (-not $publishedExe) { throw "Publish output has no Ergonomy*.exe in $OutDir" }
}

# -----------------------------------------------------------------------------
Write-Host "`n>>> [2/4] Creating ZIP Package & Calculating SHA256..." -ForegroundColor Yellow
if (-not (Test-Path $OutDir)) { throw "Publish folder missing: $OutDir" }
if (Test-Path $ZipFile) { Remove-Item -LiteralPath $ZipFile -Force }
Compress-Archive -Path (Join-Path $OutDir "*") -DestinationPath $ZipFile -Force
if (-not (Test-Path $ZipFile) -or ((Get-Item $ZipFile).Length -lt 1024)) {
    throw "ZIP is missing or too small: $ZipFile"
}

$Hash = (Get-FileHash -LiteralPath $ZipFile -Algorithm SHA256).Hash.ToUpperInvariant()
if ($Hash -notmatch '^[0-9A-F]{64}$') { throw "Invalid SHA256: $Hash" }
$verify = (Get-FileHash -LiteralPath $ZipFile -Algorithm SHA256).Hash.ToUpperInvariant()
if ($verify -ne $Hash) { throw "SHA256 re-hash mismatch" }
$Hash | Set-Content -LiteralPath $HashFile -Encoding ascii
Write-Host "Artifact Created: $ZipFile" -ForegroundColor Green
Write-Host "SHA256 Checksum:  $Hash" -ForegroundColor Green

# -----------------------------------------------------------------------------
if (-not $SkipUpload) {
    Write-Host "`n>>> [3/4] Uploading ZIP to Linux Host via SCP..." -ForegroundColor Yellow
    if ([string]::IsNullOrWhiteSpace($ServerPassword) -and (Get-Command pscp -ErrorAction SilentlyContinue)) {
        throw "Set ERGONOMY_SCP_PASSWORD or pass -ServerPassword. Password is not stored in the script."
    }

    if (Get-Command pscp -ErrorAction SilentlyContinue) {
        & pscp -batch -pw $ServerPassword $ZipFile "${ServerUser}@${ServerIP}:${ServerRemotePath}/"
        Assert-LastExit "pscp"
    }
    else {
        & scp $ZipFile "${ServerUser}@${ServerIP}:${ServerRemotePath}/"
        Assert-LastExit "scp"
    }
    Write-Host "SCP Upload Completed." -ForegroundColor Green
}

# -----------------------------------------------------------------------------
Write-Host "`n>>> [4/4] Merging Update block into the live settings row..." -ForegroundColor Yellow
$Headers = @{
    "Accept" = "application/json"
}
if ($env:ERGONOMY_SETTINGS_API_KEY) {
    $Headers["X-Api-Key"] = $env:ERGONOMY_SETTINGS_API_KEY
}

$current = $null
try {
    $current = Invoke-JsonRequest -Uri $FastApiUrl -Method GET -Headers $Headers
}
catch {
    Write-Host "GET $FastApiUrl failed (continuing to merge): $($_.Exception.Message)" -ForegroundColor Yellow
}

$currentUpdate = $null
if ($current -and $current.Update) { $currentUpdate = $current.Update }
$alreadySame = $currentUpdate -and
    [string]$currentUpdate.LatestVersion -eq $Version -and
    [string]$currentUpdate.Sha256 -and
    [string]$currentUpdate.Sha256.ToUpperInvariant() -eq $Hash -and
    [string]$currentUpdate.DownloadUrl -eq $DownloadUrl

if ($alreadySame) {
    Write-Host "Idempotent skip: live Update already matches $Version / $Hash" -ForegroundColor Green
}
else {
    # Nested merge only. Kafka, API, alarm intervals, EnabledMetrics stay on the server.
    $Patch = @{
        Update = @{
            Sha256               = $Hash
            Enabled              = $true
            DownloadUrl          = $DownloadUrl
            LatestVersion        = $Version
            CheckIntervalMinutes = $CheckIntervalMinutes
        }
    }
    $JsonBody = $Patch | ConvertTo-Json -Depth 6 -Compress

    try {
        $Response = Invoke-JsonRequest -Uri $MergeUrl -Method POST -Headers $Headers -Body $JsonBody
        Write-Host "Live row merged. row_version=$($Response.row_version)" -ForegroundColor Green
        if ($Response.settings -and $Response.settings.Update) {
            Write-Host ("Update.LatestVersion=" + $Response.settings.Update.LatestVersion + " Sha256=" + $Response.settings.Update.Sha256) -ForegroundColor Green
        }
    }
    catch {
        Write-Host "Merge API failed. Deploy FastAPI PATCH/merge first; POST /api/settings no longer inserts rows but a full POST would still overwrite Kafka/alarms." -ForegroundColor Red
        Write-Host "Detail: $($_.Exception.Message)" -ForegroundColor Red
        if ($_.ErrorDetails.Message) { Write-Host "Server Response: $($_.ErrorDetails.Message)" -ForegroundColor Red }
        exit 1
    }
}

Write-Host "`nPipeline completed for version $Version." -ForegroundColor Green
Write-Host "Agents pick this up on the next SettingsCheckIntervalSeconds tick." -ForegroundColor Cyan
