# Project working agreements

- Commit and push all changes made for the task directly to `main` without waiting for further instruction, unless the user explicitly says not to commit or push. Pull requests are not required; create one only if the user explicitly requests it.
- Include the commit hash in the final summary message.

# Project context

- Windows-only personal file browser + drive tool. Scope and decisions: `PROJECT_SCOPE.md`
  (moves to `docs/` in M0). Implementation plan: `docs/IMPLEMENTATION_PLAN.md` (added in M0).

# Safety rules (mandatory)

1. **Tests and dev builds never write to a real disk.** DiskWorker has `DPT_SAFETY=virtual-only`, which is on by default in Debug builds and in every test run. In that mode it refuses any disk whose `BusType` is not `FileBackedVirtual`. Only Release builds can target physical disks, and only after a UAC prompt plus the typed confirmation.
2. Never run DiskWorker plans, `diskpart`, `bcdboot`, `format` or partition cmdlets against the host's real disks during development.
3. File-operation tests run only inside temp directories or attached test VHDs.
4. The index service pipe exposes no file-mutating commands.
5. Permanent deletion always requires the explicit confirmation dialog. The engine's API has no "silent permanent delete" path.
