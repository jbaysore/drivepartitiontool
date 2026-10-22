# Progress

Evidence that each milestone's exit criteria in `IMPLEMENTATION_PLAN.md` are met.

| Milestone | Status | Commit |
|---|---|---|
| Step 0 — Repo setup | Done | `481c13a` |
| M0 — Bootstrap | Done | see git log |
| M1 — Explorer shell | Done | see git log |
| M2 — Index service, search, folder sizes | Not started | |
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
