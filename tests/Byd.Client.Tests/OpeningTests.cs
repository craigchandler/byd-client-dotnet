using System.Text.Json.Nodes;
using Xunit;

namespace Byd.Client.Tests;

public class OpeningTests
{
    private static VehicleStatus Parse(string json) => VehicleStatus.Parse(JsonNode.Parse(json)!, DateTimeOffset.UtcNow);
    [Fact]
    public void Door_zero_is_closed_but_window_zero_is_unknown()
    {
        var status = Parse("""{"leftFrontDoor":0,"rightFrontDoor":0,"leftRearDoor":0,"rightRearDoor":0,"leftFrontWindow":0,"rightFrontWindow":0,"leftRearWindow":0,"rightRearWindow":0,"backCover":0}""");
        Assert.True(status.Openings.DoorsKnown); Assert.Equal(0, status.Openings.OpenDoors);
        Assert.False(status.Openings.WindowsKnown); Assert.Equal(0, status.Openings.OpenWindows); Assert.False(status.Openings.BootOpen);
    }
    [Fact]
    public void Passenger_door_window_boot_and_sunroof_open_states_are_preserved()
    {
        var status = Parse("""{"leftFrontDoor":1,"rightFrontDoor":0,"leftRearDoor":0,"rightRearDoor":0,"leftFrontWindow":1,"rightFrontWindow":1,"leftRearWindow":1,"rightRearWindow":2,"backCover":1,"skylight":2,"forehold":1}""");
        Assert.True(status.Openings.DoorsKnown); Assert.True(status.Openings.WindowsKnown);
        Assert.Equal(1, status.Openings.OpenDoors); Assert.Equal(1, status.Openings.OpenWindows);
        Assert.True(status.Openings.BootOpen); Assert.True(status.Openings.SunroofOpen); Assert.True(status.Openings.FrontCompartmentOpen);
    }
    [Fact]
    public void Missing_and_sentinel_opening_fields_do_not_become_closed()
    {
        var status = Parse("""{"leftFrontDoor":-1,"rightFrontDoor":1,"leftFrontWindow":-1,"rightRearWindowPct":0,"trunkLid":-1,"skylight":-1}""");
        Assert.False(status.Openings.DoorsKnown); Assert.False(status.Openings.WindowsKnown);
        Assert.Equal(1, status.Openings.OpenDoors); Assert.Equal(0, status.Openings.OpenWindows);
        Assert.Null(status.Openings.BootOpen); Assert.Null(status.Openings.SunroofOpen);
        Assert.Equal(VehicleOpenings.Unknown, Parse("{}").Openings);
    }
    [Fact]
    public void Positive_window_percentage_warns_even_when_enum_claims_closed()
    {
        var status = Parse("""{"leftFrontWindow":1,"leftFrontWindowPct":10,"rightFrontWindow":1,"leftRearWindow":1,"rightRearWindow":1}""");
        Assert.True(status.Openings.WindowsKnown); Assert.Equal(1, status.Openings.OpenWindows);
    }
    [Fact]
    public void Boot_alias_can_supply_a_valid_state_when_primary_is_unavailable()
    {
        Assert.True(Parse("""{"trunkLid":-1,"backCover":1}""").Openings.BootOpen);
        Assert.True(Parse("""{"trunkLid":0,"backCover":1}""").Openings.BootOpen);
    }
}
