# install.ps1 - Build and register the AI Bob SolidWorks add-in.
# Run once as Administrator after cloning the repo.

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ProjectDir   = $PSScriptRoot
$AddinGuid    = "8F4A2E1D-3C7B-4F9A-8E5D-2A6B1C3D4E5F"
$DllName      = "SolidWorksCopilot.dll"

Write-Host "=== AI Bob - SolidWorks Add-In Installer ===" -ForegroundColor Cyan

# 1. Build (.NET Framework 4.8 class library)
Write-Host ""
Write-Host "Building $Configuration..." -ForegroundColor Yellow
dotnet build "$ProjectDir\SolidWorksCopilot.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$DllPath = Join-Path $ProjectDir "bin\$Configuration\net48\$DllName"
if (-not (Test-Path $DllPath)) {
    throw "Could not find $DllName at $DllPath. Check the build output."
}
Write-Host "  Built: $DllPath" -ForegroundColor Green

# 2. COM registration (regasm — the .NET Framework COM registration tool)
Write-Host ""
Write-Host "Registering COM server (regasm)..." -ForegroundColor Yellow
$RegAsm = (Get-ChildItem "C:\Windows\Microsoft.NET\Framework64" -Filter "regasm.exe" -Recurse |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
if (-not $RegAsm) { throw "regasm.exe not found under Framework64." }
& $RegAsm $DllPath /codebase
if ($LASTEXITCODE -ne 0) { throw "regasm failed (run PowerShell as Administrator)." }
Write-Host "  COM registered." -ForegroundColor Green

# 3. SolidWorks add-in registry key
Write-Host ""
Write-Host "Writing SolidWorks add-in registry keys..." -ForegroundColor Yellow

# (a) Machine-wide add-in registration — this is the list SolidWorks scans.
#     Default DWORD: 1 = load at startup. Title/Description show in the dialog.
$SwKey = "HKLM:\SOFTWARE\SolidWorks\AddIns\{$AddinGuid}"
New-Item -Path $SwKey -Force | Out-Null
New-ItemProperty -Path $SwKey -Name "(Default)"   -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $SwKey -Name "Description" -Value "AI Bob - Natural Language CAD Copilot" -PropertyType String -Force | Out-Null
New-ItemProperty -Path $SwKey -Name "Title"       -Value "AI Bob" -PropertyType String -Force | Out-Null
Write-Host "  $SwKey" -ForegroundColor Green

# (b) Per-user startup flag — 1 = enabled for the current user.
$SwStartup = "HKCU:\SOFTWARE\SolidWorks\AddInsStartup\{$AddinGuid}"
New-Item -Path $SwStartup -Force | Out-Null
New-ItemProperty -Path $SwStartup -Name "(Default)" -Value 1 -PropertyType DWord -Force | Out-Null
Write-Host "  $SwStartup" -ForegroundColor Green

# 4. API key reminder
$ConfigFile = Join-Path $env:APPDATA "AiBob\config.txt"
if (-not (Test-Path $ConfigFile)) {
    Write-Host ""
    Write-Host "[ACTION REQUIRED] No API key found." -ForegroundColor Red
    Write-Host "Create this file and paste your Anthropic key (sk-ant-...):"
    Write-Host "  $ConfigFile" -ForegroundColor White
    New-Item -ItemType Directory -Path (Split-Path $ConfigFile) -Force | Out-Null
    Set-Content -Path $ConfigFile -Value "sk-ant-YOUR_KEY_HERE" -Encoding utf8
    Write-Host "  Placeholder file created - replace the contents with your real key."
} else {
    Write-Host ""
    Write-Host "API key file already exists: $ConfigFile" -ForegroundColor Green
}

Write-Host ""
Write-Host "=== Installation complete ===" -ForegroundColor Cyan
Write-Host "Restart SolidWorks and look for the AI Bob tab in the Task Pane."
