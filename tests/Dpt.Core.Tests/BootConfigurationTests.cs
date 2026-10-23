using Dpt.Core.Boot;
using Dpt.Core.WindowsCopy;

namespace Dpt.Core.Tests;

public class BootConfigurationTests
{
    [Fact]
    public void Entries_keep_locations_continuation_values_and_unknown_fields()
    {
        var entries = BootConfiguration.Parse("""
            Windows Boot Manager
            --------------------
            identifier              {bootmgr}
            device                  partition=R:
            path                    \EFI\Microsoft\Boot\bootmgfw.efi
            description             Windows Boot Manager
            displayorder            {one}
                                    {two}
            custom                  retained

            Windows Boot Loader
            -------------------
            identifier              {one}
            device                  partition=W:
            path                    \Windows\system32\winload.efi
            description             Work Windows
            osdevice                partition=W:
            """);
        Assert.Equal(2, entries.Count);
        Assert.Equal("Chooses which Windows installation to start", entries[0].Meaning);
        Assert.Equal("{one} {two}", entries[0].Fields["displayorder"]);
        Assert.Equal("retained", entries[0].Fields["custom"]);
        Assert.Equal("Work Windows", entries[1].Title);
        Assert.Equal("partition=W:", entries[1].WindowsDevice);
        Assert.Equal("Starts a Windows installation", entries[1].Meaning);
    }

    [Theory]
    [InlineData("")]
    [InlineData("The boot configuration data store could not be opened. Access is denied.")]
    [InlineData("Localized output\n----------------\nBezeichner         {one}")]
    public void Uninterpreted_or_denied_output_does_not_imply_an_empty_healthy_store(string text) => Assert.Empty(BootConfiguration.Parse(text));

    [Theory]
    [InlineData(BootFirmware.Uefi, "UEFI")]
    [InlineData(BootFirmware.Bios, "BIOS")]
    public void Boot_arguments_always_name_the_exact_source_and_system_partition(BootFirmware firmware, string expected)
    {
        Assert.Equal([@"W:\Windows", "/s", "S:", "/f", expected], BootRepairPlanning.Arguments(new() { SourceRoot = @"W:\", Firmware = firmware }, @"S:\"));
        Assert.Throws<ArgumentException>(() => BootRepairPlanning.Arguments(new() { SourceRoot = @"W:\" }, "SYS"));
        Assert.Throws<ArgumentException>(() => BootRepairPlanning.Arguments(new() { SourceRoot = "W:\\ /s C:" }, @"S:\"));
    }
}
