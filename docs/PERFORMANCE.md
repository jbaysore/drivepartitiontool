# Non-admin performance measurements

The targets remain startup <1,500 ms, a complete 50,000-item folder load <300 ms, and the median first page from a million-entry catalog <100 ms. Timing-sensitive tests run alone after other tests. UI performance measures the shipped self-contained **ReadyToRun package**. `build.ps1` builds the Release test harness and publishes/verifies a package first, unless `DPT_TEST_APP_EXE` selects an existing package. No service or integration is installed. `-NoUi` skips desktop timing checks and publishing, and reports only search performance.

The real app emits opt-in JSON timings under its data directory's `logs/performance.jsonl` when `DPT_MEASURE_PERF=1`. Startup starts at OS process creation (including runtime initialization) and ends at WPF's first `ContentRendered`. Folder timing starts when navigation calls `LoadAsync`, includes complete enumeration, all row objects, natural sorting, binding and first render, and ends at dispatcher ContextIdle. Only row containers are virtualized; the item collection contains all 50,000 entries. The fixture checks that count and the first visible item. Catalog work stays enabled and is confined to the test root.

Environment: Windows 11 Home 10.0.26200, Intel Core i5-13600K (20 logical processors), about 31.8 GiB usable memory, local NTFS temporary files on the Windows SSD. Each sample launches a fresh process with a new app-data directory. OS/runtime filesystem caches cannot be flushed in this no-admin goal, and other desktop activity is not controlled; **these are fresh-process measurements, not cold-cache boot proof**. The folder fixture contains 50,000 newly created numbered files inserted in a permuted order; other folder mixes, drives and network latency need additional owner measurements.

Initial navigation baseline: **396.9, 334.3, 360.7, 358.4, 353.6 ms**, all over target (`.local/performance-navigation-uia.log`). An earlier opening-at-startup measurement also included unfinished startup and is retained as diagnostic evidence, not the independent folder-navigation comparison. A keyboard-based attempt refused input when the app could not gain foreground focus; the final fixture uses normal accessible address controls.

After increasing enumeration buffers to 64 KiB and caching culture-aware numeric name sort keys, the same full navigation measurement was **289.5, 269.7, 281.9, 269.7, 264.3 ms**, all under target. Fresh-process startup in that run was **801.8, 614.2, 602.5, 608.4, 606.5 ms**, all under target (`.local/performance-sortkeys.log`). Sort-key tests cover multiple numeric runs, long numeric strings, accented names and invalidation after rename. Numeric collation uses the current .NET culture; exact punctuation/tie ordering can differ from Shell's undocumented `StrCmpLogicalW` ordering. No numeric parsing overflow or ASCII-only ordering is introduced. See [.NET sort keys](https://learn.microsoft.com/en-us/dotnet/api/system.globalization.compareinfo.getsortkey?view=net-10.0).

Reproduce from a normal, interactive PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./build.ps1
# Or run only measurements after a Release test build and publish:
$env:DPT_TEST_APP_EXE = '<verified package directory>\DrivePartitionTool.exe'
$env:DPT_SAFETY = 'virtual-only'
dotnet test tests/Dpt.Index.Tests --no-build --filter 'Category=Perf&Category!=Elevated' --logger 'console;verbosity=detailed'
dotnet test tests/Dpt.App.UITests -c Release --no-build --filter 'Category=Perf&Category!=Elevated' --logger 'console;verbosity=detailed'
```

An integrated run subsequently measured **312.2, 277.6, 279.7, 271.9, 261.0 ms** and correctly failed the folder assertion on the first sample (`.local/non-admin-final-tests.log`). Initial row creation and sorting were then moved to the background so folder-view rendering can overlap preparation; refreshes retain their existing row identities and selection. That integrated run's isolated million-entry search median was **89.7 ms** (samples 89.7, 90.9, 90.8, 87.2, 64.4), preserving its original median-under-100-ms assertion.

After background preparation, full-folder samples were **217.2, 232.5, 251.5, 236.6, 236.9 ms**, and startup samples were **771.6, 629.5, 678.9, 683.2, 661.2 ms**, all within their unchanged thresholds (`.local/performance-background-preparation.log`).

Another complete run passed all 270 functional tests and the search target (median **76.8 ms**) but the framework-dependent Release folder test again failed its first sample (**316.7**, 252.9, 239.1, 248.4, 220.3 ms). This result is retained in `.local/non-admin-verified-tests.log`. The final performance phase uses the actual ReadyToRun package, with the same thresholds, complete listing/render workload, catalog work and bundled-runtime assertion; source/JIT-build measurements are not silently relabeled as production-package results.

Final full-suite/package results are recorded in `PROGRESS.md`; failed measurements retain their target assertions and are not counted as passed by later documentation changes.

The current ReadyToRun candidate passed every sample: startup **755.0, 622.0, 618.6, 614.3, 618.9 ms** and full-folder navigation **210.3, 209.5, 219.2, 206.6, 210.1 ms** (`.local/readytorun-performance.log`). The harness confirmed its bundled CLR with external-runtime lookup disabled. The full script integration and clean-source package are recorded separately in `PROGRESS.md`.
