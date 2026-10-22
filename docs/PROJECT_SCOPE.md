# DrivePartitionTool — Scoping Notes

**Started:** 2026-10-22  
**Status:** Scoped; implementation in progress (see `IMPLEMENTATION_PLAN.md`, `PROGRESS.md`)  
**Question limit:** 50 total

## Planning decisions (2026-10-22)

- V1 includes everything, including disk tools (clone, Windows migration, partitions); built in milestone order M0–M10.
- Search catalog is kept current by a small background Windows service (installed once with admin approval); the main app never runs elevated.
- Default start view is Home-first; a setting switches to Folder-first or "reopen last tabs".
- Disk tools are tested on virtual disks (VHD/VHDX) only, with VirtualBox for boot tests; real drives are never touched by tests.
- "Isolate Windows" means a bootable Windows-only copy to *another* drive (system + chosen apps/folders); the source is never modified. In-place cleanup is out of V1.
- Out of V1: MBR↔GPT conversion, moving partitions, in-place non-Windows cleanup, offline WinRE-based repair.
- Repo: private GitHub `jbaysore/drivepartitiontool`, commit and push to `main` per `AGENTS.md`.

## Project context

The project folder was empty at the start of this session. No source files, README, project instructions (`AGENTS.md`), or Git metadata were present. The project name suggests a drive partitioning tool, but its intended users, operating system, capabilities, workflow, and interface are not yet established.

## Goals and requirements

- A personal tool tailored to the owner's needs; no other users are currently planned.
- Aim to become the owner's everyday file browser, replacing regular File Explorer use.
- Cover both internal and external/USB physical drives.
- Make it much easier to understand where files are and how much storage they use across physical drives.
- Improve on native File Explorer search, which the owner finds poor.
- Allow searching with incomplete clues, including a single clue when that is all the owner has.
- Search through nested folders so the owner does not need to navigate the hierarchy manually.
- Support searching across selected drives or narrowing search to the current folder/location.
- Start home-view searches across all selected drives, while allowing scope to be narrowed during browsing.
- Make file and storage sizes readily visible.
- Show sizes for both individual files and whole folders, including nested contents.
- Provide a cross-drive view of the largest files and folders to identify major space users.
- Help the owner understand where items are saved, since their locations can be hard to track.
- Offer easy ways to perform bulk file operations, including deleting, creating, and cloning items; the exact meaning and scope of these actions still need clarification.
- Prefer sending deleted items to the Recycle Bin by default.
- Never permanently delete automatically when Windows cannot put an item in the Recycle Bin; pause and let the owner choose to cancel or permanently delete.
- Support creating files and folders.
- Support cloning selected files/folders and entire drives. A whole-drive clone should make an identical copy, potentially as a replacement or a second copy; bootability remains to be defined.
- Include partition management as an optional advanced feature.
- Treat Windows migration and boot repair as an intentional secondary use because the owner often has boot issues.
- Explore workflows to move or clone Windows to another drive, isolate Windows, or remove non-Windows data; exact meanings and safety boundaries remain to be defined.
- Let the owner configure what comes along when moving Windows, with explanations understandable to someone who does not know how Windows works internally.
- Present neutral, plain-language information and consequences so the owner can choose what suits the current situation; do not recommend a migration setup.
- Preview what will be copied, kept, or erased before Windows migration or cleanup actions.
- Offer to copy non-Windows data to another drive before a cleanup removes it.
- Help the owner build a personally satisfying file workflow through user-defined organization and categories.
- Keep the main experience centered on organizing files, finding large items, searching, and managing/moving/removing existing files.
- Treat configurability as a default: when a meaningful behavior can vary by user or situation, assume it should be a setting unless there is a clear reason otherwise.
- Support both virtual groupings and physical folder reorganization; a virtual-only system would not meet the longer-term preference.
- Allow categories to combine criteria such as project/topic, file type, status, date, and custom labels.
- Support both manually applied categories and automatic categorization through user-defined rules.
- Let each automatic rule independently specify whether it only categorizes items or also moves files.
- Preview which files match a moving rule before its first run.
- Let each rule choose whether it runs automatically or only when started manually.
- Expect rules to be used extensively for organizing new files, while letting the folder taxonomy evolve over time.
- Help the owner experiment with possible folder structures, since the best organization system is not yet known.
- Preview how alternative folder structures would organize existing files before moving anything.
- Keep the experience approachable for someone without partitioning knowledge.

