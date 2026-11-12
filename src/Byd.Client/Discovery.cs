namespace Byd.Client;

/// <summary>Typed resources implemented by the client, independently of account entitlement or server availability.</summary>
public enum BydResource
{
    /// <summary>Vehicles.</summary>
    Vehicles,
    /// <summary>Status.</summary>
    Status,
    /// <summary>Details.</summary>
    Details,
    /// <summary>Location.</summary>
    Location,
    /// <summary>Charging.</summary>
    Charging,
    /// <summary>Climate.</summary>
    Climate,
    /// <summary>Climate Bookings.</summary>
    ClimateBookings,
    /// <summary>Energy.</summary>
    Energy,
    /// <summary>Capabilities.</summary>
    Capabilities,
}
/// <summary>Whether a discovery entry describes readable data or a vehicle mutation.</summary>
public enum BydFeatureKind
{
    /// <summary>Readable Data.</summary>
    ReadableData,
    /// <summary>Control.</summary>
    Control,
}
/// <summary>Conservative interpretation of advertised flags. NotReported and Unknown do not prove a read is unsupported.</summary>
public enum BydFeatureAvailability
{
    /// <summary>A matching capability flag was advertised; operation is not live-verified.</summary>
    ReportedAvailable,
    /// <summary>No matching capability flag was reported; a read endpoint may still be usable.</summary>
    NotReported,
    /// <summary>Unavailable or unverified. For command outcomes, verify the vehicle before another attempt.</summary>
    Unknown,
}
/// <summary>Local resource metadata. Stable Id is intended for selection; ClientMethod is descriptive, not an invocation contract.</summary>
/// <param name="Resource">Typed library resource, or null for control descriptors.</param>
/// <param name="Id">Stable discovery identifier.</param>
/// <param name="Name">Human-readable catalog label.</param>
/// <param name="ClientMethod">Descriptive client method name; use the typed public API to invoke it.</param>
/// <param name="RequiresSignIn">Whether authentication is required before requesting the resource.</param>
/// <param name="MayWakeVehicle">Whether a read can wake the car; never a guarantee of wake-up behaviour.</param>
/// <param name="RequiresVehicleSelection">Whether a VIN from the current vehicle list is required.</param>
public sealed record BydResourceInfo(BydResource Resource, string Id, string Name, string ClientMethod, bool RequiresSignIn, bool MayWakeVehicle, bool RequiresVehicleSelection = true);
/// <summary>Bundled server region and account countries. This metadata does not assert live compatibility.</summary>
/// <param name="Id">Stable discovery identifier.</param>
/// <param name="Name">Human-readable catalog label.</param>
/// <param name="ApiBaseUrl">Bundled BYD HTTPS endpoint for this server region.</param>
/// <param name="Countries">Bundled account-country entries for this server region.</param>
/// <param name="ListableResources">Implemented library resources, independent of live server/account support.</param>
public sealed record BydRegionInfo(string Id, string Name, string ApiBaseUrl, IReadOnlyList<BydMarket> Countries, IReadOnlyList<BydResourceInfo> ListableResources);
/// <summary>Vehicle-specific advertised support and safety requirements; execution always rechecks support.</summary>
/// <param name="Id">Stable discovery identifier.</param>
/// <param name="Name">Human-readable catalog label.</param>
/// <param name="Kind">Readable data or a vehicle control.</param>
/// <param name="Availability">Conservative advertised support, not live certification.</param>
/// <param name="Resource">Typed library resource, or null for control descriptors.</param>
/// <param name="Command">Associated command type, or null.</param>
/// <param name="Seat">Associated comfort target, or null.</param>
/// <param name="RequiresSignIn">Whether authentication is required before requesting the resource.</param>
/// <param name="RequiresControlOptIn">Whether per-client control opt-in is required.</param>
/// <param name="RequiresConfirmation">Whether an exact fresh single-use confirmation is required.</param>
/// <param name="RequiresPin">Whether a locally entered control PIN is required.</param>
public sealed record BydFeatureInfo(string Id, string Name, BydFeatureKind Kind, BydFeatureAvailability Availability, BydResource? Resource = null, VehicleCommand? Command = null, SeatFeature? Seat = null,
    bool RequiresSignIn = true, bool RequiresControlOptIn = false, bool RequiresConfirmation = false, bool RequiresPin = false);
/// <summary>Feature discovery from one read-only configuration request; no PIN verification or vehicle-command dispatch.</summary>
/// <param name="Capabilities">Reported flags from the configuration request.</param>
/// <param name="Features">Read-only discovery entries, including inconclusive availability.</param>
/// <param name="RetrievedAt">UTC-compatible instant when the client retrieved/created the snapshot; not the vehicle measurement time.</param>
public sealed record VehicleFeatureCatalog(VehicleCapabilities Capabilities, IReadOnlyList<BydFeatureInfo> Features, DateTimeOffset RetrievedAt);

