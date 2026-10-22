[CmdletBinding()]
param([string]$Configuration = 'Release', [switch]$Release, [switch]$PublicFeed,
    [string]$ExpectedCommit, [string]$ExpectedLibrarySha256)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskProject = Join-Path $taskRoot 'src/VibeDxf.Wpf/VibeDxf.Wpf.csproj'
$taskVersion = ([xml](Get-Content -LiteralPath $taskProject -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$taskFeed = Join-Path $taskRoot 'artifacts/nuget/local'
$taskRun = Join-Path $taskRoot ('artifacts/nuget/validation/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $taskFeed,$taskRun | Out-Null
$taskHead = (& git -C $taskRoot rev-parse HEAD).Trim()
$taskCommit = 'local-uncommitted'
if ($PublicFeed -and ($ExpectedCommit -notmatch '^[0-9a-f]{40}$' -or $ExpectedLibrarySha256 -notmatch '^[0-9A-Fa-f]{64}$')) {
    throw 'Public verification requires the approved full ExpectedCommit and ExpectedLibrarySha256 from validation.json.'
}
if ($Release -and -not $PublicFeed -and (Test-Path (Join-Path $taskRoot "artifacts/nuget/release/$taskVersion"))) {
    throw 'A release candidate already exists for this version. Preserve it; use a separate clean checkout to rebuild.'
}
if ($Release -or $PublicFeed) {
    $taskChanges = @(& git -C $taskRoot status --porcelain --untracked-files=all | Where-Object { $_ -notmatch '^\?\? \.claude/' })
    if ($taskChanges.Count) { throw 'Release validation requires a clean source checkout (except local .claude configuration).' }
    $taskCommit = $taskHead
    if ($ExpectedCommit -and $ExpectedCommit -cne $taskHead) { throw 'ExpectedCommit must equal the full source HEAD SHA.' }
}

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}

if ($PublicFeed) {
    $taskPackage = Join-Path $taskRun "VibeDxf.Wpf.$taskVersion.nupkg"
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/vibedxf.wpf/$taskVersion/vibedxf.wpf.$taskVersion.nupkg" -OutFile $taskPackage
} else {
    Invoke-DotNet @('restore', $taskProject, '--disable-build-servers', '-p:NuGetAudit=false')
    Invoke-DotNet @('pack', $taskProject, '-c', $Configuration, '--no-restore', '--disable-build-servers', '-o', $taskFeed,
        "-p:RepositoryCommit=$taskCommit", '-p:IncludeSourceRevisionInInformationalVersion=false',
        ('-p:NuspecOutputPath=' + (Join-Path $taskRun 'nuspec')))
    $taskPackage = Join-Path $taskFeed "VibeDxf.Wpf.$taskVersion.nupkg"
}
$taskArchive = [IO.Compression.ZipFile]::OpenRead($taskPackage)
try {
    $taskNames = @($taskArchive.Entries.FullName)
    foreach ($taskRequired in @('LICENSE','README.md','THIRD-PARTY-NOTICES.txt','VibeDxf.Wpf.nuspec')) {
        if ($taskRequired -notin $taskNames) { throw "Missing package file: $taskRequired" }
    }
    if ($taskNames | Where-Object { $_ -match '\.(cs|xaml|dxf|exe|ico|pdb)$|(^|/)(Tests|Fixtures|Assets|bin|obj)/' }) {
        throw 'Application/source/test assets leaked into the package.'
    }
    $taskLibrary = @($taskArchive.Entries | Where-Object FullName -match '^lib/net10\.0-windows[^/]*/VibeDxf\.Wpf\.dll$')
    if ($taskLibrary.Count -ne 1 -or $taskNames.Where({ $_ -like '*.dll' }).Count -ne 1) { throw 'Expected exactly one runtime library.' }
    if ($taskLibrary[0].FullName.Replace('.dll','.xml') -notin $taskNames) { throw 'XML API documentation is missing.' }
    $taskNuspecReader = [IO.StreamReader]::new($taskArchive.GetEntry('VibeDxf.Wpf.nuspec').Open())
    try { $taskNuspec = [xml]$taskNuspecReader.ReadToEnd() } finally { $taskNuspecReader.Dispose() }
    $taskMetadata = $taskNuspec.package.metadata
    if ($taskMetadata.id -ne 'VibeDxf.Wpf' -or $taskMetadata.version -ne $taskVersion -or
        $taskMetadata.license.'#text' -ne 'MIT' -or $taskMetadata.repository.commit -cne $taskCommit -or
        $taskMetadata.repository.url -ne 'https://github.com/yoavmil/VibeDxf') { throw 'Package metadata does not match the approved source and preview.' }
    $taskDependencies = @($taskMetadata.dependencies.group.dependency)
    if (($taskDependencies.id | Sort-Object) -join ',' -ne 'Vortice.D3DCompiler,Vortice.Direct2D1,Vortice.Direct3D11' -or
        @($taskDependencies | Where-Object version -ne '3.8.3').Count -ne 0) { throw 'Unexpected package dependencies.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($taskLibrary[0], (Join-Path $taskRun 'package-library.dll'))
    $taskEntries = @($taskArchive.Entries | Select-Object FullName, Length)
} finally { $taskArchive.Dispose() }
$taskLibraryHash = (Get-FileHash -LiteralPath (Join-Path $taskRun 'package-library.dll')).Hash
if ($PublicFeed -and $taskLibraryHash -ne $ExpectedLibrarySha256) { throw 'Public DLL differs from the approved release candidate.' }

# Copy plain consumer sources, then restore into a fresh cache; no repository ProjectReference or shared source link.
$taskSamples = Join-Path $taskRoot 'samples/PackageConsumers'
$taskConsumers = Join-Path $taskRun 'consumers'
foreach ($taskFile in Get-ChildItem -LiteralPath $taskSamples -Recurse -File | Where-Object FullName -notmatch '[\\/](bin|obj)[\\/]') {
    $taskRelative = [IO.Path]::GetRelativePath($taskSamples, $taskFile.FullName)
    $taskDestination = Join-Path $taskConsumers $taskRelative
    New-Item -ItemType Directory -Force -Path (Split-Path $taskDestination -Parent) | Out-Null
    Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
}
$taskConfig = Join-Path $taskConsumers 'NuGet.Config'
$taskEscapedFeed = [Security.SecurityElement]::Escape($taskFeed)
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
 <packageSources><clear/><add key="local-preview" value="$taskEscapedFeed"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
 <packageSourceMapping><packageSource key="local-preview"><package pattern="VibeDxf.Wpf"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $taskConfig -Encoding utf8
$taskCache = Join-Path $taskRun 'packages'
if ($PublicFeed) {
    '<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>' | Set-Content -LiteralPath $taskConfig -Encoding utf8
}
$taskFixture = Join-Path $taskRun 'package-smoke.dxf'
@'
0
SECTION
2
HEADER
9
$INSUNITS
70
4
0
ENDSEC
0
SECTION
2
TABLES
0
TABLE
2
LAYER
0
LAYER
2
0
62
7
0
LAYER
2
HIDDEN
62
-1
0
ENDTAB
0
ENDSEC
0
SECTION
2
ENTITIES
0
LINE
5
A
8
0
10
0
20
0
11
10
21
0
0
LINE
5
B
8
HIDDEN
10
0
20
3
11
10
21
3
0
MTEXT
5
T
8
0
10
0
20
8
40
1
41
20
71
1
1
Package smoke
0
ENDSEC
0
EOF
'@ | Set-Content -LiteralPath $taskFixture -Encoding utf8

foreach ($taskConsumer in @('ConsoleHost','WpfHost')) {
    $taskConsumerProject = Join-Path $taskConsumers "$taskConsumer/$taskConsumer.csproj"
    # Bind only the copied samples to the candidate version; keep repository examples untouched.
    $taskConsumerXml = [xml](Get-Content -LiteralPath $taskConsumerProject -Raw)
    $taskPackageReference = @($taskConsumerXml.Project.ItemGroup.PackageReference | Where-Object Include -eq 'VibeDxf.Wpf')
    if ($taskPackageReference.Count -ne 1) { throw 'Expected one VibeDxf.Wpf package reference per consumer.' }
    $taskPackageReference[0].SetAttribute('Version', "[$taskVersion]")
    $taskConsumerXml.Save($taskConsumerProject)
    if ((Get-Content -LiteralPath $taskConsumerProject -Raw) -match 'ProjectReference|Compile\s+Include|Link=') { throw 'Consumer is coupled to repository sources.' }
    Invoke-DotNet @('restore', $taskConsumerProject, '--configfile', $taskConfig, '--packages', $taskCache, '--disable-build-servers', '-p:NuGetAudit=false')
    Invoke-DotNet @('build', $taskConsumerProject, '-c', $Configuration, '--no-restore', '--disable-build-servers', '-p:UseAppHost=false')
    $taskOutput = Join-Path $taskConsumers "$taskConsumer/bin/$Configuration/net10.0-windows"
    if ((Get-FileHash -LiteralPath (Join-Path $taskRun 'package-library.dll')).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $taskOutput 'VibeDxf.Wpf.dll')).Hash) { throw 'Consumer did not use the packed library bytes.' }
    $taskAssets = Get-Content -LiteralPath (Join-Path $taskConsumers "$taskConsumer/obj/project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
    if ($taskAssets.libraries["VibeDxf.Wpf/$taskVersion"].type -ne 'package') { throw 'Library restored as something other than a package.' }
    if ($taskAssets.packageFolders.Keys.Count -ne 1 -or -not ([IO.Path]::GetFullPath(@($taskAssets.packageFolders.Keys)[0]).TrimEnd([char[]]'\/') -eq [IO.Path]::GetFullPath($taskCache))) { throw 'Consumer did not use the isolated package cache.' }
}
$taskPackageMetadata = Get-Content -LiteralPath (Join-Path $taskCache "vibedxf.wpf/$taskVersion/.nupkg.metadata") -Raw | ConvertFrom-Json
if ($PublicFeed) {
    if ($taskPackageMetadata.source.TrimEnd('/') -ne 'https://api.nuget.org/v3/index.json') { throw 'Preview was not restored from nuget.org.' }
} elseif ([IO.Path]::GetFullPath($taskPackageMetadata.source).TrimEnd([char[]]'\/') -ne [IO.Path]::GetFullPath($taskFeed)) { throw 'Preview was not restored from the local feed.' }
$taskConsole = Join-Path $taskConsumers "ConsoleHost/bin/$Configuration/net10.0-windows/ConsoleHost.dll"
& dotnet $taskConsole $taskFixture --smoke | Tee-Object -FilePath (Join-Path $taskRun 'console-output.txt')
if ($LASTEXITCODE -ne 0) { throw 'Packed console consumer failed.' }

$taskWpf = Join-Path $taskConsumers "WpfHost/bin/$Configuration/net10.0-windows/WpfHost.dll"
$taskWpfReport = Join-Path $taskRun 'wpf-smoke.json'
$taskWpfProcess = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @(('"'+$taskWpf+'"'),('"'+$taskFixture+'"'),'--smoke','--report',('"'+$taskWpfReport+'"')) -WindowStyle Hidden -PassThru
if (-not $taskWpfProcess.WaitForExit(30000)) { $taskWpfProcess.Kill(); throw 'Packed WPF consumer timed out.' }
if ($taskWpfProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskWpfReport)) { throw 'Packed WPF consumer failed; inspect its smoke report.' }
$taskWpfResult = Get-Content -LiteralPath $taskWpfReport -Raw | ConvertFrom-Json
if (-not $taskWpfResult.Passed) { throw 'Packed WPF consumer reported failure.' }
$taskHead = (& git -C $taskRoot rev-parse HEAD).Trim()
if ($Release -or $PublicFeed) {
    if ($taskHead -cne $taskCommit -or @(& git -C $taskRoot status --porcelain --untracked-files=all | Where-Object { $_ -notmatch '^\?\? \.claude/' }).Count) {
        throw 'Source changed during validation; no release candidate will be saved.'
    }
}
$taskSummary = [ordered]@{
    Package = $taskPackage; Version = $taskVersion; PackageSha256 = (Get-FileHash -LiteralPath $taskPackage).Hash
    LibrarySha256 = $taskLibraryHash
    BaseGitHead = $taskHead; RepositoryCommit = $taskCommit; PublicFeedVerified = [bool]$PublicFeed
    PackageEntries = $taskEntries; Dependencies = $taskDependencies; IsolatedCache = $taskCache
    ConsolePassed = $true; Wpf = $taskWpfResult; ValidationDirectory = $taskRun
}
$taskSummary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRun 'summary.json') -Encoding utf8
if ($Release -and -not $PublicFeed) {
    $taskRelease = Join-Path $taskRoot "artifacts/nuget/release/$taskVersion"
    New-Item -ItemType Directory -Force -Path $taskRelease | Out-Null
    Copy-Item -LiteralPath $taskPackage -Destination $taskRelease
    $taskSummary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRelease 'validation.json') -Encoding utf8
    "$($taskSummary.PackageSha256)  $([IO.Path]::GetFileName($taskPackage))" | Set-Content -LiteralPath (Join-Path $taskRelease 'SHA256SUMS.txt') -Encoding ascii
}
Write-Output "Local package validation passed. Report: $(Join-Path $taskRun 'summary.json')"
