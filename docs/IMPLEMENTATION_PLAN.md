# DrivePartitionTool — Initial Implementation Plan

## Context

`PROJECT_SCOPE.md` records 45 answered scoping questions for a personal **Windows-only, everyday file browser** that replaces File Explorer and adds:

- fast cross-drive search that works from partial clues
- folder sizes that include everything inside them
- largest-item and physical-drive views
- safe bulk file operations
- user-defined categories and rules
- previews for trying out different folder structures
- advanced disk tools: cloning, bootable Windows migration and partition management

`ui-first-pass.html` and `ui-preview.html` hold the visual direction: a Harness-inspired window with browser-style tabs, in Home-first and Folder-first variants.

The folder has no code and no Git repo yet. This plan turns the notes into a concrete architecture and a milestone sequence that an implementing agent can follow.

**Decisions made in this planning session (add them to PROJECT_SCOPE.md during M0):**

- **V1 includes everything**, including the disk tools. The work is still built in the milestone order below, so each layer is usable and tested before the next one starts.
- **Search catalog:** a small background Windows service, installed once with admin approval, keeps the catalog up to date. The main app never runs elevated.
- **Default start view:** Home-first. A setting can change it to Folder-first or "reopen last tabs".
- **Testing:** disk tools are tested on **virtual disks only** (VHD/VHDX), with **VirtualBox** for boot tests. Real drives are never touched by tests.
- **"Isolate Windows" means:** make a bootable, Windows-only copy on *another* drive with the system plus chosen apps and folders. The source drive is never modified. In-place cleanup is **out of V1**.

## Technology choices

| Concern | Choice | Why |
|---|---|---|
| Language/runtime | C# on **.NET 10** (SDK 10.0.400 is installed) | Best access to Windows shell, COM, WMI and the Volume Shadow Copy Service (VSS). No Rust toolchain is installed. |
| UI | **WPF** with the built-in Fluent theme (`ThemeMode`) and **CommunityToolkit.Mvvm** | Mature, reliable list virtualization for very large folders, easy shell interop, and no packaging friction (WinUI 3 has packaging friction). |
| Win32/COM interop | **Microsoft.Windows.CsWin32** source generator | Typed P/Invoke for `IFileOperation`, `IContextMenu`, `IShellItemImageFactory`, `DeviceIoControl` and the virtual-disk API. |
| User data | **SQLite** (`Microsoft.Data.Sqlite`) | Stores settings, pins, history, categories, rules and the operation journal. |
| Search index | Custom in-memory index in the service, saved to disk as snapshots | Gives Everything-like speed. SQLite's full-text search (FTS) can't do fast substring search over millions of names. |
| IPC | Named pipes with JSON messages (`System.Text.Json`, source-generated) | Pipe access is restricted to the owner's account and Administrators. |
| Storage info | Windows Storage Management API through WMI (`Microsoft.Management.Infrastructure`, `root\Microsoft\Windows\Storage`) | Covers disks, partitions, volumes, bus type and reliability counters. |
| UI tests | **FlaUI** (UIA3) | |
| Unit/integration tests | xUnit | |

## Solution layout

