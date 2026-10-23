# DrivePartitionTool — local Windows x64 package

Keep the complete package folder together. Run `DrivePartitionTool.exe` from a permanent location that you can write to. The app includes its .NET/WPF runtime; its catalog service and disk worker are in their own subfolders. Personal settings, rules, tags, history and logs are stored separately under `%LOCALAPPDATA%\DrivePartitionTool`.

This package has completed non-admin testing only. Real Windows-copy, boot-repair and VirtualBox boot proofs remain open. Development/test disk operations are limited to File Backed Virtual disks. Release disk writes require the app's preview, typed confirmation and a separate administrator prompt. Development verification never targets physical disks.

## Everyday use and optional Windows integration

Open the app, search from Home, or enter a folder path. Copy/cut/paste, rename, recycle and undo use the same engine from folder, search, rule and layout workflows. Permanent deletion requires an explicit confirmation.

To pin the app, right-click its running taskbar icon and choose **Pin to taskbar**. Keep the package in that location after pinning. Use the same menu to unpin it.

In **Settings → Integration**, **Open folders with DrivePartitionTool** is off until you switch it on. It installs per-user folder/directory/drive verbs and remembers previous defaults. Switch it off to restore them. **Restore previous folder settings** also handles a partial registration. Changes made outside the app are preserved. Windows surfaces that bypass normal Shell verbs can keep opening File Explorer; checking the owner's real workflow remains a manual follow-up. No folder association is enabled by publishing, launching, or testing this package.

Closing the main window keeps active file/disk operations running in the tray. Open the tray icon to review or cancel them. **Exit** asks you to finish or cancel active jobs first. Automatic rules can also keep the app in the tray when that setting is on.

## Optional catalog service installation or upgrade — owner/admin phase

The app works with an in-process catalog until the optional background service is installed. Installation/upgrade has **not** been executed during the non-admin goal.

1. Keep the new package in a permanent folder. Close any older installed copy of DrivePartitionTool and finish its active jobs.
2. Open the new package's app and use **Settings → Search catalog → Install/repair**. Approve the administrator prompt when you are present. The app passes your account SID so the service pipe stays private to your account, Administrators and SYSTEM.
3. The installer stops the service, copies the app and both companions to `%ProgramFiles%\DrivePartitionTool`, configures the service for delayed automatic start and restarts it. If a file is in use or a step fails, close the old copy and retry; review `%ProgramData%\DrivePartitionTool\install.log` before treating the upgrade as complete.
4. Check that Settings reports the service running and that search/boot metadata works. Service stop/start, upgrade and pipe ACL verification remain administrator follow-ups.

For an already-open Administrator PowerShell, the equivalent explicit command is:

```powershell
$ownerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& .\DrivePartitionTool.exe --install-service --owner $ownerSid
```

Use the SID of the account that will use the app if the administrator account differs. Settings supplies this automatically. **Remove service** removes its registration while keeping program files and catalog data; it also needs the owner present for an administrator prompt. No install/remove command is run by `publish.ps1`.

## Logs, crashes and updates

Daily logs are under `%LOCALAPPDATA%\DrivePartitionTool\logs`. Unexpected startup/dispatcher failures produce a standalone crash report and close the app rather than continuing in an unknown state. If the normal data location is unusable, the report goes under `%TEMP%\DrivePartitionTool\crash-reports`. Reports include exception details, version, process and OS information. Review unfinished operations before retrying them.

Before removing or relocating a package that handles folders, turn that integration off. Copy/extract the next package to a fresh permanent folder, launch it and re-enable integration if wanted. Per-user data stays in its existing location. Keep the previous package until the new one is verified.

## Building and verifying a package from the repository

```powershell
./build.ps1
./publish.ps1
./tools/verify-package.ps1 -PackageDirectory '<printed package directory>'
```

`publish.ps1` creates a unique folder under `artifacts/publish`, preserving existing packages. Use `-Zip` to produce a ZIP too. All three executables are self-contained, untrimmed, Windows x64 builds; ReadyToRun is enabled unless `-NoReadyToRun` is specified. The manifest records file lengths/hashes, runtime, source commit and whether the source had uncommitted changes. Verification reads payload files and PE resources; it never starts the disk worker or service.

From the repository, the UI suite can target the printed package without changing live settings:

```powershell
$env:DPT_TEST_APP_EXE = '<package directory>\DrivePartitionTool.exe'
$env:DPT_SAFETY = 'virtual-only'
dotnet test tests/Dpt.App.UITests --no-build --filter 'Category=UI'
```

The harness uses temporary app data/browse folders and checks the packaged app loads its own `coreclr.dll`, with the external runtime location disabled. Folder-registration tests use GUID-named test registry keys, never live folder associations. Elevated VHD and boot checks remain listed in `docs/PROGRESS.md` in the repository.

References: [.NET self-contained publishing](https://learn.microsoft.com/en-us/dotnet/core/deploying/), [ReadyToRun](https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run), [per-user Shell registration](https://learn.microsoft.com/en-us/windows/win32/shell/fa-file-types).