/// <summary>Discovery metadata only. Region/resource listing never authenticates or probes a server.</summary>
public static class BydDiscovery
{
    /// <summary>Version of the local discovery metadata shape.</summary>
    public const int SchemaVersion = 1;
    /// <summary>Pinned upstream revision of the country/server catalog.</summary>
    public const string RegionCatalogRevision = "hass-byd-vehicle:0dd8cbefa485e819e27c805129b4287928b7b18a";
    private static readonly IReadOnlyList<BydResourceInfo> Resources = Array.AsReadOnly(new[]
    {
        new BydResourceInfo(BydResource.Vehicles, "data.vehicles", "Vehicles in the account", nameof(BydClient.GetVehiclesAsync), true, false, false),
        new BydResourceInfo(BydResource.Status, "data.status", "Vehicle status", nameof(BydClient.GetStatusAsync), true, true),
        new BydResourceInfo(BydResource.Details, "data.details", "Temperature, odometer and tyres", nameof(BydClient.GetStatusAsync), true, true),
        new BydResourceInfo(BydResource.Location, "data.location", "Vehicle location", nameof(BydClient.GetLocationAsync), true, true),
        new BydResourceInfo(BydResource.Charging, "data.charging", "Charging and charging schedules", nameof(BydClient.GetChargingAsync), true, true),
        new BydResourceInfo(BydResource.Climate, "data.climate", "Climate and comfort readings", nameof(BydClient.GetClimateAsync), true, true),
        new BydResourceInfo(BydResource.ClimateBookings, "data.bookings", "Climate bookings", nameof(BydClient.GetClimateBookingsAsync), true, false),
        new BydResourceInfo(BydResource.Energy, "data.energy", "Energy statistics", nameof(BydClient.GetEnergyAsync), true, false),
        new BydResourceInfo(BydResource.Capabilities, "data.capabilities", "Reported feature availability", nameof(BydClient.GetVehicleFeaturesAsync), true, false)
    });
    private static readonly IReadOnlyList<BydRegionInfo> Regions = Array.AsReadOnly(BydMarkets.All.GroupBy(m => m.ApiBaseUrl).Select(group => new BydRegionInfo(
        new Uri(group.Key).Host.Split('.')[0].Replace("dilinkappoversea-", "", StringComparison.Ordinal), group.First().ServerRegion, group.Key,
        Array.AsReadOnly(group.ToArray()), Resources)).OrderBy(r => r.Name, StringComparer.Ordinal).ToArray());
    /// <summary>Lists bundled server regions and their account countries without authentication or network requests.</summary>
    public static IReadOnlyList<BydRegionInfo> GetRegions() => Regions;
    /// <summary>Lists resources implemented by this library without authentication or network requests.</summary>
    public static IReadOnlyList<BydResourceInfo> GetResources() => Resources;
    internal static VehicleFeatureCatalog Describe(VehicleCapabilities capabilities)
    {
        var features = new List<BydFeatureInfo>();
        foreach (var resource in Resources)
        {
            bool? advertised = resource.Resource switch
            {
                BydResource.Location => capabilities.Has("1014"), BydResource.Charging => capabilities.Has("1012"),
                BydResource.Climate or BydResource.ClimateBookings => capabilities.Has("1001", "10300001", "1015"), BydResource.Energy => capabilities.Has("1022"),
                _ => null
            };
            features.Add(new(resource.Id, resource.Name, BydFeatureKind.ReadableData, advertised switch { true => BydFeatureAvailability.ReportedAvailable, false => BydFeatureAvailability.NotReported, _ => BydFeatureAvailability.Unknown }, Resource: resource.Resource));
        }
        foreach (var command in Enum.GetValues<VehicleCommand>().Where(c => c != VehicleCommand.ChangeSeatClimate))
            features.Add(new("command." + command, CommandName(command), BydFeatureKind.Control, capabilities.Supports(command) ? BydFeatureAvailability.ReportedAvailable : BydFeatureAvailability.NotReported, Command: command, RequiresControlOptIn: true, RequiresConfirmation: true, RequiresPin: true));
        foreach (var seat in Enum.GetValues<SeatFeature>())
            features.Add(new("comfort." + seat, SeatName(seat), BydFeatureKind.Control, capabilities.SupportsSeat(seat) ? BydFeatureAvailability.ReportedAvailable : BydFeatureAvailability.NotReported,
                Command: VehicleCommand.ChangeSeatClimate, Seat: seat, RequiresControlOptIn: true, RequiresConfirmation: true, RequiresPin: true));
        return new(capabilities, features.AsReadOnly(), DateTimeOffset.UtcNow);
    }
    private static string CommandName(VehicleCommand c) => c switch
    {
        VehicleCommand.Lock => "Lock", VehicleCommand.Unlock => "Unlock", VehicleCommand.StartClimate => "Start climate", VehicleCommand.StopClimate => "Stop climate", VehicleCommand.CloseWindows => "Close windows", VehicleCommand.VentWindows => "Vent windows", VehicleCommand.OpenBoot => "Open boot", VehicleCommand.CloseBoot => "Close boot", VehicleCommand.ScheduleClimate => "Climate bookings", VehicleCommand.BatteryHeating => "Battery heating", VehicleCommand.StartCharging => "Start charging", VehicleCommand.StopCharging => "Stop charging", VehicleCommand.SaveChargingSchedule => "Charging schedule", _ => "Seat comfort"
    };
    private static string SeatName(SeatFeature s) => s switch { SeatFeature.DriverHeating => "Driver seat heating", SeatFeature.DriverVentilation => "Driver seat ventilation", SeatFeature.PassengerHeating => "Passenger seat heating", SeatFeature.PassengerVentilation => "Passenger seat ventilation", _ => "Steering-wheel heating" };
}
public sealed partial class BydClient
{
    /// <summary>Read reported feature support once for a listed vehicle. Does not probe features, verify PINs or operate the car.</summary>
    /// <param name="vin">VIN selected from the latest GetVehiclesAsync result in this session; never log it.</param>
    /// <param name="cancellation">Caller cancellation/deadline. After possible command dispatch, cancellation means unknown outcome.</param>
    /// <exception cref="BydException">Sanitized authentication, selection, protocol or command failure; inspect Error and CommandStage.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public Task<VehicleFeatureCatalog> GetVehicleFeaturesAsync(string vin, CancellationToken cancellation = default) => Run(async () =>
    {
        RequireVehicle(vin); return BydDiscovery.Describe(await LoadCapabilities(vin, cancellation));
    }, cancellation);
}
