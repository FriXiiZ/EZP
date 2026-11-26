# Compile EZP pour Windows (EZP.exe autonome, un seul fichier).
# Prérequis : SDK .NET 8 ou plus récent.  Usage : .\build.ps1 [-SkipTests]
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not $SkipTests) {
    dotnet run --project tests/EFP.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Des tests ont échoué." }
}
dotnet publish src/EFP.Desktop -c Release -r win-x64 -o out/windows
if ($LASTEXITCODE -ne 0) { throw "La compilation a échoué." }
Write-Host "`nPrêt : out\windows\EZP.exe" -ForegroundColor Green
