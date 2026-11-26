# DrivePartitionTool

A Windows file browser with whole-drive search and storage insight, plus disk tools for partitions, drive cloning, and copying Windows to another drive. It is a personal tool, built to replace everyday File Explorer use.

The interesting part is how the risk is contained. Browsing and searching happen all day; changing a disk happens rarely and can destroy data. So the program is split by privilege:

- **`Dpt.App`** always runs as the signed-in user, never as administrator. It performs every file operation, so files keep normal ownership.
- **`Dpt.IndexService`** is a background Windows service that reads NTFS change data for instant search and folder sizes. Its named pipe answers queries only; the one write it accepts is the validated list of volumes to index.
- **`Dpt.DiskWorker`** starts with one administrator prompt per disk operation, carries out one pre-approved written plan, streams progress, and exits.

The long-lived elevated component is read-only, and the destructive component is short-lived. See [ADR 0002](docs/decisions/0002-privilege-split.md).

## Status

Milestones M0–M7 are complete. M8–M10 (whole-drive clone, Windows-only copy with boot repair, and release packaging) are implemented and tested without administrator access; their elevated and boot checks are still open. [docs/PROGRESS.md](docs/PROGRESS.md) records the evidence for each milestone and lists exactly what remains unverified.

Disk operations are tested on virtual disks (VHD/VHDX) only, and `DPT_SAFETY=virtual-only` stays on in every test run. Real drives are never touched by tests ([ADR 0003](docs/decisions/0003-virtual-disk-only-testing.md)).

## Layout

| Path | Contents |
| --- | --- |
| `src/Dpt.App` | WPF application: explorer shell, search, storage views, rules, disk tools UI |
| `src/Dpt.Core` | Shared domain model, plans, and protection checks |
| `src/Dpt.Data`, `src/Dpt.Storage` | Local settings, catalog storage, and storage analysis |
| `src/Dpt.Index`, `src/Dpt.IndexService` | NTFS indexing and the background catalog service |
| `src/Dpt.Ipc` | The channel between the app, the service, and the worker |
| `src/Dpt.DiskWorker` | Per-operation elevated worker that executes one approved plan |
| `src/Dpt.Shell` | Windows shell integration |
| `tests/` | Unit, integration, and UI tests |
| `docs/` | Scope, implementation plan, progress evidence, and decision records |

## Build and test

Requires Windows 11 and the .NET 10 SDK.

```powershell
./build.ps1          # build and run the fast test suite
./build.ps1 -NoUi    # skip UI tests when no interactive desktop is available
```

The last recorded full run (2026-10-23) passed 274 tests with no build warnings. Elevated virtual-disk tests run separately and only on request.

## How it was built

Scope, design decisions, and safety rules were set by me and recorded in `docs/`. The implementation was written with AI coding agents working inside those rules.

## Rights

The source is published for viewing. All rights reserved; no license is granted to copy, modify, or redistribute it.
