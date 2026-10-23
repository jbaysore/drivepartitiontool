namespace Dpt.Ipc;

/// <summary>Read-only startup metadata. Clients cannot supply a command, path, store or arguments.</summary>
public sealed record BootConfigurationInfo(bool Available, string Output, string? Note);
