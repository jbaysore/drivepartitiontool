<#
.SYNOPSIS
  Runs ./build.ps1 -Elevated in an Administrator PowerShell (one UAC prompt) and saves the output to a log file.
  The elevated tests only create, use and delete throwaway virtual disks (VHDX) and read the system drive's file table.
.PARAMETER Filter
  Optional extra dotnet test filter (e.g. "FullyQualifiedName~ElevatedIndexTests").
#>
param([string]$Filter = "")

$repo = Split-Path -Parent $PSScriptRoot
$log = Join-Path $repo ".local\elevated-tests.log"
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
Remove-Item $log -ErrorAction SilentlyContinue

$inner = "Set-Location '$repo'; `$env:DPT_EXTRA_FILTER='$Filter'; & .\build.ps1 -Elevated *>&1 | Tee-Object -FilePath '$log'; 'EXIT:' + `$LASTEXITCODE | Out-File -Append '$log'"
$p = Start-Process powershell.exe -Verb RunAs -PassThru -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -Command $inner"
Get-Content $log -Tail 60
