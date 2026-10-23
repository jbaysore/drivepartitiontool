# Progress

Evidence that each milestone's exit criteria in `IMPLEMENTATION_PLAN.md` are met.

| Milestone | Status | Commit |
|---|---|---|
| Step 0 — Repo setup | Done | `481c13a` |
| M0 — Bootstrap | Done | see git log |
| M1 — Explorer shell | Done | see git log |
| M2 — Index service, search, folder sizes | Done | see git log |
| M3 — Storage insight | Done | see git log |
| M4 — Safe file-operation engine | Done | see git log |
| M5 — Categories and rules | Done | see git log |
| M6 — Folder-structure experiments | Done | see git log |
| M7 — Disk worker + partitions | Done | see git log |
| M8 — Whole-drive clone | Implemented with limitations; boot verification deferred by the owner | `19cad74` |
| M9 — Windows-only copy + boot repair | In progress (copy UI/preflight implemented; boot health pending; elevated copy test not yet run) | see git log |
| M10 — Integration and release | Not started | |

## Step 0 — Repo setup

- `git init -b main`; `AGENTS.md` (owner's standard rules + safety rules), `CLAUDE.md` → `@AGENTS.md`.
- Private repo https://github.com/jbaysore/drivepartitiontool created and pushed.

## M0 — Bootstrap

- Solution `DrivePartitionTool.sln` with all 9 `src/` and 6 `tests/` projects from the plan; `Directory.Build.props`, `.editorconfig`.
- `build.ps1` (fast tests by default, `-Elevated`, `-NoUi`).
- Scope doc and mockups moved to `docs/`; plan copied to `docs/IMPLEMENTATION_PLAN.md`; planning decisions recorded in `docs/PROJECT_SCOPE.md` and ADRs 0001–0004.
- **Evidence:** `./build.ps1` → "Build succeeded. 0 Warning(s) 0 Error(s)"; test run completed (no tests yet).

## M1 — Explorer shell

- WPF window with custom title bar, browser-style tabs (Ctrl+T/W/Tab/Shift+Tab/1–9, middle-click close, drag to reorder), back/forward/up (Alt+arrows, mouse buttons), breadcrumb address bar that becomes editable (Ctrl+L/Alt+D/F4; accepts paths, `%ENV%`, `shell:` names, page words).
- Folder view: virtualized detailed list (Name, Date modified, Type, Size; sortable; natural name order; columns configurable), thumbnail view (`IShellItemImageFactory`, virtualizing wrap panel), folder sizes computed in the background (catalog hook ready for M2), live refresh via `FileSystemWatcher` with incremental merge (selection/rename survive refreshes).
- Explorer behavior: open via `ShellExecuteEx`; native shell context menu (`IContextMenu`, submenus via `IContextMenu3`, Shift for extended verbs; delete/rename/cut/copy/paste/properties routed to the app's own engine); F2, Ctrl+C/X/V, Del, Shift+Del (confirmed), Ctrl+Shift+N, Alt+Enter, Backspace; Explorer-compatible clipboard (CF_HDROP + Preferred DropEffect) and drag-drop both ways; Move to…/Copy to… folder pickers.
- Navigation pane (pages, pinned, This PC with USB icons, Settings) and Home (large all-drives search box, pinned with manage/reorder/rename, frequently visited ranked by frecency, drive space meters, largest-items slot).
- Settings page generated from `[Setting]` attributes; start view Home (default) / Folder / Restore tabs; light/dark palette from the mockup CSS variables + Fluent control theme.
- File-operation engine (`IFileOperation` on an STA thread) with the Recycle Bin guard, journal-backed undo (Ctrl+Z) and activity pill/flyout were built here because M1's paste/rename/recycle need them; M4 extends and verifies them.
- **Deviation from plan:** controls use the built-in Fluent `ThemeMode` for standard controls plus custom styles for app chrome (the plan said Fluent theme only); this matched the mockup more closely.
- **Evidence:**
  - `./build.ps1` → all fast tests pass: Core 45, Data 8, Shell 10, UI 10.
  - FlaUI tests (`tests/Dpt.App.UITests/ExplorerShellTests.cs`): navigate into folder/back/forward/up, Enter/Backspace, tabs open/switch/close (button, Ctrl+T, Ctrl+W), middle-click opens folder in new tab, F2 rename, New folder → rename, paste of a file placed on the clipboard in Explorer's format (copy and cut/move), app copy/cut puts Explorer's format on the clipboard, typed address navigation.
  - Shell test `Recycle_on_drive_without_recycle_bin_blocks_and_keeps_items` (SUBST drive, no Recycle Bin): both items blocked and still on disk.
  - Screenshots checked: Home and folder views render as in the mockup.
  - UI tests only send keyboard/mouse input while the app is the foreground window (`AppSession.EnsureForeground`).
- **Owner check pending:** "usable for a day as the only file browser" is a manual, real-use check for the owner.

## M2 — Index service, search and folder sizes

- `Dpt.Index`: compact in-memory `VolumeIndex` (entries + shared name buffer, child links, rolled-up size / size on disk / file count, binary snapshots that keep raw UTF-16 names). NTFS volumes: direct MFT read (`NtfsVolume.LoadInto`, fix-ups, `$STANDARD_INFORMATION`/`$FILE_NAME`/`$DATA`, extension records, Win32-over-DOS names, hard links counted once) + USN journal tailing (`ReadChanges`, `OpenFileById` stat). Other file systems / no admin: directory scan + `FileSystemWatcher`.
- `IndexEngine` (`ICatalog`): volume discovery, per-volume workers, selection config, snapshots, change events. `SearchExecutor`: words in any order; `ext:`, `type:`, `size:`, `modified:`, `in:`, `drive:`, `folder:`/`file:` filters; hidden-tree exclusion; ranking exact > prefix > word-boundary > substring + recency + visited-location boost; largest files/folders; folder listing.
- `Dpt.Ipc`: length-prefixed JSON named pipe, ACL = owner SID + Administrators + SYSTEM; only write = validated "selected volumes". `Dpt.IndexService`: LocalSystem Windows service (`DrivePartitionToolCatalog`), below-normal priority.
- App: `CatalogService` (service if running, else in-app scan engine), Search page (filter chips, sort, paging, preview pane, live refresh on changes, `--search` argument), folder sizes from the catalog (walk fallback), Home largest items, Settings catalog panel (drives to search, rebuild, install/repair/remove service via one UAC prompt: `--install-service` copies to `%ProgramFiles%\DrivePartitionTool`, private ACL'd catalog folder, `sc create … delayed-auto`).
- **Deviation:** the plan's 1M-file search target is verified on a synthetic 1M-entry index (same search path) rather than a VHD with 1M real files, which would take a long time to create; the real system drive (3.3M entries) is used for MFT load speed.
- **Evidence:**
  - Fast tests (`./build.ps1`): Core 58, Data 8, Shell 10, Index 25, UI 14 — all pass. Includes `One_million_entries_first_page_under_100ms` (median of 5 runs < 100 ms), scan-mode create/rename/move/delete within 2 s, pipe round trip incl. change subscription, MFT parser on synthetic records (fix-ups, torn records, run lists, extension records).
  - Elevated (`tools/run-elevated-tests.ps1`, trx in `.local/TestResults`): `Reads_system_drive_mft_quickly_and_correctly` — C: 3,307,919 entries in 6.9 s (2.1 s per million; target < 30 s); `notepad.exe` size and `drivers\etc` total match the file system. `Journal_reflects_changes_within_two_seconds_and_hard_links_count_once` on a test VHDX — hard link counted once, then create/rename/move/size change/delete-tree all reflected (whole sequence 1.3 s).
  - Search UI tests via UI Automation (no keyboard): nested partial-name match, live appearance of a new file, `ext:` filter; folder search box scoped to the folder.
- **Follow-up:** C:'s logical total (1.49 TB) exceeds its capacity — expected for sparse files/cloud placeholders; the MFT test now also prints size on disk vs. volume used for the next elevated run.

## M3 — Storage insight

- `Dpt.Storage.StorageInventory` (Storage Management WMI, read-only, no admin): disks (name, serial, bus, SSD/HDD, size, GPT/MBR, boot/system, offline, health status), partitions + volumes (letter, label, file system, used/free, Recycle Bin limit), plain-language role tags ("Windows is here", "Starts your PC (EFI)", "Recovery", "Reserved"), unallocated gaps (alignment slack ignored).
- Temperature/wear/power-on hours need admin: served by the catalog service (`diskHealth` pipe method, `MSFT_StorageReliabilityCounter`); without the service the page says how to enable them.
- Drives page: card per disk with a proportional partition bar (`ProportionalPanel`), badges ("Windows starts from here", USB, Virtual, unallocated), per-partition usage meters, Open. Largest items page: files/folders, drive/type/age filters, include-hidden, "show the specific folders" (drops parents ≥ 80 % explained by a listed subfolder), size bars, size on disk, file counts, folder drill-down with Back, open location, recycle (M4 guard), move to. `--open <Page>` argument.
- **Evidence:**
  - `StorageInventoryTests.Inventory_matches_get_disk_and_get_partition` (non-elevated): every disk's name, size, boot flag, partition style and every partition's number, offset, size, drive letter and GPT type match `Get-Disk`/`Get-Partition` on this PC; system volume tagged "Windows is here".
  - `Attached_test_vhd_appears_as_virtual_disk_with_its_partition` (elevated, test VHDX): passes (2026-10-23).
  - UI (UIA reads only): `Largest_items_lists_biggest_files_and_folders` (largest file first; folder view shows `2024`, hides `Movies` that it fully explains), `Drives_page_lists_disks`.
  - Screenshot of the Drives page checked against `Get-Disk`/`Get-Partition` (2 NVMe disks, MBR D:, GPT C: with System Reserved/Recovery/EFI).

## M4 — Safe file-operation engine

- Built on the M1 engine: every operation runs through `IFileOperation` on its own STA thread (several at once), each with progress, pause/resume and cancel in the activity flyout; journaled for undo.
- Recycle guard: `FOFX_RECYCLEONDELETE`; `PreDeleteItem` without `TSF_DELETE_RECYCLE_IF_POSSIBLE` → item blocked (never deleted) and listed; up-front warning from the per-volume Recycle Bin limit/NukeOnDelete (`RecycleBin.PrecheckReason`); dialog "Cancel — keep them" / "Permanently delete these"; permanent deletion only via a `PermanentDeleteConfirmation` covering every path.
- Conflicts: Replace / Skip / Keep both per item with "set all to"; moving/copying a folder into itself is refused.
- Bulk actions: recycle / move to / copy to on multi-selections, search results and largest items; **bulk rename** (`BulkRenamePlanner`: `{name}`, `{n:000}`, `{date:…}`, `{folder}`, find/replace incl. regex, letter case, extension; duplicate/clash/invalid-name checks; live preview; two-step temporary names for swaps/chains; undo as one batch); **Duplicate here** (Ctrl+D) and Copy to… for cloning files/folders.
- **Evidence:**
  - `Dpt.Shell.Tests` (temp folders): copy/move, copy-name in same folder, conflict policies, rename/new folder/new file, recycle then restore from the `$R` path, permanent delete refused without matching confirmation, SUBST (no Recycle Bin) items blocked and kept.
  - New `Dpt.App.Tests` (scripted owner): blocked recycle + Cancel keeps both items; + "Permanently delete" removes; Shift+Del does nothing unless confirmed; conflicts cancel/skip/replace; folder-into-itself refused; undo of move, rename, new folder, copy and recycle; bulk rename chain 1→2, 2→3 and its undo; duplicate; name validation. 9/9 pass.
  - `Dpt.Core.Tests` bulk-rename planner tests (templates, regex, case, extension, duplicates, clashes).
  - `ElevatedRecycleTests.Item_larger_than_the_recycle_bin_limit_is_blocked_not_deleted` (VHD with a 1 MB bin limit): **its first run failed and exposed a real bug** — Windows' "recycle if possible" mode permanently deleted the 8 MB file and the per-item guard did not stop it (`PreDeleteItem` still reported "recycle if possible"). Fix (2026-10-23): the engine now measures each item (folders totalled) and compares it with the drive's effective Recycle Bin limit (configured size, 0 for "delete immediately", or a cautious 5 % default) **before** anything reaches Windows; items that don't fit are blocked for the owner's decision. Any item Windows reports as deleted without arriving in the Recycle Bin is reported as a failure, never as done. Rerun passes: oversized file kept and listed as blocked, small file recycled.

## M5 — Categories and rules

- Categories: `categories` + `item_tags` keyed by volume serial + file ID (`FileIdentity`), so tags follow renames/moves; stale paths are re-resolved with `OpenFileById` + `GetFinalPathNameByHandle`; path-only fallback for file systems without IDs (updated when the app moves/renames). Tri-state "Categories…" dialog (Ctrl+K) for selections; optional Categories column; `cat:Name` search filter (AND across categories); category list with counts, rename, delete, "show items".
- Rules (`Dpt.Core.Rules`): folders (+subfolders), file/folder/both, all/any condition groups (name contains/starts/ends/is/pattern incl. `/regex/`, extension list, type group, size, modified/created older/newer/before/after, category, location), actions tag and/or move to a template folder (`{year} {month} {monthname} {day} {ext} {type} {category} {name}`), conflict policy, trigger manual/automatic, scope existing/new/both. `RuleDescriber` writes the plain-language sentence shown everywhere.
- `RuleService`: preview plans (matches, destinations, conflicts, totals); moving rules refuse to run until previewed (and changing what/where they move resets that); automatic rules watch their folders, ignore downloads in progress (`.crdownload`, `.part`, …) and wait for files to settle; each run is one journal batch (moves + tags) undone together with Ctrl+Z.
- UI: Rules & categories page, sentence-style rule editor with live summary and live match count, preview window with "Run now", notifications for automatic runs; keep running in the notification area while automatic rules exist (open / pause rules / exit), "Start at sign-in" (HKCU Run entry, only changed when the owner flips the setting).
- **Evidence:**
  - `RuleEngineTests` (Core, 20 cases): folder scoping, every clause type, all/any/nested groups, destination templates, categories added only once, folder-into-itself guard. Core total 83 pass.
  - `RuleServiceTests` (App): preview lists the planned move and a moving rule refuses to run before preview; **new file arrives → automatic rule tags and moves it → Ctrl+Z restores it and removes the tag**; tags follow a rename and `cat:` resolves the new path; in-progress downloads ignored.
  - Screenshot of the Rules & categories page with two sample rules (since removed from the scratch database).

## M6 — Folder-structure experiments

- `Dpt.Core.Layouts`: a layout = folders to reorganize + target folder + ordered "these files go here" templates (type / extensions / name words → relative path with `{year}`, `{month}`, `{ext}`, `{type}`, `{category}` …) + fallback (or leave unmatched in place). `LayoutPlanner` produces every file's destination, a virtual folder tree with counts/sizes, and flags name clashes (between planned files or with files already there), files already in place and invalid patterns — no disk changes.
- Folder layouts page: saved layouts, editor, **side-by-side preview of up to three layouts** (stats + expandable tree), "Apply this layout…" (re-plans, confirms counts, one undoable batch; optional removal of folders left empty), "Make rules for new files" (one automatic rule per template, still preview-gated), "Try layouts for this folder…" and "Largest items in this folder" from any folder.
- Safety fixes found while building M6:
  - **Engine: same-named items in one copy/move batch** (e.g. `a\photo.jpg` and `b\photo.jpg` → one folder) were queued under the same name, so the second could overwrite the first. Names claimed earlier in the batch now count as taken ("photo (2).jpg"). Regression test `Same_named_sources_in_one_batch_keep_both`.
  - **Tags and reused file IDs:** a deleted file's ID can be reused by Windows for a new file; tags now store the file's creation time and stale matches are dropped (migration 4).
  - Same-drive bulk moves (layouts and their undo) use a direct rename (`FastMove`, never overwrites) instead of per-item shell operations: 10k moves ≈ 23 s instead of ≈ 116 s.
- **Evidence:**
  - `LayoutPlannerTests` (Core): destinations/tree/counts, first-match-wins, unmatched left in place, clashes and already-in-place, layout → rules.
  - `LayoutServiceTests.Ten_thousand_files_preview_matches_result_and_undo_restores` (App): 10,000 generated files in 185 folders, 28 name clashes; per-folder file counts after applying exactly equal the preview tree; source emptied; one Ctrl+Z restores every original path. Plan 2.9 s, apply 23 s, undo 24 s.
  - Screenshot of the Folder layouts editor.

## M7 — Disk worker and partition management

- `Dpt.Ipc` disk plan contracts: `DiskPlan` (target/source `DiskFingerprint` = number + unique ID + serial + size + partition-table hash, steps, typed confirmation, unlock flag, status file), progress lines, exit codes.
- `Dpt.DiskWorker` (`requireAdministrator` manifest, one UAC prompt per operation): checks typed confirmation → **virtual-only safety mode** (always in Debug builds and with `DPT_SAFETY=virtual-only`; refuses any disk whose bus type isn't File Backed Virtual) → fingerprint (refuses if the disk or its partitions changed since the preview) → protection (`PlanRunner.CheckProtection`: never the running Windows partition; boot/EFI/recovery/MSR locked unless unlocked in Settings; never initialize/clone onto the boot disk) → executes via Storage WMI (`StorageOperations`: initialize, create (+format), delete, resize with `GetSupportedSize` bounds, format, drive letter, label, offline/online), re-checking disk identity before every step; JSON-lines status file.
- App: Disk tools page (clickable partition bars, actions limited to what makes sense for the selection with reasons when disabled, development-build notice), action dialog with before/after bars, plain-language "your data" / "undo" notes, typed confirmation for delete/format, progress from the worker (`DiskWorkerLauncher`), entry points for clone / Windows copy / boot health (M8/M9).
- **Evidence so far:** `DiskWorkerRefusalTests` (non-elevated, 6 tests; safety and protection rules tested as pure checks or read-only probes so no test can reach a write on a real disk): physical disk refused in virtual-only mode; confirmation mismatch refused; changed partition table refused; Windows partition refused even when unlocked (format/delete/resize/letter/initialize); EFI/recovery refused unless unlocked. Screenshot of the Disk tools page.
- **Elevated evidence (2026-10-23, `tools/run-elevated-tests.ps1`, projects run one at a time, transcript in `.local/elevated-tests.log`):** `PartitionOperationTests.Shrink_create_format_letter_label_delete_extend` (shrink to 400 MB → create exFAT "DPTNEW" with a letter → rename → reformat NTFS → move letter → remove letter → delete → extend back to the original size, each through the real elevated worker), `Worker_refuses_a_plan_whose_disk_changed` (stale fingerprint refused after a separate shrink), `Initialize_a_blank_disk` (delete the only partition, create FAT32) — all pass. The runner now also detaches and deletes leftover test disks from interrupted runs.

## M8 — Whole-drive clone

- `CloneDisk` worker step (`Dpt.DiskWorker/Clone`): verifies both fingerprints (source only read), refuses destinations smaller than the source's partitions, recreates the source's GPT/MBR layout on the destination with Windows (new disk and partition IDs; Windows' automatic reserved partition removed first), **takes the destination offline**, copies NTFS partitions' used clusters only (`FSCTL_GET_VOLUME_BITMAP`) from a **Volume Shadow Copy snapshot** (`Win32_ShadowCopy`, deleted afterwards) plus the backup boot sector after the last cluster, copies other partitions (EFI, MSR, recovery) raw, brings the disk online, grows the last NTFS partition into extra space (setting), and runs `bcdboot … /f UEFI|BIOS` when the copy holds Windows (temporary letters removed afterwards).
- Clone wizard: source/destination pickers with partition bars and contents, destination excludes the source and the running Windows disk, plain-language "everything on disk N is replaced" warning, size note, extend option, outcome text, typed `DISK N` confirmation, progress from the worker.
- **Deviations:** (1) destinations smaller than the source's partition area are refused with an explanation instead of falling back to a file-by-file copy of the last partition; (2) the VirtualBox "boots to the login screen" proof is **deferred at the owner's request (2026-10-23)** — goal criterion 4 is open.
- **Evidence (elevated, test VHDs):** `CloneTests.Clone_makes_an_identical_copy_and_uses_extra_space` — 600 MB system-like source (EFI FAT32 + MSR + NTFS with a 12 MB random file and 200 notes) cloned to a 1000 MB disk that held other data: same partition count/offsets/GPT types, new disk ID, EFI partition byte-identical, every file's SHA-256 identical, old destination data gone, label preserved, last partition grew by > 300 MB, source files and layout unchanged (3.5 min). `Clone_refuses_a_destination_that_is_too_small` (destination untouched), `Clone_requires_the_typed_disk_number`.

## M9 — Windows-only copy and boot repair (in progress)

- `WindowsContentClassifier` and shared `WindowsCopyPlanning` (Core): separate required Windows/boot/profile files, `ProgramData\Microsoft`, Microsoft account branches and shared program components from selectable programs, app settings, personal folders and other content. Registry program-location hints split custom install paths; current-user Known Folder redirections are identified. Other installations' offline registry metadata remains unavailable without loading their hives; the preview explains this and keeps their folders selectable by location. Literal exclusions reject traversal, wildcards, control characters and required content.
- `WindowsCopyViewModel` / `WindowsCopyWindow`: source NTFS volume and protected destination pickers, explicit UEFI/BIOS choice, asynchronous content/size discovery, per-item copy/stays-on-source preview and neutral consequences, before-write layout/space/erase summary, typed confirmation, worker progress and outcome. Unreadable branches are kept in full; partial sizes and external links/cloud content are disclosed. Scan cancellation cannot replace a newer source's choices. Excluded personal folders can be copied separately through the existing file engine, preserving profile paths; the backup folder is resolved through junctions to a volume GUID and must be on a third writable local disk.
- `WindowsCopyEngine` now validates fingerprints, source NTFS membership, destination protection/virtual-only mode, single-step plans, literal exclusions and mandatory typed disk confirmation before platform access. Snapshot creation and complete content measurement precede destination erasure. Capacity allows the selected content plus a temporary uncompressed image and working space. The real platform rechecks destination identity/layout before its writes, uses structured process arguments, requests DISM integrity/verification/EA preservation, and cleans up the image, config, directory link, shadow and temporary letters on failure. Fake-platform tests cover failures at each stage without running any privileged command.
- **Recovery layout deviation addressed in implementation:** a separate 1 GiB recovery partition is now created after Windows (UEFI: EFI + MSR + Windows + Recovery; BIOS: active Windows + Recovery). A readable `Winre.wim` is copied and registered only against the offline destination. If unavailable, the worker reports that recovery space is reserved but recovery tools were not registered. Recovery activation/attributes, file metadata preservation and actual bootability still require elevated VHD/VM proof; see ADR 0005.
- **Not yet verified:** `WindowsCopyTests.Copies_windows_and_chosen_items_only_and_leaves_the_source_unchanged` is written (elevated, VHDs) but has not run — the owner can't approve admin prompts right now.
- **Non-admin evidence (2026-10-23):** `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./build.ps1` with an explicit unelevated-shell check: zero build warnings/errors, **221 tests passed** (Core 121, Data 8, Shell 11, worker 19, Index 24 + 1 isolated performance, App 20, UI 17). Log `.local/m9-copy-final-tests.log`. New coverage includes required-content/config/capacity checks, fake-platform ordering/refusals/failure cleanup, preview confirmation/stale scans, temporary-tree discovery, canonical backup-volume refusal, and UI opening with the Run action disabled. The preview screenshot `.local/m9-windows-copy-preview.png` was visually inspected. UI teardown sometimes required the existing harness fallback to kill its own app after a modal window; this remains a shutdown-review item.
- **Next:** boot-health panel/BCD fixtures and boot-repair planning/worker path, then M10 and the remaining goal audit in `NON_ADMIN_AUDIT.md`. No privileged copy/boot operation or VHD attach/detach has been executed under this goal.

## Non-admin cleanup and handoff (2026-10-23)

- Preserved the unfinished M9 implementation and its explicitly elevated VHD test. No elevated helper, disk mutation or boot command was executed during this cleanup.
- The million-entry search test is tagged `Category=Perf` and runs after the other non-elevated tests, preserving its median-under-100-ms assertion. Both phases respect `-NoUi` and exclude elevated tests. `DPT_SAFETY=virtual-only` is now set for every build-script test run, including Release.
- Added `build.ps1 -MaxCpuCount 1` for environments where parallel MSBuild workers fail. In this sandbox, parallel builds stopped with no compiler errors reported; a serial build succeeded with zero warnings/errors.
- The reusable continuation prompt is `docs/GOAL_NO_ADMIN.md`. It covers M9 UI/boot-health implementation, M10 preparation and non-admin regression checks, while keeping elevated VHD tests, boot proofs, service installation and owner checks open.
- **Verification:** `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./build.ps1`, run outside the restricted sandbox with an explicit check that the shell was not Administrator: build succeeded with zero warnings/errors; **173 tests passed** (Core 90, Data 8, Shell 11, Index 24 + 1 isolated performance test, disk-worker protection/inventory 8, App 15, UI 16). The 1M-entry test retained its median-under-100-ms assertion. Full log: `.local/cleanup-final-tests.log` (local, ignored).
- **Observed intermittent failure:** an earlier full run timed out in `Largest_items_lists_biggest_files_and_folders`. It subsequently passed alone (1 s) and in the complete rerun (2 s); no UI code or timeout assertion was changed. The initial sandbox attempt also could not connect to testhost; its identified processes were stopped before the unelevated rerun. Elevated and VirtualBox tests were not run.
