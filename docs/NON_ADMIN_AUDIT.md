# Non-admin goal evidence

Goal: `GOAL_NO_ADMIN.md`. This checklist tracks the whole goal; unchecked work remains required. No elevated or boot test is implied by a non-admin result.

- [x] M9 classifier: required Windows/profile subtrees, program locations, neutral consequences and sizes (offline registry metadata limitations disclosed).
- [x] M9 copy preflight: validated exclusions, source/destination identity and protection, typed confirmation, capacity and failure cleanup (pure/fake-platform tests; privileged execution deferred).
- [x] M9 Windows-copy options, preview, excluded-data copy, progress and results UI (view-model/temp-tree tests and read-only UI smoke test).
- [x] M9 boot-health read-only inventory/BCD parsing, unavailable-data explanations and UI (fixed read-only service query, fixture/pipe/UI tests).
- [x] M9 boot-repair preview and protected worker implementation, tested with fixtures/fakes (actual VHD repair/boot proof deferred).
- [x] M10 opt-in/reversible folder association, isolated-registry tests, no live association changed.
- [x] M10 taskbar and service install/upgrade guidance; logging/crash handling.
- [ ] M10 self-contained publish script, built and inspected local package.
- [ ] Performance: actual cold-start (<1.5 s), 50k-folder (<300 ms), 1M-search (<100 ms) measurements and any limitations.
- [ ] Everyday workflow review and meaningful non-admin regression/UI checks.
- [ ] Reconcile M8 smaller-destination and M9 recovery-partition deviations.
- [ ] Full non-admin build/test run, progress updated, explicit admin/owner follow-up commands.
- [ ] Task changes committed/pushed to main; final requirement-by-requirement completion audit.

Deferred by the goal: elevated VHD mutations and Windows-copy/boot-repair tests; M8/M9 Windows login-screen boot proofs; service install/upgrade execution; live folder-handler opt-in; owner's everyday-use check. Real host disks are never development write targets.

Evidence so far: `PROGRESS.md` M9 section, ADRs 0005/0006; complete non-admin build/test run: 238 passing tests, zero build warnings/errors (`.local/m9-boot-full-tests.log`). Elevated copy-test source is updated for the recovery layout and a boot-repair VHD test is prepared, but neither was run. The modal-test teardown race is resolved by waiting for the owner to be re-enabled; both modal tests passed afterwards without forced-exit warnings (`.local/m9-modal-close-tests.log`). General shutdown/logging review remains part of M10.
