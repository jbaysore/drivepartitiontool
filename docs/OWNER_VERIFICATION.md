# Deferred administrator and owner verification

This is follow-up work, not evidence of passed tests. The non-admin goal did not request UAC, install/remove services, attach/detach VHDs, execute mutating disk plans, capture/apply images or run boot tools. M8/M9 bootability and V1 release acceptance remain open.

## Disposable VHD tests (owner present, Administrator PowerShell)

Keep any reusable Windows fixture outside `%TEMP%\dpt-vhd`; the optional elevated helper cleans that test directory. Use only File Backed Virtual disks, never the host Windows disk or an owner's real data disk. The basic copy/clone tests create their own throwaway VHDs. The boot-repair fixture additionally needs an already-prepared, attached, non-running Windows VHD with actual boot templates.

```powershell
$env:DPT_SAFETY = 'virtual-only'
# Run each group separately. These commands are intentionally NOT run by the non-admin goal.
$env:DPT_EXTRA_FILTER = 'FullyQualifiedName~CloneTests'
./build.ps1 -Elevated
$env:DPT_EXTRA_FILTER = 'FullyQualifiedName~WindowsCopyTests'
./build.ps1 -Elevated
# Replace V: with the disposable Windows fixture's actual root, never C: or another host Windows root.
$env:DPT_BOOT_TEST_WINDOWS_ROOT = 'V:\'
$env:DPT_EXTRA_FILTER = 'FullyQualifiedName~BootRepairVhdTests'
./build.ps1 -Elevated
```

Required evidence: larger and smaller clones, file hashes/streams/permissions/hard links/reparse points, source file/layout preservation, destination erasure, staged-image cleanup without touching unrelated staging files, GPT/MBR layouts and destination BCD. Verify recovery type, required/no-default-letter attributes and absence of temporary letters; test both an available recovery image and the missing-image outcome. A mocked platform or readable destination registry is not a boot proof. The prepared boot-repair test verifies a template hash and source layout; it does not prove an unchanged whole-source-VHD hash.

## Windows login and recovery in VirtualBox

No Windows fixture or VirtualBox boot was prepared or executed during the unattended goal. The implementation plan's `tools/boot-test.ps1` does not exist yet; do not run that nonexistent command or infer a pass from it. An owner-approved Windows fixture/VM setup is needed before an automated boot runner can be validated. Do not download an ISO or install VirtualBox without the owner present.

For each M8/M9 copy, configure an isolated test VM with **only the disposable destination** attached and the matching UEFI/BIOS mode. With that VM already configured, the owner can run:

```powershell
VBoxManage showvminfo 'DPT copy verification' --machinereadable
VBoxManage startvm 'DPT copy verification' --type gui
# After observing Windows login:
VBoxManage controlvm 'DPT copy verification' screenshotpng 'C:\path\to\evidence\windows-login.png'
```

Record the VM/disk identities, selected/excluded content, successful login and recovery result. Follow the generated recovery instructions **inside the copied Windows**; activation on the host/source is not allowed. Keep the whole-source-VHD SHA-256 criterion open until a suitable offline-source test records before/after hashes. A live Windows/VSS source can change independently while it runs; content/template hashes do not substitute for this criterion.

## Service, integration and everyday use

Follow `INSTALL_AND_VERIFY.md` from a permanent package folder. The owner must verify first install, stop/start, upgrade over the previous package, account/pipe access and failure reporting. Live folder-handler enable/disable, drive-root/directory opening, restoring previous defaults, and actual taskbar pinning remain manual checks; automated registry tests used only isolated keys.

Check browsing, tabs, clipboard exchange with File Explorer, rename/create/recycle/undo, search updates, rule previews and runs, layout preview/apply/undo and the Drives display. Finish or cancel jobs before exiting. Automated tests cover these routes in temporary trees; they do not replace the owner's everyday-use acceptance.

For a cold-cache startup check, reboot during the owner phase and launch the final package once with `DPT_MEASURE_PERF=1`; preserve `logs/performance.jsonl` and record whether restoring tabs or catalog work was active. The no-admin measurements could not flush OS caches. Other folder mixes, network/removable media and owner-specific settings also need representative measurements.
