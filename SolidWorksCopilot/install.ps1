# install.ps1 — Build and register the AI Bob SolidWorks add-in.
# Run once as Administrator after cloning the repo.
#
# What this does:
#   1. Builds the Release DLL
#   2. Registers it as a COM server (regasm)
#   3. Writes the SolidWorks add-in registry key so SW loads it on startup

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ProjectDir  = $PSScriptRoot
$AddinGuid   = "8F4A2E1D-3C7B-4F9A-8E5D-2A6B1C3D4E5F"
$DllName     = "SolidWorksCopilot.dll"

Write-Host "=== AI Bob — SolidWorks Add-In Installer ===" -ForegroundColor Cyan

# ── 1. Build ──────────────────────────────────────────────────────────────────
Write-Host "`nBuilding $Configuration..." -ForegroundColor Yellow
dotnet build "$ProjectDir\SolidWorksCopilot.csproj" -c $Configuration -r win-x64 --no-self-contained
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$DllPath = Join-Path $ProjectDir "bin\$Configuration\net8.0-windows\win-x64\$DllName"
if (-not (Test-Path $DllPath)) {
    # Fallback path without RID
    $DllPath = Join-Path $ProjectDir "bin\$Configuration\net8.0-windows\$DllName"
}
if (-not (Test-Path $DllPath)) { throw "Could not find built DLL. Check build output." }
Write-Host "  Built: $DllPath" -ForegroundColor Green

# ── 2. COM registration ───────────────────────────────────────────────────────
Write-Host "`nRegistering COM server..." -ForegroundColor Yellow
$RegAsm = (Get-ChildItem "C:\Windows\Microsoft.NET\Framework64" -Filter "regasm.exe" -Recurse |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
if (-not $RegAsm) { throw "regasm.exe not found under Framework64." }

& $RegAsm $DllPath /codebase /tlb
if ($LASTEXITCODE -ne 0) { throw "regasm failed." }
Write-Host "  COM registered." -ForegroundColor Green

# ── 3. SolidWorks add-in registry key ────────────────────────────────────────
Write-Host "`nWriting SolidWorks add-in registry key..." -ForegroundColor Yellow
$SwKey = "HKCU:\SOFTWARE\SolidWorks\AddIns\{$AddinGuid}"
New-Item -Path $SwKey -Force | Out-Null
Set-ItemProperty -Path $SwKey -Name "(Default)"    -Value 1
Set-ItemProperty -Path $SwKey -Name "Description"  -Value "AI Bob — Natural Language CAD Copilot"
Set-ItemProperty -Path $SwKey -Name "Title"        -Value "AI Bob"
Write-Host "  Registry key: $SwKey" -ForegroundColor Green

# ── 4. API key reminder ───────────────────────────────────────────────────────
$ConfigFile = Join-Path $env:APPDATA "AiBob\config.txt"
if (-not (Test-Path $ConfigFile)) {
    Write-Host "`n[ACTION REQUIRED] No API key found." -ForegroundColor Red
    Write-Host "Create the file and paste your Anthropic key (sk-ant-...):"
    Write-Host "  $ConfigFile" -ForegroundColor White
    New-Item -ItemType Directory -Path (Split-Path $ConfigFile) -Force | Out-Null
    "sk-ant-YOUR_KEY_HERE" | Out-File $ConfigFile -Encoding utf8
    Write-Host "  Placeholder file created — replace the contents with your real key."
} else {
    Write-Host "`nAPI key file already exists: $ConfigFile" -ForegroundColor Green
}

Write-Host "`n=== Installation complete ===" -ForegroundColor Cyan
Write-Host "Restart SolidWorks and look for the 'AI Bob' tab in the Task Pane."
