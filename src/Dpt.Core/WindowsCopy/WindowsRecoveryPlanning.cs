namespace Dpt.Core.WindowsCopy;

/// <summary>Recovery preparation only. Activation must run in the copied Windows, never in the source/host OS.</summary>
public static class WindowsRecoveryPlanning
{
    public const long Mb = 1024 * 1024;
    public static int PartitionSizeMb(long? imageBytes, int requestedMb)
    {
        if (imageBytes < 0) throw new ArgumentException("The recovery image size is invalid.");
        var needed = imageBytes is { } bytes ? checked((bytes + 250 * Mb + Mb - 1) / Mb) : 1024;
        var result = Math.Max(requestedMb, Math.Max(1024, needed));
        if (result > 16384) throw new ArgumentException("The recovery image needs a partition larger than the supported 16 GiB limit.");
        return checked((int)result);
    }

    public static string AttributeScript(int disk, int partition)
    {
        if (disk < 0 || partition < 1) throw new ArgumentException("A destination disk and recovery partition are required.");
        return $"select disk {disk}\r\nselect partition {partition}\r\ngpt attributes=0x8000000000000001\r\nexit\r\n";
    }

    public static string ActivationInstructions(bool imageRegistered) => imageRegistered
        ? "DrivePartitionTool recovery preparation\r\nA recovery image was copied and registered against this offline Windows installation.\r\n" +
          "Recovery activation and bootability have not been verified. First boot this copied Windows with the source disconnected.\r\n" +
          "In an Administrator command prompt IN THE COPIED WINDOWS, run:\r\nreagentc /enable\r\nreagentc /info\r\n" +
          "Confirm the recovery location is on this disk, then verify Advanced startup in the test VM. Do not run these commands in the source/host Windows.\r\n"
        : "DrivePartitionTool recovery preparation\r\nRecovery space was reserved, but no readable Winre.wim was available.\r\n" +
          "Recovery tools are not registered or activated. Supply the matching Windows recovery image and verify recovery from the copied Windows in the owner/admin phase.\r\n";
}
