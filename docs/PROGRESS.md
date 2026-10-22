# Progress

Evidence that each milestone's exit criteria in `IMPLEMENTATION_PLAN.md` are met.

| Milestone | Status | Commit |
|---|---|---|
| Step 0 — Repo setup | Done | `481c13a` |
| M0 — Bootstrap | Done | see git log |
| M1 — Explorer shell | Done | see git log |
| M2 — Index service, search, folder sizes | Done | see git log |
| M3 — Storage insight | Not started | |
| M4 — Safe file-operation engine | Not started | |
| M5 — Categories and rules | Not started | |
| M6 — Folder-structure experiments | Not started | |
| M7 — Disk worker + partitions | Not started | |
| M8 — Whole-drive clone | Not started | |
| M9 — Windows-only copy + boot repair | Not started | |
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
