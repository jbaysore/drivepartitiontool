<#
.SYNOPSIS
  Builds the solution and runs tests.
.DESCRIPTION
  Default: build + all fast tests (everything not tagged Category=Elevated).
  -Elevated: build + only Category=Elevated tests (index + disk-worker tests on test VHDs).
             Must be run from an Administrator shell.
  -NoUi:     skip FlaUI tests (Category=UI), e.g. when no interactive desktop is available.
#>
param(
    [switch]$Elevated,
    [switch]$NoUi,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet build DrivePartitionTool.sln -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

if ($Elevated) {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "-Elevated tests must run from an Administrator shell."
    }
    $env:DPT_SAFETY = 'virtual-only'
    $filter = 'Category=Elevated'
} else {
    $filter = 'Category!=Elevated'
    if ($NoUi) { $filter += '&Category!=UI' }
}

dotnet test DrivePartitionTool.sln -c $Configuration --no-build --nologo --filter $filter --logger "console;verbosity=normal"
if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
Write-Host "All tests passed ($filter)." -ForegroundColor Green
