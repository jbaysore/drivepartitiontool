# Project working agreements

- Commit and push all changes made for the task directly to `main` without waiting for further instruction, unless the user explicitly says not to commit or push. Pull requests are not required; create one only if the user explicitly requests it.
- Include the commit hash in the final summary message.

# Project context

- Windows-only personal file browser + drive tool. Scope and decisions: `docs/PROJECT_SCOPE.md`.
  Implementation plan (source of truth): `docs/IMPLEMENTATION_PLAN.md`. Progress and evidence per
  milestone: `docs/PROGRESS.md`. Decisions: `docs/decisions/NNNN-*.md` (short ADRs).
- C# / .NET 10, WPF app (`src/Dpt.App`), solution `DrivePartitionTool.sln`. `Dpt.Core` is pure
  logic (no I/O, `net10.0`); everything else targets `net10.0-windows`.

# Build and test

- `./build.ps1` — build + all fast tests (everything not tagged `[Trait("Category", "Elevated")]`).
- `./build.ps1 -NoUi` — same, skipping FlaUI tests (`Category=UI`).
- `./build.ps1 -Elevated` — from an Administrator shell: index and disk-worker tests on test VHDs only.
- Run the app: `dotnet run --project src/Dpt.App`.
- Tag tests that need admin with `[Trait("Category", "Elevated")]` and UI-automation tests with
  `[Trait("Category", "UI")]`.

# Safety rules (mandatory)

1. **Tests and dev builds never write to a real disk.** DiskWorker has `DPT_SAFETY=virtual-only`, which is on by default in Debug builds and in every test run. In that mode it refuses any disk whose `BusType` is not `FileBackedVirtual`. Only Release builds can target physical disks, and only after a UAC prompt plus the typed confirmation.
2. Never run DiskWorker plans, `diskpart`, `bcdboot`, `format` or partition cmdlets against the host's real disks during development.
3. File-operation tests run only inside temp directories or attached test VHDs.
4. The index service pipe exposes no file-mutating commands.
5. Permanent deletion always requires the explicit confirmation dialog. The engine's API has no "silent permanent delete" path.
