# 0004 — "Isolate Windows" = Windows-only copy to another drive

**Status:** Accepted (2026-10-22)

**Decision:** V1's "isolate Windows / remove non-Windows data" workflow builds a bootable copy of Windows on a different drive containing Windows itself plus the apps, account settings and personal folders the owner selects. The source drive is never modified. Before copying, the tool offers to copy excluded personal data to another drive. In-place cleanup of the running Windows drive is out of V1.

**Why:** Safest interpretation; nothing is erased, and the owner can boot the copy and decide later.
