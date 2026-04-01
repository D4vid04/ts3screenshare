# Build installer for TS3ScreenShare
# Requirements: Inno Setup 6, Visual Studio with CMake, .NET SDK

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# ── 1. Publish app ────────────────────────────────────────────────────────────
Write-Host "==> Publishing app..." -ForegroundColor Cyan
& "C:\Program Files\dotnet\dotnet.exe" publish "$root\TS3ScreenShare.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -o "$root\publish" --nologo
if ($LASTEXITCODE -ne 0) { Write-Error "App publish failed"; exit 1 }

# ── 2. Build TS3 plugin ───────────────────────────────────────────────────────
Write-Host "==> Building TS3 plugin..." -ForegroundColor Cyan
$cmake = "E:\Visual_Studio_2026_community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
if (-not (Test-Path $cmake)) {
    # Fallback: search PATH
    $cmake = "cmake"
}
$pluginBuild = "$root\Plugin\build"
if (-not (Test-Path "$pluginBuild\CMakeCache.txt")) {
    New-Item -ItemType Directory -Force $pluginBuild | Out-Null
    & $cmake -S "$root\Plugin" -B $pluginBuild -G "Visual Studio 18 2026" -A x64
    if ($LASTEXITCODE -ne 0) { Write-Error "CMake configure failed"; exit 1 }
}
& $cmake --build $pluginBuild --config Release
if ($LASTEXITCODE -ne 0) { Write-Error "Plugin build failed"; exit 1 }

# ── 3. Convert notification.mp3 → notification.wav ───────────────────────────
Write-Host "==> Converting notification sound..." -ForegroundColor Cyan
$mp3 = "$root\publish\Assets\notification.mp3"
$wav = "$PSScriptRoot\TS3ScreenShareNotification.wav"
if (Test-Path $mp3) {
    $converterSrc = @"
using NAudio.Wave;
using var reader = new MediaFoundationReader(@"$mp3");
WaveFileWriter.CreateWaveFile(@"$wav", reader);
"@
    $tmpDir = Join-Path $env:TEMP "ts3ss_wav_converter"
    New-Item -ItemType Directory -Force $tmpDir | Out-Null
    Set-Content "$tmpDir\convert.csx" $converterSrc

    # Use dotnet-script if available, otherwise skip (use existing WAV)
    $dotnetScript = Get-Command "dotnet-script" -ErrorAction SilentlyContinue
    if ($dotnetScript) {
        & dotnet-script "$tmpDir\convert.csx"
    } elseif (-not (Test-Path $wav)) {
        Write-Warning "dotnet-script not found and no existing WAV — notification sound will be missing"
    } else {
        Write-Host "  Using existing WAV file." -ForegroundColor DarkGray
    }
} else {
    Write-Warning "notification.mp3 not found in publish output"
}

# ── 4. Build installer ────────────────────────────────────────────────────────
Write-Host "==> Building installer..." -ForegroundColor Cyan
$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    Write-Error "Inno Setup 6 not found at '$iscc'. Download from https://jrsoftware.org/isdl.php"
    exit 1
}
& $iscc "$PSScriptRoot\setup.iss"
if ($LASTEXITCODE -ne 0) { Write-Error "Inno Setup failed"; exit 1 }

Write-Host "==> Done! Installer: installer\output\TS3ScreenShare-Setup-v1.0.0.exe" -ForegroundColor Green
