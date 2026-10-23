<#
.SYNOPSIS
  Runs the elevated (admin) tests in an Administrator PowerShell (one UAC prompt) and saves a transcript.
  The tests only create, use and delete throwaway virtual disks (VHDX files under %TEMP%\dpt-vhd) and read the
  system drive's file table. Leftover test disks from interrupted runs are detached and deleted first.
.PARAMETER Filter
  Optional extra dotnet test filter (e.g. "FullyQualifiedName~ElevatedIndexTests").
#>
param([string]$Filter = "")

$repo = Split-Path -Parent $PSScriptRoot
$log = Join-Path $repo ".local\elevated-tests.log"
$results = Join-Path $repo ".local\TestResults"
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
Remove-Item $log -ErrorAction SilentlyContinue

$inner = @"
`$ErrorActionPreference = 'Continue'
Start-Transcript -Path '$log' -Force | Out-Null
try {
    Set-Location '$repo'
    `$env:DPT_SAFETY = 'virtual-only'
    `$env:MSBUILDDISABLENODEREUSE = '1'
    `$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    # Clean up test disks left by an interrupted run (only files in our own temp folder).
    `$vhdDir = Join-Path `$env:TEMP 'dpt-vhd'
    Get-ChildItem `$vhdDir -Filter *.vhdx -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Output "Cleaning up leftover test disk `$(`$_.Name)"
        Dismount-DiskImage -ImagePath `$_.FullName -ErrorAction SilentlyContinue | Out-Null
        Remove-Item `$_.FullName -Force -ErrorAction SilentlyContinue
    }
    dotnet build DrivePartitionTool.sln -v q --nologo --disable-build-servers
    `$filter = 'Category=Elevated'
    if ('$Filter') { `$filter += '&$Filter' }
    `$failed = 0
    # One project at a time (each test creates and attaches virtual disks).
    foreach (`$proj in 'tests\Dpt.Index.Tests','tests\Dpt.Shell.Tests','tests\Dpt.DiskWorker.IntegrationTests') {
        Write-Output "=== `$proj"
        dotnet test `$proj --no-build --nologo --filter `$filter --results-directory '$results' --logger trx --logger 'console;verbosity=normal' --blame-hang --blame-hang-timeout 10m
        if (`$LASTEXITCODE -ne 0) { `$failed++ }
    }
    Write-Output "EXIT: `$failed failing project(s)"
}
catch {
    Write-Output "RUNNER ERROR: `$_"
}
finally {
    Stop-Transcript | Out-Null
}
"@
$script = Join-Path $repo ".local\elevated-inner.ps1"
Set-Content -Path $script -Value $inner -Encoding UTF8
Start-Process powershell.exe -Verb RunAs -Wait -WindowStyle Minimized -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$script`""
Get-Content $log -Tail 40
