# 0002 — Privilege split: unelevated app, read-only index service, per-operation elevated disk worker

**Status:** Accepted (2026-10-22)

**Decision:**
- `Dpt.App` always runs unelevated, as the user. It performs every file operation and runs rules, so files keep normal ownership and drag-drop with other apps works.
- `Dpt.IndexService` runs as a LocalSystem Windows service so it can read NTFS MFT/USN data. Its named pipe is ACL'd to the owner's SID + Administrators and offers queries/status only. The single write it accepts is the validated "selected volumes" setting.
- `Dpt.DiskWorker` is launched with the `runas` verb (one UAC prompt) per disk operation, executes one pre-approved JSON plan, streams progress and exits.

**Why:** The owner chose a background service for instant search (no UAC per launch). Keeping the long-lived elevated component read-only and the destructive component short-lived minimizes risk.