```
DrivePartitionTool/
  AGENTS.md                 conventions + hard safety rules (see "Safety rules")
  docs/PROJECT_SCOPE.md     moved from root; plus docs/ui/*.html mockups
  docs/decisions/           short ADRs (one per decision above + spikes)
  DrivePartitionTool.sln
  src/
    Dpt.Core/          pure logic: models, query parser, rule engine, layout planner,
                       size formatting, Windows-content classifier rules. No I/O.
    Dpt.Data/          SQLite schema + repositories (settings, pins, visits, categories,
                       item tags, rules, layouts, operation journal); migrations
    Dpt.Ipc/           pipe contracts + client/server helpers (index + disk-worker channels)
    Dpt.Index/         NTFS MFT enumeration (FSCTL_ENUM_USN_DATA), USN journal tailing
                       (FSCTL_READ_USN_JOURNAL), fallback recursive enumerator +
                       FileSystemWatcher for exFAT/FAT32/USB, in-memory index, folder-size
                       aggregation, snapshot persistence
    Dpt.IndexService/  Worker Service host → Windows service (LocalSystem), read-only API
    Dpt.Shell/         IFileOperation engine + progress sink, ShellExecuteEx open,
                       native context menu, icons/thumbnails, Explorer-compatible clipboard
                       (CF_HDROP + "Preferred DropEffect"), drag-drop
    Dpt.Storage/       read-only physical disk/partition/volume model via Storage WMI
    Dpt.DiskWorker/    separate exe launched elevated (UAC) per operation; executes a
                       pre-approved JSON "plan"; partition ops, clone, Windows copy, boot fix
    Dpt.App/           WPF app (Home, Folder tabs, Search, Largest items, Drives, Rules,
                       Layouts, Disk tools, Settings) + `--install-service`/`--uninstall-service`
  tests/
    Dpt.Core.Tests, Dpt.Data.Tests, Dpt.Shell.Tests (temp dirs),
    Dpt.Index.Tests (VHDX fixtures), Dpt.DiskWorker.IntegrationTests (VHDX only),
    Dpt.App.UITests (FlaUI)
  tools/
    vhd-fixtures.ps1     create/attach/detach/destroy test VHDs via diskpart / virtdisk API
    boot-test.ps1        wrap a cloned VHD as a VirtualBox VM (EFI + TPM), boot, check for login screen
```

**Privilege split (important):**

- **App:** runs unelevated in the user's own context. It does all file operations and runs rules, so moved files keep normal ownership.
- **IndexService:** runs as LocalSystem but is *read-only*. Its pipe offers queries and status only; there are no mutating commands.
- **DiskWorker:** started with a UAC prompt for each disk operation. It receives one complete, previewed plan, executes it, streams progress back and exits. It is never a long-running elevated process.

## Cross-cutting design

