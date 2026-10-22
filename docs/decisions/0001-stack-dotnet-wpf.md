# 0001 — C# / .NET 10 + WPF

**Status:** Accepted (2026-10-22)

**Decision:** Build in C# on .NET 10. UI is WPF with the built-in Fluent theme and CommunityToolkit.Mvvm. Win32/COM interop uses the CsWin32 source generator where practical and hand-written `[ComImport]` interfaces where CsWin32 output is awkward (shell COM sinks).

**Why:** First-class access to the Windows shell (`IFileOperation`, `IContextMenu`, thumbnails), WMI storage APIs, VSS and `DeviceIoControl`. WPF has mature list virtualization and no packaging requirements; WinUI 3 adds packaging friction; no Rust toolchain is installed.

**Consequences:** Windows-only (accepted in scope). `Dpt.Core` stays pure `net10.0` so its logic is trivially unit-testable.
