# Non-admin goal evidence

Goal: `GOAL_NO_ADMIN.md`. **Non-admin phase complete, 2026-10-23.** No elevated or boot test is implied by a non-admin result. V1 acceptance remains open for the checks in `OWNER_VERIFICATION.md`.

- [x] M9 classifier: required Windows/profile subtrees, program locations, neutral consequences and sizes (offline registry metadata limitations disclosed).
- [x] M9 copy preflight: validated exclusions, source/destination identity and protection, typed confirmation, capacity and failure cleanup (pure/fake-platform tests; privileged execution deferred).
- [x] M9 Windows-copy options, preview, excluded-data copy, progress and results UI (view-model/temp-tree tests and read-only UI smoke test).
- [x] M9 boot-health read-only inventory/BCD parsing, unavailable-data explanations and UI (fixed read-only service query, fixture/pipe/UI tests).
- [x] M9 boot-repair preview and protected worker implementation, tested with fixtures/fakes (actual VHD repair/boot proof deferred).
- [x] M10 opt-in/reversible folder association, isolated-registry tests, no live association changed.
- [x] M10 taskbar and service install/upgrade guidance; logging/crash handling.
- [x] M10 self-contained publish script, built and inspected final package from clean source `3d58af2`, 862 hashed payload files and three runtimes/privilege manifests; 22 additional packaged UI tests passed.
- [x] Performance: real fresh-process startup (<1.5 s), full 50k-folder (<300 ms) and 1M-search median (<100 ms) measured; cold-cache limitation and reproduction are explicit in `PERFORMANCE.md`/`OWNER_VERIFICATION.md`.
- [x] Everyday workflow review and meaningful non-admin regression/UI checks (file-operation confirmation/undo, rules/layouts, accessible navigation, job lifetime, clone/copy/boot previews and crash/registry isolation).
- [x] Reconcile M8 smaller-destination and M9 recovery-partition deviations in implementation and ADR 0008; privileged verification remains deferred.
- [x] Full non-admin build/test run: 274 passing tests, zero build warnings/errors; progress and explicit administrator/owner follow-up commands recorded.
- [x] Implementation committed/pushed to main; final requirement-by-requirement completion audit recorded and committed with this evidence update.

Deferred by the goal: elevated VHD mutations and Windows-copy/boot-repair tests; M8/M9 Windows login-screen boot proofs; service install/upgrade execution; live folder-handler opt-in; owner's everyday-use check. Real host disks are never development write targets.

Final evidence: `PROGRESS.md` completion summary, ADRs 0005–0008, `PERFORMANCE.md`, `.local/non-admin-active-final.log` and `.local/final-package-ui.log`. Startup 611–629 ms, full 50k-folder 202–237 ms with foreground validation, search median 78.6 ms. Cold-cache proof and other media/folder mixes remain owner checks; earlier failed timing runs are retained. The package is `artifacts/publish/DrivePartitionTool-0.1.0-win-x64-20261023-141034-9c13798c`. Elevated clone/copy/recovery/repair tests are prepared but unrun in this goal. Test cleanup requires normal process exit; forced termination never counts as a passing shutdown.

| Goal requirement | Completed non-admin evidence | Explicit deferred proof |
|---|---|---|
| Windows-copy options, preview, backup, progress/results | Classifier/planner, temporary-tree and view-model tests, platform fakes and read-only UI | Real capture/apply metadata, recovery and Windows startup |
| Boot health and protected repair path | Parser/pipe fixtures, shared protections, fake platform and UI | Real destination BCD writes and repaired VHD boot |
| Windows integration, lifecycle/crash, service guidance, package | Isolated registry/temporary copy/crash tests, normal-exit UI, 862-file package verification | Live Shell defaults/pinning, service install/upgrade/ACLs |
| Everyday workflows and performance | Full 274-test run plus 22 packaged UI checks; unchanged timing targets passed | Cold-cache/other-media measurements and owner everyday use |
| Smaller clone and recovery deviations | Shared layout/capacity logic, 12 clone fake/pure tests, recovery sizing/script/ordering tests, prepared VHD tests | Actual VHD metadata/attributes/activation, login/recovery and whole-source-VHD hashes |
| Handoff and repository state | Goal prompt retained, ADRs/progress/audit updated, direct main commits and pushes | V1 remains unaccepted until the above owner/admin work passes |
