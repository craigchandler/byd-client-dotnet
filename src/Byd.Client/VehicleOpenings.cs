using System.Text.Json.Nodes;

namespace Byd.Client;

/// <summary>Known open states and completeness for the four passenger doors/windows.</summary>
/// <param name="OpenDoors">Count of positively reported open passenger doors. Check DoorsKnown for completeness.</param>
/// <param name="OpenWindows">Count of positively reported open passenger windows. Check WindowsKnown for completeness.</param>
/// <param name="DoorsKnown">Whether all four passenger door states were understood.</param>
/// <param name="WindowsKnown">Whether all four passenger window states were understood.</param>
/// <param name="BootOpen">Reported boot opening state, or null.</param>
/// <param name="SunroofOpen">Reported sunroof opening state, or null.</param>
/// <param name="FrontCompartmentOpen">Reported front compartment opening state, or null.</param>
public sealed record VehicleOpenings(int OpenDoors, int OpenWindows, bool DoorsKnown, bool WindowsKnown,
    bool? BootOpen, bool? SunroofOpen, bool? FrontCompartmentOpen)
{
    /// <inheritdoc/>
    public override string ToString() => "Vehicle openings (telemetry redacted)";
    /// <summary>No known door/window or compartment states.</summary>
    public static VehicleOpenings Unknown { get; } = new(0, 0, false, false, null, null, null);
    internal static VehicleOpenings Parse(JsonNode node)
    {
        string[] positions = ["leftFront", "rightFront", "leftRear", "rightRear"];
        var doors = positions.Select(p => Door(node, p + "Door")).ToArray();
        var windows = positions.Select(p => Window(node, p + "Window", p + "WindowPct")).ToArray();
        var boot = Door(node, "trunkLid"); var bootAlias = Door(node, "backCover");
        return new(doors.Count(open => open == true), windows.Count(open => open == true), doors.All(open => open != null), windows.All(open => open != null),
            boot == true || bootAlias == true ? true : boot ?? bootAlias, Window(node, "skylight"), Door(node, "forehold"));
    }
    private static bool? Door(JsonNode node, string key) => VehicleStatus.Number(node, key) switch { 0 => false, 1 => true, _ => null };
    private static bool? Window(JsonNode node, string key, string? percentKey = null)
    {
        // A positive opening percentage is evidence of an opening even if the enum says closed.
        // Percentage zero alone is not proof of closure: some responses use zero-filled defaults.
        if (percentKey != null && VehicleStatus.Number(node, percentKey) is > 0 and <= 100) return true;
        return VehicleStatus.Number(node, key) switch { 1 => false, 2 => true, _ => null };
    }
}