## Functionality

- Personalized file-explorer experience across internal and external/USB physical drives.
- Core everyday jobs include organizing files, finding large items, searching, and managing/moving/removing existing files.
- Flexible recursive search and discovery across drives, using whichever clues are available and showing each result's location.
- Home searches cover all selected drives by default; searches can be narrowed to the current folder/location.
- Search results default to name, full location, drive, type, size, and modified date, with previews available on demand; these details are configurable.
- Searching inside file contents is optional and not yet a requirement.
- Readily visible file and storage-usage sizes.
- Folder sizes should include nested contents.
- Cross-drive largest-files-and-folders view for finding space users.
- Candidate bulk actions: delete, create, clone.
- Bulk deletion should use the Recycle Bin by default when possible.
- If Windows cannot recycle a selected item, pause and ask before any permanent deletion.
- Creating files and folders is a definite requirement.
- Cloning should cover both selected files/folders and full-drive copies.
- Full-drive cloning should produce a second identical copy, either for replacement or to keep two copies of the information.
- Windows migration/boot-repair workflows are an intentional secondary use: move or clone Windows to another drive, isolate Windows, or remove non-Windows data.
- Bootable cloning of the Windows drive is wanted.
- What moves with Windows should be configurable and explained in plain language.
- Explain options and consequences neutrally; the owner makes the choice for each situation.
- Preview what will be copied, kept, or erased before migration or cleanup actions.
- Offer a copy-to-another-drive option before removing non-Windows data.
- Windows-only cloning and removing non-Windows data need clearer definitions and safeguards.
- Provide user-defined ways to organize or categorize files as part of a personalized file workflow.
- Organization should support both virtual grouping and actually moving files into a preferred folder structure, potentially in phases.
- Preview how candidate folder structures would organize existing files before selecting/applying one.
- Categories should be flexible and combinable across topic, type, status, date, and user-defined labels.
- Support manual categorization and automatic category assignment through rules.
- Per-rule settings control whether matching files are only categorized or also moved.
- Moving rules preview their matching files before the first run.
- Rule execution timing is configurable per rule: automatic or manually started.
- Rule scope is configurable per rule: existing files, new files, or both.
- Partition management is an optional advanced feature and needs accessible explanations and safeguards.

## User workflow

The expected daily pattern is to open the app, quickly search across selected drives or jump to a pinned/frequently visited location, then browse or act on the results using familiar file-explorer behavior. The exact opening screen is still under UI discussion.

- Search from Home across selected drives using whichever clues are available, or narrow the search to the current folder while browsing.
- Browse folders in tabs, see file and folder sizes with other useful details, and open files/apps through their usual Windows behavior.
- Create, copy, move, rename, and recycle items as needed; use personal categories and rules to build an organization workflow over time.
- Review proposed rule matches and candidate folder structures before moving files.
- Use largest-item and drive views to understand storage, then enter optional advanced clone, partition, or Windows-migration flows with plain-language previews of consequences.

## Interface and experience

- The tool should function as an everyday file browser, not only a search and drive-management companion.
- Browser-style tabs can keep several folders or searches open at once.
- Opening a folder should replace the current tab's view, with an option to open it in another tab to keep the current location.
- Folder views should default to a detailed list with size, type, and modified date visible; optional thumbnails are useful for images and videos.
- Opening files and apps should behave as it does in File Explorer, including using the usual Windows association for files.
- Familiar File Explorer actions and shortcuts (copy, cut, paste, rename, and delete) should work as expected.
- A useful overview is a possible starting view, but the owner generally dislikes overviews unless they support practical tasks.
- The starting view should provide quick search to locate any file, plus separate shortcuts to frequently visited and manually pinned locations.
- File locations should use familiar Windows paths; do not add the physical device name to each file's path display.
- A separate physical-drive view showing capacity and Windows drive letters/volumes is tentatively useful.
- Use a standalone Windows desktop app with browser-style tabs; it should be easy to pin alongside or in place of File Explorer.

## Platform and technical constraints