- **Settings framework (honors "configurable by default").** There is one typed `AppSettings` record. Each property has an attribute giving its section, plain-language description and default. The Settings page is generated from those attributes, so any new behavior becomes a setting with almost no extra work.
- **Theme.** Map the CSS variables from `ui-first-pass.html` (`--dpt-window`, `--dpt-rail`, `--dpt-accent` #646fd0 / dark #a3aaff, `--dpt-green`, `--dpt-red` and the rest) into `Themes/Light.xaml` and `Themes/Dark.xaml`. Use Segoe UI 13px base and Lucide-style icons (Lucide is MIT-licensed; use SVG→XAML geometry).
- **Activity indicator.** A title-bar pill like the mockup ("Scanning E: 68%"). It is driven by one shared `ActivityHub` that the index status, file operations and disk-worker progress all publish to. Clicking it opens an activity flyout listing current and recent operations.
- **Operation journal.** Every create, rename, move and copy the app performs is written to SQLite with source and destination. This enables "Undo" (Ctrl+Z) and "Undo last rule run / layout apply". Recycle actions are undone through the shell's restore function.
- **Plain-language explanations.** Every advanced screen has an expandable "What this means" panel. Text lives in `Dpt.App/Resources/Explanations.resx`, so wording can be revised without touching code. Explanations are neutral: no recommended option is ever highlighted (scope Q17).

## Step 0 — Repo setup (do first, before any implementation)

The owner asked for this to be done and committed before implementation starts.

1. Run `git init -b main` in `C:\Users\Joshua\Projects\DrivePartitionTool`.
2. Write **`AGENTS.md`**, based on `C:\Users\Joshua\Projects\Agentic Harness\AGENTS.md` (the owner's standard rules):
   ```markdown
   # Project working agreements

   - Commit and push all changes made for the task directly to `main` without waiting for further instruction, unless the user explicitly says not to commit or push. Pull requests are not required; create one only if the user explicitly requests it.
   - Include the commit hash in the final summary message.

   # Project context

   - Windows-only personal file browser + drive tool. Scope and decisions: `PROJECT_SCOPE.md`
     (moves to `docs/` in M0). Implementation plan: `docs/IMPLEMENTATION_PLAN.md` (added in M0).

   # Safety rules (mandatory)
   ```
   …followed by safety rules 1–5 from the "Safety rules" section below, copied verbatim. Build and test commands are added in M0, once they exist.
3. Write **`CLAUDE.md`** containing only `@AGENTS.md`, the same as Agentic Harness's `CLAUDE.md`.
4. Add `.gitignore`: the standard VisualStudio/.NET template (`bin/`, `obj/`, `.vs/`, `*.user`, `TestResults/`), plus `*.vhd`, `*.vhdx`, `*.iso`, `*.wim`, `*.vdi`, `.local/` and `logs/`.
5. Add `.gitattributes`: `* text=auto`.
6. Make an initial commit by explicit path (`AGENTS.md CLAUDE.md .gitignore .gitattributes PROJECT_SCOPE.md ui-first-pass.html ui-preview.html`), with the required Co-Authored-By trailer. Call `workspace_activity` and run `git status` immediately before committing.
7. Run `gh repo create jbaysore/drivepartitiontool --private --source . --remote origin --push` (the owner approved a **private** repo).
8. **Checkpoint:** report the commit hash and repo URL, then begin M0 when the owner says to go ahead.

## Milestones

Each milestone ends with its exit criteria met, all tests passing, and a commit **pushed to `origin/main`** (per AGENTS.md). Disk-tool milestones also start with a **spike** whose result is written up as an ADR before the full build.

### M0 — Bootstrap

- `.editorconfig`.
- Create the solution and projects listed above (empty but compiling).
- Add build and test commands to `AGENTS.md`.
- Move the scope doc and mockups into `docs/`, copy this plan to `docs/IMPLEMENTATION_PLAN.md`, and record this session's decisions in the scope doc.
- `build.ps1` that runs `dotnet build` plus `dotnet test`, excluding the slow and elevated test categories.

### M1 — Explorer shell (the daily driver)

**Window and tabs**
- Window with a custom title bar, a browser-style `TabControl` with a + button, closable and reorderable tabs, and Ctrl+T / Ctrl+W / Ctrl+Tab.
- Back / Forward / Up, Alt+arrow shortcuts and a breadcrumb address bar that becomes editable (accepts typed paths, `%ENV%` variables and `shell:` folders).
- Opening a folder replaces the current tab; middle-click or Ctrl+click opens it in a new tab (Q41).

**Folder view**
- Detailed list: Name, Date modified, Type, Size. Columns can be configured, sorted and resized.
- Virtualized with recycling so folders with 100k+ items stay responsive.
- Optional thumbnail view using `IShellItemImageFactory`.
- Folder sizes show "…" until M2 provides them.

**Explorer behavior** (Q42/Q43)
- Opening an item uses `ShellExecuteEx` with the default verb.
- Right-click shows the native shell context menu through `IContextMenu`.
- Supported shortcuts: F2 rename, Ctrl+C/X/V, Del to recycle, Shift+Del to permanently delete with confirmation, Ctrl+Shift+N for a new folder, plus a "New file" menu.
- Clipboard and drag-drop work in both directions with real File Explorer.

**Navigation pane and Home**
- Optional left pane: Home, Search, Largest items, Drives, Rules, Layouts, Disk tools, and a "This PC" drive tree.
- Home matches the Home-first mockup: a large search box with an "All selected drives" scope dropdown, Pinned locations (with manage, reorder and unpin), Frequently visited (from the visits table, ranked by frequency and recency), a modest Drive space section and a Largest items placeholder.
- Start view setting: Home (default), Folder or Restore tabs.

**Exit:** FlaUI tests cover navigating, opening tabs, renaming, creating a folder and pasting a file copied in Explorer. A manual check shows the app is usable for a day as the only file browser, minus search.

### M2 — Index service, search and folder sizes

**Building the index**
- **NTFS volumes:** read the Master File Table with `FSCTL_ENUM_USN_DATA` to get file reference numbers (FRN), parent FRN, name and attributes. Get sizes from `FSCTL_GET_NTFS_FILE_RECORD`, or for speed by parsing `$MFT` directly. Keep the index current by tailing the USN change journal and persist the last USN read. Choose between record-by-record lookup and direct `$MFT` parsing in a short spike: the target is indexing about 1M files in under 30 seconds.
- **Non-NTFS volumes** (exFAT/FAT32 USB drives): scan with `FindFirstFileExW` (`FIND_FIRST_EX_LARGE_FETCH`) and keep current with `FileSystemWatcher` while attached. Re-scan quickly when the drive is plugged back in, using the volume serial number to recognize it.

**Index storage and sizes**
- In memory: one compact array of records per volume, all names in a single UTF-16 buffer, plus a parent→children map.
- Folder sizes are rolled up and store both logical size and size on disk. Hard links are counted once, by FRN. Updates propagate up the folder tree.
- Snapshots are saved to `%ProgramData%\DrivePartitionTool\index\` (access limited to SYSTEM and the owner) so the service starts warm.
- "Selected drives": the user can include or exclude each volume (Settings and Drives view). This is a service setting the app can change through one narrow, validated pipe command. It is the only write the pipe accepts.

**Search**
- The query parser lives in `Dpt.Core`.
- Free-text words match name substrings in any order, ignoring case.
- Optional filters: `ext:`, `type:image|video|doc|audio|archive` (extension groups that can be configured), `size:>1gb`, `modified:` (today / lastweek / 2025 / a date range), `in:<path>` (current folder and everything under it), `drive:E`, `cat:<category>` (after M5) and `folder:` / `file:`.
- The UI offers clickable filter chips, so the syntax is never required (Q4).
- Results are ranked by: exact name > name starts with the query > word-boundary match > substring match, then by how recently the item was modified and how often its location is visited.
- Results stream back in pages, with a target of under 100 ms for the first page.
- Result columns default to Name, full location, Drive, Type, Size and Modified, all configurable (Q32). A preview pane (Alt+P) shows images, text and the shell preview handler.
- Scope: Home searches all selected drives; the search box in a folder tab defaults to "this folder and its subfolders" and can be switched to "all drives" (Q31).

**Other M2 items**
- Folder-view size column uses the service's rolled-up sizes. If the service is unavailable, the app computes sizes on demand in the background.
- `Dpt.App --install-service` (UAC prompt) copies the binaries to `%ProgramFiles%\DrivePartitionTool`, registers the service and sets the pipe access rules. Settings shows the service status and has Repair and Uninstall buttons.

**Exit**
- Index tests on VHDX fixtures pass: creating, renaming, moving and deleting files is reflected in the index within 2 seconds, and folder-size rollups are correct, including with hard links.
- Searching a 1M-file test volume returns the first page in under 100 ms.

### M3 — Storage insight

**Drives view (answers the open question about what it should show beyond capacity)**

For each **physical disk**:
- friendly name/model and serial number
- bus (NVMe/SATA/USB) and media type (SSD/HDD)
- size
- partition style (GPT/MBR)
- health and, when the drive reports them, temperature and wear (from `MSFT_StorageReliabilityCounter`)
- a proportional bar showing its partitions, including unallocated space

For each **partition/volume**:
- drive letter, label and file system
- used and free space
- role tags in plain language: "Windows is here", "Starts your PC (EFI)", "Recovery", "Reserved"
- Recycle Bin size limit

**Largest items view**
- Top-N files and folders across selected drives, from the service's rollups.
- Can be filtered by drive, type and age.
- A "folder vs. its contents" drill-down.
- Acting on an item (open location, recycle, move) reuses the M4 engine.

**Exit:** the Drives view matches `Get-Disk`/`Get-Partition` output on this PC and on attached VHDs.

### M4 — Safe file-operation engine

**Engine**
- All copy, move, rename, create, recycle and delete operations go through `IFileOperation` with a custom `IFileOperationProgressSink`.
- Operations are queued so several can run at once, each with pause/cancel and a progress entry in the activity flyout.

**Recycle guard (Q12/Q13)**
- Recycle sets `FOFX_RECYCLEONDELETE` with `FOF_ALLOWUNDO`.
- In `PreDeleteItem`, if `TSF_DELETE_RECYCLE_IF_POSSIBLE` is **not** set, the item would be permanently deleted. Return `HRESULT_FROM_WIN32(ERROR_CANCELLED)` for that item and add it to a "Couldn't go to Recycle Bin" list.
- Before starting, pre-check sizes against each volume's Recycle Bin limit (`HKCU\...\BitBucket\Volume\{GUID}\MaxCapacity`) and whether the drive is removable, so the user is warned up front.
- After the run, a dialog lists each blocked item with its size and the reason, and offers **Cancel** or **Permanently delete these**. Permanent deletion is a separate, explicitly confirmed operation.
- **A unit test with a fake sink must prove that no permanent deletion can happen without that confirmation.**

**Conflicts and bulk actions**
- Conflict dialog with Replace / Skip / Keep both, each with "apply to all".
- Bulk actions on multi-selection or search results: recycle, move to, copy to, rename (pattern with preview), and apply category (after M5).
- **Clone selected files/folders** = "Duplicate here" or "Copy to…", keeping timestamps and attributes.

**Exit:** `Dpt.Shell.Tests` pass on temp folders and an attached removable VHD (the no-Recycle-Bin case). Undo works for moves and renames.

### M5 — Categories and rules

**Data model**
- `Category(id, name, color, parentId?)`.
- `ItemTag(volumeSerial, fileRef, pathSnapshot, categoryId)`. Tags are keyed by NTFS FRN, so they survive renames and moves; non-NTFS volumes fall back to the path.
- Categories can be combined; `cat:` filters in search use AND/OR.

**Rules**

| Rule part | Contents |
|---|---|
| Conditions | A tree of AND/OR clauses over name pattern, extension or type group, size, created/modified dates, path under, existing category, and age. |
| Actions | Assign category and/or **move to a template path**, e.g. `E:\Media\Images\{year}\{ext}`. Tokens: `{year} {month} {ext} {type} {category} {name}`. |
| Trigger | Manual or Automatic (per rule). |
| Scope | Existing files, new files, or both (per rule). |
| Conflict policy | What to do when the destination already has a file with that name. |

- Rules that move files require a match preview before their first run (enforced). The preview is a tree of the planned moves, with counts and sizes and flags for collisions.
- **Automatic rules** are triggered by the service's change feed, which the app subscribes to over the pipe. They run in the app, as the user. A "keep running in the system tray at sign-in" setting (default on) lets automatic rules run while the window is closed; otherwise they catch up at the next launch.
- Every rule run is journaled and can be undone as a batch.
- Rule editor UI: a sentence-style builder ("When a **file** in **Downloads** has **type image**, **tag** *Photos* and **move to** …"), with a live "N files match now" count.

**Exit:** rule-engine unit tests (Core) and an end-to-end test in a temp folder (new file arrives → automatic rule tags and moves it → undo restores it) pass.

### M6 — Folder-structure experiments (Q35/Q36)

- A **Layout** is a scope (folders or drives), an ordered list of path templates (the same tokens as rules, plus `{category}`) and a fallback bucket for files that don't match.
- **Preview** shows a virtual tree with folder counts and sizes, collisions, unmatched files and files that won't move, without touching disk.
- Two or three layouts can be compared **side by side**. An "Apply" step generates a move plan, runs it through M4 in batches and journals it so it can be undone.
- A saved layout can be turned into rules for new files with one click.

**Exit:** layout planner unit tests pass, and on a sample tree of about 10k generated files the preview matches the result after applying.

### M7 — Disk worker foundation and partition management

**Disk worker foundation**
- DiskWorker protocol: the app builds a `DiskPlan` (JSON describing each step, the target disk's ID and serial, and the expected layout) and shows the preview. When the user confirms, the app launches `Dpt.DiskWorker.exe --plan <file>` with the `runas` verb (UAC prompt).
- Before every write, the worker checks the target disk again (ID, serial, size and partition table hash). If anything differs, it aborts. It streams progress back over a pipe.
- **VHD test harness** (`tools/vhd-fixtures.ps1` plus a C# fixture using the Windows virtual-disk API, which is available on Windows 11 Home): creates, attaches and detaches test VHD/VHDX files containing GPT layouts, NTFS/exFAT volumes and sample data.

**Partition operations** (all through the Storage WMI API)
- create a partition in unallocated space
- delete
- shrink/extend (NTFS)
- format (NTFS/exFAT/FAT32)
- change drive letter or label

Each operation shows a before/after bar diagram, a plain-language "what will happen to the data" statement and an undo note ("cannot be undone" where true).

**Protections**
- System, boot, EFI, recovery and pagefile partitions are locked unless an "Advanced: allow system partitions" setting is on, and even then extra confirmation is required.
- Destructive operations require typing the drive letter or disk number.
- MBR↔GPT conversion and moving partitions are **out of V1** (record this in the scope doc).

**Exit:** integration tests run every operation against VHDs, and DiskWorker refuses non-virtual disks while running in test mode.

### M8 — Whole-drive clone

**Spike first (ADR):** a block-level clone of a *live* VHD-based Windows system booted in VirtualBox.

**Choosing the destination (answers the open question)**
- The picker lists physical disks other than the source, showing model, bus, size, current volumes and labels, used space and "contains Windows" / "contains data" flags.
- The disk Windows booted from can never be a destination.
- The user must type the destination's disk number to confirm.
- The destination disk is set **offline** while it is being written.

**How the clone works**
1. Recreate the source's GPT/MBR layout on the destination, with **new** disk and partition GUIDs so the two disks don't collide when both are attached.
2. For each NTFS volume, take a **VSS snapshot** and copy **only the used clusters** (from `FSCTL_GET_VOLUME_BITMAP`) from the snapshot device to the matching destination partition.
3. Copy other partitions (EFI FAT32, recovery) raw.
4. If the destination is larger, extend the last data partition (setting: or leave the space unallocated).
5. If the destination is smaller than the source disk but large enough for the used data, copy the affected data partition file-by-file through the M9 capture/apply path instead of block-by-block.

**Making it bootable:** when the source holds Windows, run `bcdboot <dest>\Windows /s <destEFI> /f UEFI` (or `/f BIOS` and set the partition active on MBR disks). Then run a verification step: the file systems mount, BCD points at the destination, and the sizes match.

**Plain-language outcome screen:** "Two identical copies now exist. If you swap drives, set the new one as first in your PC's boot menu (BIOS/UEFI). Keep only one connected the first time you start from it."

**Exit:** a test VHD with Windows is cloned, and the cloned VHD boots to the Windows login screen in VirtualBox (`tools/boot-test.ps1`). Data-disk clones are byte-verified on used files.

### M9 — Windows-only copy to another drive, plus boot repair

**Spike first (ADR):** capture from a VSS snapshot of the live C: with **DISM `/Capture-Image`** using an exclusion config, then `/Apply-Image` to the destination. DISM preserves NTFS permissions, hard links (important for WinSxS), reparse points and alternate data streams. If DISM can't capture from a shadow copy reliably, fall back to bundling **wimlib** (LGPL), which supports `--snapshot` capture natively.

**Windows-content classifier** (in `Dpt.Core`, also used by the Drives view; answers the open question about identifying system files, apps and personal data)

| Group | What it contains |
|---|---|
| Windows itself | `\Windows`, boot files, `\ProgramData\Microsoft`, the essential parts of user profiles (`NTUSER.DAT`, AppData skeleton) |
| Installed programs | `Program Files*`, `ProgramData`, and InstallLocation from the Uninstall registry keys, each mapped to its program |
| Your account settings | Per-user AppData |
| Your personal folders | Each Known Folder (Documents, Pictures, Desktop, Downloads, Videos, Music…), per user |
| Other | Root-level folders and anything else |
| Always excluded | `pagefile.sys`, `hiberfil.sys`, `swapfile.sys`, `System Volume Information`, `$Recycle.Bin` |

**Options screen (Q16–Q19)**
- One checkbox group per category, with sizes. Neutral consequence notes, for example: "Programs not copied will remain listed in Settings but won't open until reinstalled."
- **Preview** shows what will be copied, what stays only on the source (nothing is erased) and the destination layout.
- Offers to **copy the excluded personal data to another drive** first (a separate M4 copy job).
- Steps: build the destination GPT layout (EFI + MSR + Windows + Recovery) → capture → apply → `bcdboot` → verify.

**Boot repair helper** (scope: recurring boot issues)
- Read-only "Boot health" panel: lists BCD entries (`bcdedit /enum all` parsed into plain language), EFI partition present and its free space, and which disk the PC booted from.
- "Rebuild boot files for this Windows" action: runs `bcdboot` for a chosen Windows install onto a chosen EFI partition, with a preview.
- Anything deeper, such as an offline WinRE-based repair, is listed as **out of V1**.

**Exit:** a Windows-only copy from a VirtualBox-hosted Windows VHD boots in VirtualBox with the selected apps and folders present and the excluded ones absent. The source VHD's hash is unchanged.

### M10 — Integration and release

- Optional, opt-in setting "Open folders with DrivePartitionTool": registers `HKCU\Software\Classes\Folder\shell\open` and can be reverted with one click.
- Taskbar pinning guidance.
- Performance pass: cold start under 1.5 s, folder with 50k items under 300 ms.
- Crash and log file at `%LOCALAPPDATA%\DrivePartitionTool\logs`.
- `publish.ps1`: produces a self-contained single-folder build and installs or upgrades it via `--install-service`.

## Safety rules (written into AGENTS.md; agents must obey them during development)

1. **Tests and dev builds never write to a real disk.** DiskWorker has `DPT_SAFETY=virtual-only`, which is on by default in Debug builds and in every test run. In that mode it refuses any disk whose `BusType` is not `FileBackedVirtual`. Only Release builds can target physical disks, and only after a UAC prompt plus the typed confirmation.
2. Never run DiskWorker plans, `diskpart`, `bcdboot`, `format` or partition cmdlets against the host's real disks during development.
3. File-operation tests run only inside temp directories or attached test VHDs.
4. The index service pipe exposes no file-mutating commands.
5. Permanent deletion always requires the explicit confirmation dialog. The engine's API has no "silent permanent delete" path.

## Items to confirm later (not blockers; they affect M7–M9)

- The VirtualBox boot tests need **VirtualBox installed** (`winget install Oracle.VirtualBox`) and a **Windows 11 ISO** downloaded from Microsoft to build the test source VHD. The implementing agent will ask before installing or downloading anything.
- Final real-hardware use of clone and migration is the owner's decision at release time. It is not part of automated verification.

## Verification (end-to-end)

- `./build.ps1`: build plus all fast tests (Core, Data, Shell on temp dirs, App UI tests with FlaUI).
- Timing-sensitive `Category=Perf` tests run separately after the other non-elevated tests. `-MaxCpuCount 1` limits MSBuild to one worker in environments where parallel workers fail; `-NoUi` skips UI automation when no interactive desktop is available.
- `./build.ps1 -Elevated`, run in an admin shell: Index tests and DiskWorker integration tests, on VHDs only.
- `tools/boot-test.ps1`: M8 and M9 boot checks in VirtualBox.
- Manual smoke test per milestone:
  - Launch the app (`dotnet run --project src/Dpt.App`).
  - Browse with tabs, copy and paste a file to and from real File Explorer, and search for a partial name across drives.
  - Recycle a large file on a removable VHD and confirm the pause dialog appears.
  - Run a rule's preview, compare two layouts, and view the Drives panel against `Get-Disk` output.

For the unattended continuation while the owner cannot approve administrator prompts, use `docs/GOAL_NO_ADMIN.md`. It defines completion of the non-admin phase and records the elevated/boot checks that remain necessary for V1.
