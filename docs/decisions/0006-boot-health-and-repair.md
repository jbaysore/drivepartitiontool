# 0006 — Read-only boot health and explicit boot-file repair

Status: non-admin implementation and fixtures verified; privileged execution and boot proofs remain deferred.

Boot health shows the running-Windows disk, startup-files disk, EFI/active partition locations, and free space when Windows exposes it. The catalog service has one new read-only metadata query. It takes no payload, command, path or BCD-store selection; its implementation invokes only the fixed `bcdedit /enum all /v` query. The service pipe still exposes no file-mutating commands. The unelevated app does not request elevation to read BCD. Missing service/volume access is explained instead of being represented as healthy or empty configuration.

The parser preserves fields and continuation values from English BCDEdit output and supplies plain-language entry meanings. Uninterpreted/localized output remains visible as raw details with an explicit statement that no health conclusion was inferred. Extending localized-field support remains possible without losing the original output.

Boot-file repair is separate from health inspection. It requires a source NTFS volume, an explicitly selected FAT32 EFI partition (UEFI) or active NTFS MBR partition (BIOS), matching fingerprints, the system-partition unlock setting and the typed destination disk number. Development/test builds require virtual source and target disks. A same-disk source/target is allowed only for a single boot-file repair step; clone and Windows-copy retain their same-disk refusal.

The worker checks readable Windows data and tool availability before assigning a temporary letter or writing. It saves a readable existing BCD file under a unique backup name, passes the exact destination letter to BCDBoot/Bootsect, verifies a readable nonempty destination BCD, and removes only its own temporary access path. These checks do not demonstrate bootability. No repair command or VHD operation was executed during the non-admin goal.

Verification: Core parser/argument fixtures; fake-platform ordering, refusal and cleanup tests; view-model permission/confirmation tests; a pipe test rejecting caller-supplied command/path payloads and a mutating method; UI inspection with repair disabled. A real-VHD test is prepared under `Category=Elevated` and requires an actual Windows installation on an attached disposable VHD before it can run.

References: [BCDEdit enumeration and permissions](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/bcdedit--enum), [BCDBoot destination options](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/bcdboot-command-line-options-techref-di?view=windows-11), [Bootsect target semantics](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/bootsect-command-line-options?view=windows-11).
