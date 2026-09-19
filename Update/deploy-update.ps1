# deploy-update.ps1
param (
    [Parameter(Mandatory = $true, HelpMessage = "Enter Version (e.g. 1.0.1)")]
    [string]$Version,

    [string]$ProjectPath = "E:\Sisco_Projects\HSE\Ergonomy\Demo\Ergonomy_V2\Ergonomy.csproj",
    [string]$BaseOutputDir = "E:\Sisco_Projects\HSE\Ergonomy\Demo",
    [string]$ServerIP = "172.17.214.38",
    [string]$ServerUser = "sisco",
    [string]$ServerPassword = "Aa@123456",
    [string]$ServerRemotePath = "/home/sisco/HSE_Monitoring/Updates",

    # تغییر: اندپوینت patch/update
    [string]$FastApiUrl = "https://siscoeye.sirjansteel.com/api/settings/update"
)

$ErrorActionPreference = "Stop"

# رفع خطای EOF و قطعی سوکت در ارتباطات امن Nginx (TLS 1.2 / 1.3)
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
$ProgressPreference = 'SilentlyContinue'

# ==========================================
# ۱. ساختاربندی مسیرها و متغیرها
# ==========================================
$ProjectDir  = if ((Get-Item $ProjectPath).PSIsContainer) { $ProjectPath } else { [System.IO.Path]::GetDirectoryName($ProjectPath) }
$CsprojFile  = if ((Get-Item $ProjectPath).PSIsContainer) { Join-Path $ProjectPath "Ergonomy.csproj" } else { $ProjectPath }

$ReleaseName = "Ergonomy_V-$Version"
$OutDir      = Join-Path $BaseOutputDir $ReleaseName
$ZipFile     = Join-Path $BaseOutputDir "$ReleaseName.zip"
$HashFile    = Join-Path $BaseOutputDir "$ReleaseName.sha256"
$DownloadUrl = "https://siscoeye.sirjansteel.com/api/updates/$ReleaseName.zip"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Project File:    $CsprojFile" -ForegroundColor Cyan
Write-Host "Target Version:  $Version" -ForegroundColor Cyan
Write-Host "Publish Folder:  $OutDir" -ForegroundColor Cyan
Write-Host "Artifact ZIP:    $ZipFile" -ForegroundColor Cyan
Write-Host "Target API:      $FastApiUrl" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# ==========================================
# ۲. پاک‌سازی کش و بیلد Single-File
# ==========================================
Write-Host "`n>>> [1/4] Publishing Project..." -ForegroundColor Yellow

if (Test-Path "$ProjectDir\bin") { Remove-Item -Path "$ProjectDir\bin" -Recurse -Force }
if (Test-Path "$ProjectDir\obj") { Remove-Item -Path "$ProjectDir\obj" -Recurse -Force }
if (Test-Path $OutDir)           { Remove-Item -Path $OutDir -Recurse -Force }

dotnet publish $CsprojFile `
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

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed!"
    exit 1
}

# ==========================================
# ۳. فشرده‌سازی خروجی به ZIP و محاسبه SHA-256
# ==========================================
Write-Host "`n>>> [2/4] Creating ZIP Package & Calculating SHA256..." -ForegroundColor Yellow

if (Test-Path $ZipFile) { Remove-Item -Path $ZipFile -Force }
Compress-Archive -Path "$OutDir\*" -DestinationPath $ZipFile -Force

$Hash = (Get-FileHash -Path $ZipFile -Algorithm SHA256).Hash.ToUpper()
$Hash | Out-File -FilePath $HashFile -Encoding ascii

Write-Host "Artifact Created: $ZipFile" -ForegroundColor Green
Write-Host "SHA256 Checksum:  $Hash" -ForegroundColor Green

# ==========================================
# ۴. انتقال فایل به سرور لینوکس مقصد (SCP)
# ==========================================
Write-Host "`n>>> [3/4] Uploading ZIP to Linux Host via SCP..." -ForegroundColor Yellow

if (Get-Command pscp -ErrorAction SilentlyContinue) {
    pscp -pw $ServerPassword $ZipFile "${ServerUser}@${ServerIP}:${ServerRemotePath}/"
} else {
    scp $ZipFile "${ServerUser}@${ServerIP}:${ServerRemotePath}/"
}

if ($LASTEXITCODE -ne 0) {
    Write-Error "SCP Transfer failed!"
    exit 1
}
Write-Host "SCP Upload Completed." -ForegroundColor Green

# ==========================================
# ۵. بروزرسانی فقط بلوک Update در FastAPI / PostgreSQL
# ==========================================
Write-Host "`n>>> [4/4] Patching Update Block via API..." -ForegroundColor Yellow

$UpdatePayload = @{
    Update = @{
        Sha256               = $Hash
        Enabled              = $true
        DownloadUrl          = $DownloadUrl
        LatestVersion        = $Version
        CheckIntervalMinutes = 1
    }
}

$JsonBody = $UpdatePayload | ConvertTo-Json -Depth 5

$Headers = @{
    "Accept"       = "application/json"
    "Content-Type" = "application/json; charset=utf-8"
}

try {
    $Response = Invoke-RestMethod -Uri $FastApiUrl `
        -Method Post `
        -Headers $Headers `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($JsonBody)) `
        -TimeoutSec 60

    Write-Host "Update block successfully patched: $($Response | ConvertTo-Json -Compress)" -ForegroundColor Green
}
catch {
    Write-Host "API Request Failed!" -ForegroundColor Red
    Write-Host "Detail: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.ErrorDetails.Message) {
        Write-Host "Server Response: $($_.ErrorDetails.Message)" -ForegroundColor Red
    }
    exit 1
}

Write-Host "`nPipeline completed successfully for version $Version!" -ForegroundColor Green
