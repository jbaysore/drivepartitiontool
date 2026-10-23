using System.Text.Json.Serialization;

namespace Dpt.Ipc;

/// <summary>Identifies exactly which disk (and which partition layout) a plan was made for.</summary>
public sealed record DiskFingerprint
{
    public required int Number { get; init; }
    public required string UniqueId { get; init; }
    public string SerialNumber { get; init; } = "";
    public required long Size { get; init; }

    /// <summary>Hash of the partition table (offsets, sizes, types) the owner saw in the preview.</summary>
    public required string LayoutHash { get; init; }

    public string FriendlyName { get; init; } = "";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiskStepKind
{
    /// <summary>Read-only check (used by tests and to validate a plan without changing anything).</summary>
    Probe,
    InitializeDisk,
    CreatePartition,
    DeletePartition,
    ResizePartition,
    FormatVolume,
    SetDriveLetter,
    SetLabel,

    // M8/M9 (implemented there):
    CloneDisk,
    WindowsCopy,
    RebuildBootFiles,
}

/// <summary>One step of a disk plan. Fields used depend on <see cref="Kind"/>.</summary>
public sealed record DiskStep
{
    public required DiskStepKind Kind { get; init; }
    public int? PartitionNumber { get; init; }
    public long? Offset { get; init; }
    public long? Size { get; init; }

    /// <summary>"NTFS", "exFAT", "FAT32".</summary>
    public string? FileSystem { get; init; }

    public string? Label { get; init; }

    /// <summary>Drive letter to assign, or null to remove the letter (SetDriveLetter); "auto" when creating.</summary>
    public char? DriveLetter { get; init; }

    public bool AutoLetter { get; init; }

    /// <summary>"GPT" or "MBR" (InitializeDisk).</summary>
    public string? PartitionStyle { get; init; }

    /// <summary>Free-form JSON payload for complex steps (clone/migration options).</summary>
    public string? Options { get; init; }
}

/// <summary>A complete, previewed set of changes for one disk. The elevated worker executes exactly this, or nothing.</summary>
public sealed record DiskPlan
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required DiskFingerprint Target { get; init; }

    /// <summary>Source disk for clone/copy plans (re-verified, only read).</summary>
    public DiskFingerprint? Source { get; init; }

    public required IReadOnlyList<DiskStep> Steps { get; init; }

    /// <summary>The owner explicitly unlocked changes to partitions Windows needs to start.</summary>
    public bool AllowSystemPartitions { get; init; }

    /// <summary>Where the worker writes progress (JSON lines). The app tails this file.</summary>
    public required string StatusFile { get; init; }

    /// <summary>What the owner typed to confirm (must equal the expected confirmation text).</summary>
    public string? Confirmation { get; init; }

    public string? ExpectedConfirmation { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiskProgressState
{
    Started,
    Step,
    Progress,
    Succeeded,
    Failed,
    Refused,
}

/// <summary>A line in the status file.</summary>
public sealed record DiskProgress(DiskProgressState State, string Message, double Fraction = 0, int StepIndex = -1, DateTime? AtUtc = null);

/// <summary>Worker exit codes.</summary>
public static class DiskWorkerExit
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int BadArguments = 2;
    public const int NotElevated = 3;
    public const int RefusedSafety = 10;
    public const int RefusedFingerprint = 11;
    public const int RefusedConfirmation = 12;
    public const int RefusedProtected = 13;
}
