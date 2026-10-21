[CmdletBinding()]
param([string]$Version, [string]$IsccPath, [switch]$PublishOnly)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = (Get-Content (Join-Path $PSScriptRoot 'version.txt') -Raw).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid release version: $Version" }
$publishDir = Join-Path $repoRoot "artifacts\release\$Version\publish"
$releaseDir = Join-Path $repoRoot "artifacts\release\$Version\downloads"
New-Item -ItemType Directory -Force $publishDir,$releaseDir | Out-Null
$project = Join-Path $repoRoot 'src\VibeDxf.csproj'
dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -p:PublishTrimmed=false "-p:Version=$Version" -p:OutputPath=bin/InstallerBuild/ -o $publishDir
$config = Get-Content (Join-Path $publishDir 'VibeDxf.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not ($config.runtimeOptions.frameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' -and $_.version -like '10.*' })) { throw '.NET 10 Desktop Runtime requirement is missing.' }
if ((Test-Path (Join-Path $publishDir 'coreclr.dll')) -or (Test-Path (Join-Path $publishDir 'hostfxr.dll'))) { throw 'Runtime files were unexpectedly bundled.' }
Copy-Item (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt') $publishDir
Copy-Item (Join-Path $PSScriptRoot 'requirements.txt') (Join-Path $publishDir 'REQUIREMENTS.txt')
$zip = Join-Path $releaseDir "VibeDxf-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip -Force
if ($PublishOnly) { Write-Host "Framework-dependent app and portable ZIP ready at $releaseDir"; return }
if (-not $IsccPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $IsccPath = $command.Source }
    else {
        foreach ($candidate in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
            if (Test-Path -LiteralPath $candidate) { $IsccPath = $candidate; break }
        }
    }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath)) { throw 'Inno Setup 6 ISCC.exe was not found. Install Inno Setup or pass -IsccPath.' }
& $IsccPath "/DAppVersion=$Version" "/DPublishDir=$publishDir" "/O$releaseDir" (Join-Path $PSScriptRoot 'VibeDxf.iss')
$installer = Join-Path $releaseDir "VibeDxf-Setup-$Version-win-x64.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw 'Installer output is missing.' }
@($installer,$zip) | ForEach-Object { $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256; '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), (Split-Path $_ -Leaf) } | Set-Content (Join-Path $releaseDir 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Installer, portable ZIP and checksums ready at $releaseDir"
