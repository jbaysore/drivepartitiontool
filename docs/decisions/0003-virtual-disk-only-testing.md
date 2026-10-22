# 0003 — Disk tools are tested on virtual disks only

**Status:** Accepted (2026-10-22)

**Decision:** All partition, clone and migration tests run against VHD/VHDX files created and attached with the Windows virtual-disk API (available on Windows 11 Home). Boot tests use VirtualBox. `DPT_SAFETY=virtual-only` (default in Debug builds and all test runs) makes DiskWorker refuse any disk whose bus type is not `FileBackedVirtual`.

**Why:** Owner chose virtual-disk-only testing; Windows 11 Home has no Hyper-V.

**Consequences:** Real-hardware use is the owner's decision at release time and is not part of automated verification.