- Target Windows only; the owner intends to use the tool only on Windows.
- This is intended as the owner's everyday file browser, with direct access to Windows drives and optional advanced partition management.

## UI discussion

- Numbered scoping questions paused at Q45 at the owner's request; the current focus is discussing and refining the interface.
- First-pass layout direction: a Windows desktop window with browser-style tabs, familiar back/forward/up navigation, a path/address bar, and a search field whose scope is clear.
- Two initial screen directions are available in `ui-preview.html` as a clickable standalone preview: Home-first, with search and quick locations up front; and Folder-first, with a familiar detailed folder browser as the opening view. `ui-first-pass.html` is the source fragment. Drive names, paths, and sizes shown are illustrative sample content.
- Use Harness as the primary UI inspiration: functional, clear, and well spaced, with readable text, restrained accent colors, and small signs of activity such as scan/progress indicators.
- Keep activity indicators useful and unobtrusive; show active scans or file operations when they are running.
- Keep Home practical rather than dashboard-heavy: prominent all-selected-drives search, separate pinned and frequently visited location lists, and modest access to drive capacity and largest items.
- Folder browsing should feel familiar, with an optional left navigation pane and a detailed file list; show size, type, and modified date by default, with a thumbnail view for media.
- Further UI choices should be shaped through visual examples and discussion rather than restarting the general scoping questionnaire.

## Concepts explained

