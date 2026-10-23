using Dpt.Core.WindowsCopy;

namespace Dpt.Core.Tests;

public class WindowsRecoveryPlanningTests
{
    [Fact]
    public void Recovery_space_includes_image_and_servicing_space_before_any_destination_write()
    {
        Assert.Equal(1024, WindowsRecoveryPlanning.PartitionSizeMb(null, 1024));
        Assert.Equal(1151, WindowsRecoveryPlanning.PartitionSizeMb(900 * WindowsRecoveryPlanning.Mb + 1, 1024));
        Assert.Equal(2048, WindowsRecoveryPlanning.PartitionSizeMb(100, 2048));
        Assert.Throws<ArgumentException>(() => WindowsRecoveryPlanning.PartitionSizeMb(-1, 1024));
        Assert.Throws<ArgumentException>(() => WindowsRecoveryPlanning.PartitionSizeMb(20_000 * WindowsRecoveryPlanning.Mb, 1024));
        var options = new WindowsCopyOptions { SourceRoot = @"X:\" };
        Assert.Equal(300, options.EfiSizeMb);
        Assert.Throws<ArgumentException>(() => WindowsCopyPlanning.Validate(options with { EfiSizeMb = 260 }));
    }

    [Fact]
    public void Attribute_script_has_only_the_selected_partition_and_required_no_letter_bits()
    {
        Assert.Equal("select disk 4\r\nselect partition 3\r\ngpt attributes=0x8000000000000001\r\nexit\r\n", WindowsRecoveryPlanning.AttributeScript(4, 3));
        Assert.Throws<ArgumentException>(() => WindowsRecoveryPlanning.AttributeScript(-1, 3));
        Assert.Throws<ArgumentException>(() => WindowsRecoveryPlanning.AttributeScript(4, 0));
        Assert.Contains("IN THE COPIED WINDOWS", WindowsRecoveryPlanning.ActivationInstructions(true));
        Assert.DoesNotContain("reagentc /enable", WindowsRecoveryPlanning.ActivationInstructions(false));
    }
}
