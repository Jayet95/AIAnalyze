using AIAnalyze.Analysis;
using AIAnalyze.Health;
using Xunit;

namespace AIAnalyze.Tests;

public class MemoryModulesTests
{
    private static MemoryModule Stick(string slot, double gb = 16, int? rated = 6000, int? configured = 6000, string part = "CMK32GX5M2B6000C36", string bank = "") =>
        new(slot, bank, MemoryModules.ChannelOf(slot, bank), gb, "DDR5", rated, configured, "Corsair", part);

    [Theory]
    [InlineData("DIMM_A1", "", "A")]
    [InlineData("DIMM_B2", "", "B")]
    [InlineData("A2", "BANK 1", "A")]
    [InlineData("A1_DIMM", "", "A")]
    [InlineData("ChannelB-DIMM0", "BANK 2", "B")]
    [InlineData("Controller1-ChannelA-DIMM0", "", "1A")]
    [InlineData("Controller0-ChannelA/B-DIMM0", "", "0A")]
    [InlineData("DIMM 0", "P0 CHANNEL A", "A")]
    [InlineData("DIMM0", "BANK 0", null)]
    public void Channel_is_read_from_the_many_slot_naming_styles(string slot, string bank, string? expected)
    {
        Assert.Equal(expected, MemoryModules.ChannelOf(slot, bank));
    }

    [Fact]
    public void Two_sticks_in_the_same_channel_warn_about_single_channel_mode()
    {
        var check = MemoryModules.Check([Stick("DIMM_A1"), Stick("DIMM_A2")], totalSlots: 4);

        Assert.Equal(FindingSeverity.Warning, check.Severity);
        Assert.Contains("single-channel", check.Summary);
        Assert.Contains("2 of 4 slots are empty", check.Evidence);
    }

    [Fact]
    public void Sticks_in_both_channels_pass()
    {
        var check = MemoryModules.Check([Stick("DIMM_A2"), Stick("DIMM_B2")], totalSlots: 4);

        Assert.Null(check.Severity);
        Assert.Contains("32 GB in 2 stick(s), 2 of 4 slots used", check.Summary);
    }

    [Fact]
    public void Separate_intel_controllers_are_not_mistaken_for_one_channel()
    {
        var check = MemoryModules.Check(
            [Stick("Controller0-ChannelA-DIMM0"), Stick("Controller1-ChannelA-DIMM0")], totalSlots: 4);

        Assert.Null(check.Severity);
    }

    [Fact]
    public void Running_below_the_reported_speed_suggests_xmp()
    {
        var check = MemoryModules.Check([Stick("DIMM_A2", configured: 4800), Stick("DIMM_B2", configured: 4800)], totalSlots: 4);

        Assert.Equal(FindingSeverity.Info, check.Severity);
        Assert.Contains("XMP or EXPO", check.Summary);
    }

    [Fact]
    public void A_single_stick_suggests_a_second_one_and_mixed_sticks_are_noted()
    {
        Assert.Contains("single channel", MemoryModules.Check([Stick("DIMM_A2")], totalSlots: 4).Summary);
        Assert.Contains("not identical",
            MemoryModules.Check([Stick("DIMM_A2"), Stick("DIMM_B2", gb: 8, part: "OTHER")], totalSlots: 4).Summary);
    }

    [Fact]
    public void Evidence_lists_slot_size_type_speed_and_model_without_serials()
    {
        var check = MemoryModules.Check([Stick("DIMM_A2", configured: 4800)], totalSlots: 2);

        Assert.StartsWith("DIMM_A2: 16 GB DDR5, 4800 MT/s (reports 6000), Corsair CMK32GX5M2B6000C36", check.Evidence[0]);
    }
}
