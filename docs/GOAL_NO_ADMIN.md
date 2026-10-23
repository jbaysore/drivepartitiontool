# Goal prompt: finish the work that needs no administrator access

Continue implementing DrivePartitionTool in `C:\Users\Joshua\Projects\DrivePartitionTool` until all remaining work that can be implemented and verified without administrator access is complete. Follow `AGENTS.md`, `docs/PROJECT_SCOPE.md`, `docs/IMPLEMENTATION_PLAN.md`, the ADRs, and the current evidence in `docs/PROGRESS.md`. Inspect the current Git state first; build on the existing work.

This goal covers the following work:

1. Finish the M9 Windows-copy options and preview UI: required Windows content, selectable programs, account settings and personal folders, sizes, neutral consequences, source/destination selection, destination layout, typed confirmation, progress and results. Offer copying excluded personal data elsewhere through the existing file-operation engine. Audit the existing classifier and worker before connecting them, including required Windows/profile content, `ProgramData\Microsoft`, custom program locations, exclusion validation, capacity checks and cleanup after failures. Preserve the source and every existing disk protection.
2. Implement the M9 boot-health panel and boot-repair preview/worker path. Show available read-only information, and explain when information needs administrator access. Verify parsing, planning, protection checks and UI with fixtures and fakes; never execute the repair during this goal.
3. Complete the M10 work that needs no administrator access: opt-in/reversible folder-association support, taskbar guidance, logging/crash handling, performance improvements, and a self-contained publish script/package with documented service install/upgrade steps. Test registry integration through isolated test keys or fakes. Produce and inspect the package locally; do not install the service or enable folder associations on this PC.
4. Review the existing everyday workflows and their integration, fix concrete defects, and add meaningful non-admin coverage. Run `build.ps1` (including UI tests when an interactive desktop is available), and keep the separate performance-test phase with the existing targets. Measure the planned cold-start, large-folder and search targets; record actual results and limitations. Use temporary directories and isolated app data for file-operation and UI tests.

Work autonomously on coding and implementation decisions. Use only the current subscription integration for AI work; stop on subscription limits. Commit and push task changes directly to `main`, following the shared-workspace rules. Update progress and decisions as the implementation changes.

## Boundaries for unattended work

- Do not request or trigger UAC, launch elevated helpers, install/remove services or software, download a Windows ISO, or spend money. If one task needs those actions, record it and continue other work.
- Do not attach/detach virtual disks or execute mutating disk-worker plans, DISM capture/apply, `bcdboot`, formatting or partition mutations during this goal. Prepare their implementation and tests without running them. Read-only probes and pure refusal/protection checks remain allowed. Never target host disks with a development write operation.
- Keep `DPT_SAFETY=virtual-only` in all tests, including Release tests. The main app stays unelevated, the index pipe has no file-mutating commands, and permanent deletion still requires explicit confirmation.
- Do not weaken assertions or safety checks to obtain passing tests. Distinguish implementation, non-admin verification, elevated VHD verification and demonstrated bootability.

## Completion criteria

- The implementation and non-admin verification above are finished, with relevant build/test/performance results recorded accurately. Any environment-limited check has a specific reason and reproducible follow-up command.
- `docs/PROGRESS.md` lists the remaining administrator/owner checks explicitly: the unrun Windows-copy VHD test, new disk/boot-repair tests, M8/M9 VirtualBox boot checks, service installation/upgrade, and the owner's everyday-use check. Reconcile known plan deviations, including smaller clone destinations and the missing recovery partition, without treating deferred checks as passed.
- M8/M9 and V1 are not described as fully verified while their boot/elevated exit criteria remain open. The final report separates completed work from that deferred verification.
- Task changes are committed and pushed to `main`; report the commit hash, verification results, package location and remaining follow-up checks. Completing this goal means the non-admin phase is complete, not that all V1 release criteria have passed.
