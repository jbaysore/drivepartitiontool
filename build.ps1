<#
.SYNOPSIS
  Builds the solution and runs tests.
.DESCRIPTION
  Default: build + all fast tests (everything not tagged Category=Elevated).
           Timing-sensitive Category=Perf tests run afterwards, on their own.
  -Elevated: build + only Category=Elevated tests (index + disk-worker tests on test VHDs).
             Must be run from an Administrator shell.
  -NoUi:     skip FlaUI tests (Category=UI), e.g. when no interactive desktop is available.
  -MaxCpuCount 1: use one MSBuild worker when parallel workers are unavailable.
#>
param(
    [switch]$Elevated,
    [switch]$NoUi,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateRange(1, 1024)][int]$MaxCpuCount = [Environment]::ProcessorCount
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
# Don't leave MSBuild/compiler server processes behind: they inherit console/pipe handles and keep callers waiting.
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:UseSharedCompilation = 'false'
$env:DPT_SAFETY = 'virtual-only'

dotnet build DrivePartitionTool.sln -c $Configuration -v q --nologo --disable-build-servers "-m:$MaxCpuCount"
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

if ($Elevated) {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "-Elevated tests must run from an Administrator shell."
    }
    $filter = 'Category=Elevated'
    if ($env:DPT_EXTRA_FILTER) { $filter += '&' + $env:DPT_EXTRA_FILTER }
} else {
    $filter = 'Category!=Elevated&Category!=Perf'
    if ($NoUi) { $filter += '&Category!=UI' }
}

$blame = if ($Elevated) { @('--blame-hang', '--blame-hang-timeout', '5m', '--results-directory', '.local\TestResults', '--logger', 'trx') } else { @() }
dotnet test DrivePartitionTool.sln -c $Configuration --no-build --nologo "-m:$MaxCpuCount" --filter $filter --logger "console;verbosity=normal" @blame
if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
if (-not $Elevated) {
    # Timing-sensitive tests run alone, after everything else, so other test projects don't compete for the CPU.
    $perfFilter = 'Category=Perf&Category!=Elevated'
    if ($NoUi) { $perfFilter += '&Category!=UI' }
    dotnet test tests\Dpt.Index.Tests -c $Configuration --no-build --nologo --filter $perfFilter --logger "console;verbosity=normal"
    if ($LASTEXITCODE -ne 0) { throw "Performance tests failed." }
    if (-not $NoUi) {
        # Measure the shipped ReadyToRun package, including its bundled runtime. Keep builds and other tests out of the timed processes.
        if ($Configuration -ne 'Release') {
            dotnet build tests\Dpt.App.UITests -c Release --nologo --disable-build-servers "-m:$MaxCpuCount" -v q
            if ($LASTEXITCODE -ne 0) { throw "Release performance build failed." }
        }
        $previousPackage = $env:DPT_TEST_APP_EXE
        try {
            if (-not $previousPackage) {
                $packages = @(& (Join-Path $PSScriptRoot 'publish.ps1') | Where-Object { $_.PSObject.Properties['PackageDirectory'] })
                if ($packages.Count -ne 1) { throw 'Publishing did not return exactly one verified package.' }
                $env:DPT_TEST_APP_EXE = Join-Path $packages[0].PackageDirectory 'DrivePartitionTool.exe'
            }
            dotnet test tests\Dpt.App.UITests -c Release --no-build --nologo --filter $perfFilter --logger "console;verbosity=detailed"
            if ($LASTEXITCODE -ne 0) { throw "UI performance tests failed." }
        } finally { $env:DPT_TEST_APP_EXE = $previousPackage }
    }
    Write-Host "All tests passed ($filter, then $perfFilter)." -ForegroundColor Green
} else {
    Write-Host "All tests passed ($filter)." -ForegroundColor Green
}
