# Build installer for TS3ScreenShare
# Requirements: Inno Setup installed at default location

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

Write-Host "==> Publishing app..." -ForegroundColor Cyan
dotnet publish "$root\TS3ScreenShare.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o "$root\publish" `
    --nologo

if ($LASTEXITCODE -ne 0) { Write-Error "Publish failed"; exit 1 }

Write-Host "==> Building installer..." -ForegroundColor Cyan
$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    Write-Error "Inno Setup not found at $iscc. Download from https://jrsoftware.org/isdl.php"
    exit 1
}

& $iscc "$PSScriptRoot\setup.iss"
if ($LASTEXITCODE -ne 0) { Write-Error "Inno Setup failed"; exit 1 }

Write-Host "==> Done! Installer is in installer\output\" -ForegroundColor Green
