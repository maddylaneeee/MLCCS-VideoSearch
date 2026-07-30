[CmdletBinding()]
param([switch]$SkipVisualStudio)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) { throw 'This bootstrap must run on Windows 10/11.' }
if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) { throw 'App Installer/winget is required. Install all Windows updates and App Installer, then retry.' }

winget install --id Microsoft.DotNet.SDK.10 --exact --silent --accept-package-agreements --accept-source-agreements
if (-not $SkipVisualStudio) {
  winget install --id Microsoft.VisualStudio.2022.BuildTools --exact --silent --accept-package-agreements --accept-source-agreements --override '--wait --quiet --norestart --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --add Microsoft.VisualStudio.Component.Windows11SDK.26100 --includeRecommended'
}

$dotnet = Get-Command dotnet.exe -ErrorAction Stop
& $dotnet.Source --info
Write-Host 'Bootstrap complete. Open a new PowerShell window before Build-Windows.ps1.'