- A partition is a logical section of a physical drive. On Windows, a partition commonly appears as its own drive letter (for example, C: or D:), even when both letters are on one physical drive.
- Partitions can separate operating systems, recovery data, or groups of data, but folders are usually enough for ordinary file organization.
- Creating, resizing, formatting, or deleting partitions can make data inaccessible or erase it, so partition operations would need clear explanations and safeguards if included.
- Whole-drive search looks recursively through filenames and file details on selected drives, so the owner does not need to know a file's folder. A searchable catalog can make repeated searches faster, although its first scan may take time. A current-folder search narrows the search to that folder and its nested folders. Searching inside file contents is a separate capability and is not currently required.
- Windows has a configurable Recycle Bin size limit for each drive/volume. An item that exceeds its drive's limit may bypass the bin and be permanently deleted after confirmation; older deleted items can also be removed when the bin fills. Some removable devices may not use the Recycle Bin. The bin is not a guaranteed recovery location for very large files.
  - Reference: [Microsoft Recycle Bin size policy](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-windowsexplorer), [Microsoft Q&A on oversized items](https://learn.microsoft.com/en-us/answers/questions/5692969/i-accidently-deleted-a-folder-and-it-did-not-go-to), and [Microsoft Q&A on per-drive limits](https://learn.microsoft.com/en-gb/answers/questions/5901959/my-recycle-bin-says-its-has-3-11gb-but-when-i-am-i).

## Decisions and assumptions

- Keep this document as the running record of scope, decisions, constraints, and open items.
- Ask one question at a time and adapt each next question to the answers so far.
- The owner is the sole intended user.
- The project is an everyday file explorer with drive mapping and management capabilities.
- Assume meaningful behaviors should be configurable; do not ask for confirmation on each individual setting.

## Open questions

- What should the physical-drive view show beyond capacity and drive letters/volumes?
- How should cleanup identify Windows/system files, installed apps, and personal data, and what should be kept or removed?
- How should clone destinations be selected?
- What Windows migration options and safeguards should be offered in plain language?
- How should rule creation and alternative folder-structure previews be presented?
- Which browsing layout and navigation details feel most natural in the UI?

## Question log

### Q1 — Answered

What should DrivePartitionTool ultimately help someone accomplish?

**Answer:** It is for the owner alone and should be an in-depth mapping and management tool for their physical drives.

### Q2 — Answered

Which changes or management actions do you expect to perform on a drive from the tool?

**Answer:** The owner is not familiar with partitioning and imagines a better, more personalized file explorer. It should make it easy to tell where things are and how much storage they use, and provide easy bulk delete and create actions, cloning, and partitioning if that proves useful.

### Q3 — Answered

What do you find most frustrating or time-consuming about managing your files today?

**Answer:** It is hard to tell where some things get saved, native File Explorer search is poor, and file size is not always readily available.

### Q4 — Answered

When you cannot find a file, what do you usually know about it (for example, part of its name, what it contains, its type, an approximate location, or when you last used it)?

**Answer:** There is no consistent clue. The owner may have some or all of the information, or only one piece, and wants to work from whatever is available.

### Q5 — Answered

Would you want search to look inside file contents too, or only search names and file details?

**Answer:** Search must traverse nested folders. Searching inside the contents of files is not necessarily needed.

### Q6 — Answered

When checking storage use, do you want sizes for individual files, whole folders including their contents, or both?

**Answer:** Both individual file sizes and whole-folder sizes.

### Q7 — Answered

Would a cross-drive view of the largest files and folders be useful for spotting what is consuming the most space?

**Answer:** Yes, definitely.

### Q8 — Answered

Which types of physical drives should it scan and manage—internal drives, external or USB drives, or both?

**Answer:** Both internal and external/USB drives.

### Q9 — Answered

When you say "create," what do you have in mind—new files or folders, new partitions or storage areas, or something else?

**Answer:** Creating new files and folders is definitely wanted. The owner is unfamiliar with partitions and wants a brief explanation before deciding whether to include partition management.

### Q10 — Answered

After the partition explanation, should partition management be an optional advanced feature, or should the app focus on files and folders for now?

**Answer:** The owner thinks partition management could be useful, so include it as an optional advanced feature.

### Q11 — Answered

When you say "clone," do you mean duplicating selected files or folders, copying an entire drive, or both?

**Answer:** Both selected files/folders and entire drives.

### Q12 — Answered

For bulk deletes, should the default move items to the Recycle Bin, with permanent deletion as a separate deliberate action?

**Answer:** Yes, with a concern about the Recycle Bin's size limit and how very large items (such as a 100 GB file) would be handled.

### Q13 — Answered

If Windows cannot send an item to the Recycle Bin because it is too large or the drive does not support it, should the app pause and let you cancel or choose permanent deletion instead of proceeding automatically?

**Answer:** Yes. Pause and let the owner cancel or explicitly choose permanent deletion.

### Q14 — Answered

What do you picture happening when you clone an entire drive: make an exact copy that can replace it, copy its files onto another drive, or something else?

**Answer:** The owner pictures an identical copy that could replace the source or create two copies of the same information.

### Q15 — Answered

If you clone the drive that contains Windows, should the new drive be ready to start the PC if you swap it in?

**Answer:** Yes. Bootable Windows cloning/migration is an intentional secondary function, alongside possible ways to isolate Windows or remove non-Windows data, motivated by recurring boot issues.

### Q16 — Answered

When moving Windows to another drive, what should come along: installed apps and settings, personal files, or only the pieces needed for Windows to run?

**Answer:** The transfer should be configurable, and its choices should be understandable without requiring the owner to know how Windows works.

### Q17 — Answered

Would you like the tool to recommend a setup and explain it in plain language, while still letting you customize what gets moved?

**Answer:** No recommendation is needed; the tool should provide enough information to let the owner decide what suits their needs at the time.

### Q18 — Answered

Before a migration or cleanup action, would a plain-language preview of what will be copied, kept, or erased be useful?

**Answer:** Yes.

### Q19 — Answered

Before wiping non-Windows data, should the tool offer to copy that data to another drive first?

**Answer:** Yes.

### Q20 — Answered

Before cleanup, should the tool show simple categories such as Windows/system files, installed apps, and personal files for you to review?

**Answer:** Yes. The owner also wants to create their own organization or categories and build a file workflow that suits them.

### Q21 — Answered

Should your own categories group files virtually across existing folders, change where files are stored, or support both?

**Answer:** Both. Virtual groupings are useful, but the owner also wants to reorganize actual folders, possibly over time.

### Q22 — Answered

What would you want to base categories on—for example, projects or topics, file type, status, date, or your own labels?

**Answer:** Any or all of those criteria, in whatever combination fits the owner's needs.

### Q23 — Answered

Should categories be applied manually, automatically through rules, or both?

**Answer:** Both manual and automatic categorization.

### Q24 — Answered

When a rule finds matching files, should it only categorize them, or also move them into chosen folders automatically?

**Answer:** It depends on each rule's settings; behavior should be configurable per rule.

### Q25 — Answered

For a rule that moves files, should the tool preview which files match before it runs for the first time?

**Answer:** Yes.

### Q26 — Answered

After you approve a rule, should it keep running automatically or only run when you start it?

**Answer:** Execution timing should be configurable per rule.

### Q27 — Answered

Should a rule apply to files already on the drives, new files as they appear, or both?

**Answer:** This should be configurable per rule, consistent with the general preference for configurable behavior.

### Q28 — Answered

When you open the tool, what should the default starting view emphasize: browsing files/folders, searching, or an overview of drives and storage?

**Answer:** The owner is unsure and tentatively accepts an overview if it is meaningful. Their key jobs are organizing files, identifying large items, searching, and managing/moving/removing existing files.

### Q29 — Answered

If the app opens to an overview, what would you most want to see immediately—drive space, largest files/folders, useful locations, or quick ways into search and organization?

**Answer:** Quick search to locate any file, and separately quick access to frequently visited or pinned locations.

### Q30 — Answered; scope preference clarified with explanation

Should search start across all included drives, or in the current folder/drive?

**Answer:** The owner does not know yet and asked what searching a drive entails. Explained that whole-drive search looks recursively across selected drives, while current-folder search narrows scope; a searchable catalog can make repeated searches faster after the first scan.

### Q31 — Answered

Would you like search from the home view to start across all selected drives, with the option to narrow it to the current folder when browsing?

**Answer:** Yes.

### Q32 — Answered by default design choice

Which details should each search result show at a glance so you can recognize it—full folder path, size, modified date, file type, or a preview?

**Answer:** The owner delegated this choice. Use name, full location, drive, type, size, and modified date as defaults, make previews available on demand, and keep result details configurable.

### Q33 — Answered

Would you want a file's location to show the actual physical drive it is stored on, along with its familiar Windows path? For example: “Samsung SSD → C: → Users\\You\\Documents\\file.pdf.”

**Answer:** No. A familiar Windows path is sufficient at the file level.

### Q34 — Answered (tentative)

Would a separate physical-drive view showing each drive's capacity and its Windows drive letters/volumes be useful?

**Answer:** The owner tentatively thinks so.

### Q35 — Answered

What is one recurring file-organization task you would most like a rule to simplify—for example, sorting downloads by file type or moving older files into an archive?

**Answer:** No specific task or taxonomy has been chosen yet. The owner expects to use rules extensively for new files and is considering structures such as images grouped by format, by date, or beneath a broader media category.

### Q36 — Answered

Would it help to preview how alternative folder structures would sort existing files before choosing one or moving anything?

**Answer:** Yes.

### Q37 — Answered (later clarified)

Do you picture opening this as a separate desktop app, or using it from within File Explorer?

**Answer:** Initially undecided between a desktop app or browser experience; later clarified as a standalone Windows desktop app with browser-style tabs that can be pinned alongside or in place of File Explorer (Q45).

### Q38 — Answered

Would you want it to become your everyday file browser instead of File Explorer, or mainly use it for specific search, organization, and drive-management tasks?

**Answer:** The owner wants it to be the everyday file browser.

### Q39 — Answered

Would browser-style tabs for multiple open folders or searches be useful?

**Answer:** Yes.

### Q40 — Answered

Would a detailed list showing size, type, and modified date by default, with an optional thumbnail view for images/videos, work well?

**Answer:** Yes. Use a detailed list as the default and offer an optional thumbnail view for images and videos.

### Q41 — Answered

When you open a folder, should it replace the current tab's view, with a way to open folders in another tab when you want to keep your place?

**Answer:** Yes. Open folders in the current tab by default, with an option to use another tab.

### Q42 — Answered

When you open a file, should it launch in the usual Windows app associated with that file type?

**Answer:** It should behave as File Explorer does when opening an app or file.

### Q43 — Answered

Should familiar File Explorer actions and shortcuts—such as copy, cut, paste, rename, and delete—work the way you expect in this tool?

**Answer:** Yes.

### Q44 — Answered

Should we scope the first version specifically for Windows?

**Answer:** Yes. The owner intends to use the tool only on Windows.

### Q45 — Answered

Would you prefer a standalone Windows desktop app with browser-style tabs, so it feels browser-like and can be pinned alongside File Explorer?

**Answer:** Yes. Use a standalone Windows desktop app with browser-style tabs.
