<# .SYNOPSIS
Read-only verification of package hashes, bundled runtimes, x64 apphosts and embedded privilege manifests.
No packaged executable is started.
#>
param([Parameter(Mandatory)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
$manifestPath = Join-Path $packageRoot 'package-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.format -ne 1 -or $manifest.runtime -ne 'win-x64' -or -not $manifest.selfContained) { throw 'Unsupported package manifest.' }
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    if ([IO.Path]::IsPathRooted($entry.path)) { throw 'Manifest paths must be relative.' }
    $path = [IO.Path]::GetFullPath((Join-Path $packageRoot $entry.path))
    if (-not $path.StartsWith($packageRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escapes the package.' }
    if (-not $expected.Add($path)) { throw "Duplicate manifest entry: $path" }
    $file = Get-Item -LiteralPath $path
    if ($file.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Package content mismatch: $($entry.path)" }
}
$actual = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object FullName -ne $manifestPath)
if ($actual.Count -ne $expected.Count -or @($actual | Where-Object { -not $expected.Contains($_.FullName) }).Count -ne 0) { throw 'The package file list does not match its manifest.' }
if (-not ('DptPackageManifestReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DptPackageManifestReader {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    public static string Read(string path) {
        var module = LoadLibraryEx(path, IntPtr.Zero, 2); // LOAD_LIBRARY_AS_DATAFILE: never execute or initialize it
        if (module == IntPtr.Zero) throw new InvalidOperationException("Cannot read executable resources.");
        try {
            var resource = FindResource(module, (IntPtr)1, (IntPtr)24);
            if (resource == IntPtr.Zero) throw new InvalidOperationException("Executable manifest is missing.");
            var bytes = new byte[SizeofResource(module, resource)];
            Marshal.Copy(LockResource(LoadResource(module, resource)), bytes, 0, bytes.Length);
            return System.Text.Encoding.UTF8.GetString(bytes).Trim('\0', '\uFEFF');
        } finally { FreeLibrary(module); }
    }
}
'@
}
foreach ($program in @(
    @{ folder = ''; name = 'DrivePartitionTool'; level = 'asInvoker' },
    @{ folder = 'service'; name = 'DrivePartitionTool.CatalogService'; level = 'asInvoker' },
    @{ folder = 'diskworker'; name = 'DrivePartitionTool.DiskWorker'; level = 'requireAdministrator' }
)) {
    $folder = if ($program.folder) { Join-Path $packageRoot $program.folder } else { $packageRoot }
    foreach ($file in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', ($program.name + '.exe'), ($program.name + '.dll'), ($program.name + '.deps.json'))) {
        if (-not (Test-Path -LiteralPath (Join-Path $folder $file) -PathType Leaf)) { throw "Missing self-contained payload: $folder\$file" }
    }
    $config = Get-Content -LiteralPath (Join-Path $folder ($program.name + '.runtimeconfig.json')) -Raw | ConvertFrom-Json
    if ($config.runtimeOptions.framework -or $config.runtimeOptions.frameworks -or -not $config.runtimeOptions.includedFrameworks) { throw 'The executable is framework-dependent.' }
    $exe = Join-Path $folder ($program.name + '.exe')
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($exe))
    try {
        $reader.BaseStream.Position = 0x3c; $pe = $reader.ReadInt32()
        $reader.BaseStream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'The executable is not an x64 PE image.' }
    } finally { $reader.Dispose() }
    [xml]$xml = [DptPackageManifestReader]::Read($exe)
    $level = $xml.SelectSingleNode("//*[local-name()='requestedExecutionLevel']").GetAttribute('level')
    if ($level -ne $program.level) { throw "Unexpected privilege manifest: $($program.name): $level" }
}
if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'PresentationFramework.dll'))) { throw 'The WPF runtime is missing.' }
Write-Host "Verified $($expected.Count) payload files, three bundled runtimes and their privilege manifests." -ForegroundColor Green
