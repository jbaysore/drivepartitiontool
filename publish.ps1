<#
.SYNOPSIS
  Builds and verifies a self-contained Windows x64 folder. Never installs or starts a service.
.PARAMETER OutputDirectory
  Parent for unique package directories (default: artifacts\publish). Existing packages are preserved.
#>
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\publish'),
    [switch]$Zip,
    [switch]$NoReadyToRun
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:UseSharedCompilation = 'false'
$env:DPT_SAFETY = 'virtual-only'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$token = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$stage = [IO.Path]::GetFullPath((Join-Path $outputRoot ".staging-$token"))
[xml]$props = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Directory.Build.props') -Raw
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '^[0-9A-Za-z.+-]+$') { throw 'Unsupported package version.' }
$package = [IO.Path]::GetFullPath((Join-Path $outputRoot "DrivePartitionTool-$version-win-x64-$stamp-$token"))
function Assert-OutputChild([string]$candidate) {
    if (-not $candidate.StartsWith($outputRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Package path escapes the intended output directory: $candidate"
    }
}
Assert-OutputChild $stage
Assert-OutputChild $package
if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $package)) { throw 'The unique package directory already exists.' }
New-Item -ItemType Directory -Path $stage | Out-Null
$readyToRun = (-not $NoReadyToRun).ToString().ToLowerInvariant()
function Publish-Project([string]$project, [string]$destination) {
    dotnet publish $project -c Release -r win-x64 --self-contained true --nologo --disable-build-servers -m:1 -v minimal `
        -p:PublishSingleFile=false -p:PublishTrimmed=false "-p:PublishReadyToRun=$readyToRun" -o $destination
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed. Diagnostic staging files remain at $stage" }
}
Publish-Project 'src\Dpt.App\Dpt.App.csproj' $stage
Publish-Project 'src\Dpt.IndexService\Dpt.IndexService.csproj' (Join-Path $stage 'service')
Publish-Project 'src\Dpt.DiskWorker\Dpt.DiskWorker.csproj' (Join-Path $stage 'diskworker')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\INSTALL_AND_VERIFY.md') -Destination (Join-Path $stage 'README.md')
$commit = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not identify the source commit.' }
$dirty = [bool](git status --porcelain)
$files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})
$manifest = [ordered]@{
    format = 1; application = 'DrivePartitionTool'; version = $version; runtime = 'win-x64'; selfContained = $true
    readyToRun = (-not $NoReadyToRun); sourceCommit = $commit; sourceHadUncommittedChanges = $dirty
    builtUtc = [DateTime]::UtcNow.ToString('O'); files = $files
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'package-manifest.json') -Encoding UTF8
& (Join-Path $PSScriptRoot 'tools\verify-package.ps1') -PackageDirectory $stage
# Both resolved paths were checked against the explicitly chosen parent before moving the directory.
Assert-OutputChild $stage
Assert-OutputChild $package
Move-Item -LiteralPath $stage -Destination $package
if ($Zip) { Compress-Archive -LiteralPath $package -DestinationPath ($package + '.zip') -CompressionLevel Optimal }
Write-Host "Package: $package" -ForegroundColor Green
Write-Host 'No service, folder association or taskbar setting was changed. See README.md for optional owner actions.'
[pscustomobject]@{ PackageDirectory = $package }
