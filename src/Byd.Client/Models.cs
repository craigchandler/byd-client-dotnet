using System.Globalization;
using System.Text.Json.Nodes;

namespace Byd.Client;

/// <summary>Connection and polling limits. Controls are disabled by default; no credentials are persisted.</summary>
public sealed record BydClientOptions
{
    /// <summary>Account country from BydMarkets; defaults to AU. Credentials go to its allow-listed HTTPS endpoint.</summary>
    public BydMarket Market { get; init; } = BydMarkets.ForCountry("AU");
    private readonly string? timeZone;
    /// <summary>IANA timezone sent to BYD. Defaults to Australia/Adelaide for AU and UTC elsewhere; set explicitly for your location.</summary>
    public string TimeZone { get => timeZone ?? (Market.CountryCode == "AU" ? "Australia/Adelaide" : "UTC"); init => timeZone = value; }
    /// <summary>Initial local control opt-in for this client; false by default. Change later with BydClient.SetRemoteControlsEnabledAsync without signing in again. Never grants command approval.</summary>
    public bool EnableRemoteControls { get; init; }
    /// <summary>Whether to open a transient TLS MQTT subscription during status/location reads; true by default.</summary>
    public bool EnablePushTelemetry { get; init; } = true;
    /// <summary>Maximum push wait; greater than zero, at most one minute, default twenty seconds.</summary>
    public TimeSpan PushTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Per-HTTP-request timeout; greater than zero, at most Int32.MaxValue milliseconds, default thirty seconds.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Maximum result lookups after a single trigger, from one to thirty; defaults to eight.</summary>
    public int PollAttempts { get; init; } = 8;
    /// <summary>Delay between result lookups; nonnegative, at most UInt32.MaxValue minus one milliseconds, default two seconds.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>Safe failure categories. Values are append-only; an unknown command outcome must never trigger an automatic retry.</summary>
public enum BydError
{
    /// <summary>No session, local twelve-hour expiry, or provider-invalidated session; request an explicit new sign-in.</summary>
    AuthenticationRequired,
    /// <summary>The provider rejected explicit sign-in; check account country and credentials locally.</summary>
    AuthenticationRejected,
    /// <summary>Transport failure or HTTP timeout; no authentication or request replay is performed.</summary>
    Network,
    /// <summary>Malformed or unrecognized provider response; raw details are suppressed.</summary>
    InvalidResponse,
    /// <summary>No usable reading is available.</summary>
    NoData,
    /// <summary>Configuration did not advertise the command, or the provider rejected the read as unsupported.</summary>
    Unsupported,
    /// <summary>The provider rejected the request.</summary>
    Rejected,
    /// <summary>This client has not opted in to controls.</summary>
    ControlsDisabled,
    /// <summary>A fresh client-bound single-use confirmation is required.</summary>
    ApprovalRequired,
    /// <summary>PIN input is not six ASCII digits.</summary>
    InvalidPin,
    /// <summary>A command may have reached the vehicle. Check its state before any manual retry.</summary>
    OutcomeUnknown,
    /// <summary>Provider busy or rate-limited; back off and let the user retry later.</summary>
    Busy,
    /// <summary>Only zero-filled templates were received.</summary>
    PlaceholderData,
    /// <summary>Live push failed and no usable HTTP snapshot arrived.</summary>
    LiveTelemetryUnavailable,
    /// <summary>No valid GPS fix was received.</summary>
    LocationUnavailable,
    /// <summary>The provider rejected remote-control PIN access.</summary>
    PinRejected,
    /// <summary>Command preflight was cancelled before dispatch.</summary>
    Cancelled,
    /// <summary>Select a vehicle returned by GetVehiclesAsync in the current session.</summary>
    VehicleNotSelected,
}
/// <summary>Stage of a command failure. CapabilityCheck and PinVerification precede vehicle-command dispatch.</summary>
public enum CommandStage
{
    /// <summary>Support check before dispatch; no vehicle command was sent.</summary>
    CapabilityCheck,
    /// <summary>PIN check before dispatch; no vehicle command was sent.</summary>
    PinVerification,
    /// <summary>Command submission was attempted; its physical outcome may be unknown.</summary>
    Dispatch,
    /// <summary>Command result lookup after submission; its physical outcome may be unknown.</summary>
    Result,
}
/// <summary>Sanitized protocol or transport failure. Server text, raw payloads and inner exceptions are omitted.</summary>
public sealed class BydException : Exception
{
    /// <summary>Safe failure category for application decisions.</summary>
    public BydError Error { get; }
    /// <summary>Optional numeric provider code, restricted to at most six ASCII digits; never server message text.</summary>
    public string? ApiCode { get; }
    /// <summary>Optional command failure stage. Dispatch or Result failures may have an unknown physical outcome.</summary>
    public CommandStage? CommandStage { get; }
    /// <summary>Creates a sanitized exception with a fixed category message and optional numeric provider code.</summary>
    public BydException(BydError error, string? apiCode = null) : this(error, apiCode, null) { }
    internal BydException(BydError error, string? apiCode, CommandStage? stage) : base(StageMessage(stage) + MessageFor(error))
    { Error = error; CommandStage = stage; ApiCode = apiCode is { Length: > 0 and <= 6 } && apiCode.All(char.IsAsciiDigit) ? apiCode : null; }
    private static string StageMessage(CommandStage? stage) => stage switch
    {
        global::Byd.Client.CommandStage.CapabilityCheck => "Vehicle support check failed. No vehicle command was sent. ",
        global::Byd.Client.CommandStage.PinVerification => "PIN verification failed. No vehicle command was sent. ",
        _ => ""
    };
    private static string MessageFor(BydError error) => error switch
    {
        BydError.AuthenticationRequired => "Sign in again. The session is missing or expired.",
        BydError.VehicleNotSelected => "Select a vehicle from the current signed-in vehicle list. Load the list again after signing in.",
        BydError.AuthenticationRejected => "BYD rejected the sign-in. Check the selected account country and your account in the official app.",
        BydError.Network => "The BYD service could not be reached. Try again manually later.",
        BydError.NoData => "No usable vehicle data arrived. The car may be asleep or offline.",
        BydError.PlaceholderData => "BYD returned placeholder data instead of confirmed vehicle readings. Try again manually later.",
        BydError.LiveTelemetryUnavailable => "The live vehicle connection was unavailable, and no usable status arrived. Try again manually later.",
        BydError.LocationUnavailable => "No valid vehicle location arrived. The car may be indoors, asleep or without a GPS fix.",
        BydError.ControlsDisabled => "Remote controls are disabled for this client.",
        BydError.ApprovalRequired => "A new confirmation for this specific command is required.",
        BydError.InvalidPin => "Enter the six-digit BYD remote-control PIN.",
        BydError.PinRejected => "BYD rejected remote-control PIN access. Check it in the official app before another attempt.",
        BydError.Cancelled => "The check was cancelled.",
        BydError.OutcomeUnknown => "Command outcome is unknown. Check the official app or vehicle before trying again.",
        BydError.Unsupported => "This function is not reported as supported for this vehicle.",
        BydError.Rejected => "BYD rejected the request. Check the official app; no automatic retry was sent.",
        BydError.Busy => "BYD is busy or rate-limiting requests. Try again manually later.",
        _ => "BYD returned an unexpected response. Sensitive response details were omitted."
    };
}

/// <summary>A vehicle returned by the signed-in account. VIN and model are sensitive; do not serialize or log the object.</summary>
/// <param name="Vin">VIN from the current signed-in vehicle list. Treat as personal data; never log.</param>
/// <param name="Model">Provider-reported model/name; may contain personal data.</param>
/// <param name="EnergyType">Provider energy-type code used for subsequent requests; not a standardized vehicle classification.</param>
public sealed record Vehicle(string Vin, string Model, int EnergyType)
{
    /// <summary>Positive window-learning flag from the authenticated vehicle list; still requires fresh capabilities before dispatch.</summary>
    public bool SupportsWindowVenting { get; internal init; }
    /// <summary>UI label containing model and last four VIN characters. Treat as personal data; do not log.</summary>
    public string DisplayName => $"{(string.IsNullOrWhiteSpace(Model) ? "BYD" : Model)} · …{Vin[^Math.Min(4, Vin.Length)..]}";
    /// <inheritdoc/>
    public override string ToString() => "BYD vehicle (identity redacted)";
}
/// <summary>Read-only snapshot. Nullable fields and Unknown labels mean unavailable data; timestamps do not guarantee freshness.</summary>
/// <param name="BatteryPercent">Reported battery percentage from zero through one hundred, or null.</param>
/// <param name="RangeKm">Reported remaining range in kilometres, or null.</param>
/// <param name="Charging">Charging, Not charging or Unknown, based only on authoritative realtime state.</param>
/// <param name="Locks">Locked, Unlocked or Unknown based on reported lock state.</param>
/// <param name="RetrievedAt">UTC-compatible instant when the client retrieved/created the snapshot; not the vehicle measurement time.</param>
/// <param name="VehicleTime">Provider measurement timestamp, or null. Inspect freshness separately.</param>
/// <param name="Online">Provider online flag, or null; not an independent connectivity check.</param>
public sealed record VehicleStatus(double? BatteryPercent, double? RangeKm, string Charging, string Locks,
    DateTimeOffset RetrievedAt, DateTimeOffset? VehicleTime, bool? Online)
{
    /// <summary>Typed charging state for application decisions. Derived from the compatibility Charging label; unrecognized labels remain Unknown.</summary>
    public VehicleChargingState ChargingState => Charging switch
    {
        "Charging" => VehicleChargingState.Charging,
        "Not charging" => VehicleChargingState.NotCharging,
        _ => VehicleChargingState.Unknown
    };
    /// <summary>Typed lock state for application decisions. Derived from the compatibility Locks label; unrecognized labels remain Unknown.</summary>
    public VehicleLockState LockState => Locks switch
    {
        "Locked" => VehicleLockState.Locked,
        "Unlocked" => VehicleLockState.Unlocked,
        _ => VehicleLockState.Unknown
    };
    /// <inheritdoc/>
    public override string ToString() => "Vehicle status (telemetry redacted)";
    /// <summary>Transport provenance for this status, independent of timestamp freshness.</summary>
    public TelemetrySource Source { get; internal init; }
    /// <summary>Known door/window counts and completeness. Missing states remain unknown.</summary>
    public VehicleOpenings Openings { get; init; } = VehicleOpenings.Unknown;
    /// <summary>Optional temperatures, odometer, tyre pressures and minutes to full from the same status response.</summary>
    public VehicleDetails Details { get; init; } = new();
    /// <summary>Returns true when the vehicle/fix timestamp is missing, over thirty minutes old or over five minutes in the future.</summary>
    public bool IsStale(DateTimeOffset now) => VehicleTime is null || now - VehicleTime > TimeSpan.FromMinutes(30) || VehicleTime > now + TimeSpan.FromMinutes(5);
    // HTTP can return an entire zero-filled template while the real readings arrive via MQTT.
    // Do not mistake that template for an empty battery. Supported zero readings remain valid.
    internal bool HasUsableData => Online != false && (BatteryPercent > 0 || RangeKm > 0 || LockState != VehicleLockState.Unknown || ChargingState == VehicleChargingState.Charging);
    internal static VehicleStatus Parse(JsonNode node, DateTimeOffset now)
    {
        var battery = ValidNumber(node, "elecPercent", 100) ?? ValidNumber(node, "powerBattery", 100);
        var range = ValidNumber(node, "enduranceMileage") ?? ValidNumber(node, "evEndurance") ?? ValidNumber(node, "enduranceMileageV2");
        // chargeState is authoritative; chargingState may remain at a default value.
        var charge = Number(node, "chargeState");
        var charging = charge switch { 1 => "Charging", 0 or 15 => "Not charging", _ => "Unknown" };
        // Authoritative 15 is a non-charging state in the reference model. It is not
        // reliable evidence of a connected cable; the secondary chargingState can be stale.
        double?[] locks = [Number(node, "leftFrontDoorLock"), Number(node, "rightFrontDoorLock"), Number(node, "leftRearDoorLock"), Number(node, "rightRearDoorLock")];
        var lockText = locks.Any(x => x == 1) ? "Unlocked" : locks.All(x => x == 2) ? "Locked" : "Unknown";
        var vehicleTime = Timestamp(node, "time") ?? Timestamp(node, "timestamp") ?? Timestamp(node, "updateTime");
        return new VehicleStatus(battery, range, charging, lockText, now, vehicleTime, Number(node, "onlineState") switch { 1 => true, 2 => false, _ => null })
        { Openings = VehicleOpenings.Parse(node), Details = VehicleDetails.Parse(node) };
    }
    private static double? ValidNumber(JsonNode node, string key, double maximum = double.MaxValue) => Number(node, key) is { } n && n >= 0 && n <= maximum ? n : null;
    internal static DateTimeOffset? Timestamp(JsonNode node, string key)
    {
        if (Number(node, key) is { } time && time > 0)
        {
            try { return time > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds((long)time) : DateTimeOffset.FromUnixTimeSeconds((long)time); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return DateTimeOffset.TryParse(node[key]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result) ? result : null;
    }
    internal static double? Number(JsonNode node, string key) => double.TryParse(node[key]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
}

/// <summary>Semantic realtime charging state. Enum values are not BYD wire codes and do not establish cable connection.</summary>
public enum VehicleChargingState
{
    /// <summary>The vehicle did not supply a recognized authoritative charging state.</summary>
    Unknown = 0,
    /// <summary>No charging is reported in progress; this includes a full battery without implying cable connection.</summary>
    NotCharging = 1,
    /// <summary>Charging is reported in progress, including when battery percentage is one hundred.</summary>
    Charging = 2
}

/// <summary>Semantic reported lock state. Enum values are not BYD wire codes or independent physical verification.</summary>
public enum VehicleLockState
{
    /// <summary>A complete recognized lock state was not supplied.</summary>
    Unknown = 0,
    /// <summary>All four passenger door locks are reported locked.</summary>
    Locked = 1,
    /// <summary>At least one passenger door lock is reported unlocked.</summary>
    Unlocked = 2
}

/// <summary>Transport that supplied a usable snapshot; this does not guarantee its freshness.</summary>
public enum TelemetrySource
{
    /// <summary>Unavailable or unverified. For command outcomes, verify the vehicle before another attempt.</summary>
    Unknown,
    /// <summary>HTTPS result snapshot.</summary>
    Http,
    /// <summary>Correlated transient MQTT event.</summary>
    LivePush,
}

/// <summary>Implemented command types, not a promise of vehicle support. Every execution needs opt-in, exact approval and a PIN.</summary>
public enum VehicleCommand
{
    /// <summary>Lock.</summary>
    Lock,
    /// <summary>Unlock.</summary>
    Unlock,
    /// <summary>Start Climate.</summary>
    StartClimate,
    /// <summary>Stop Climate.</summary>
    StopClimate,
    /// <summary>Close Windows.</summary>
    CloseWindows,
    /// <summary>Vent Windows.</summary>
    VentWindows,
    /// <summary>Open Boot.</summary>
    OpenBoot,
    /// <summary>Close Boot.</summary>
    CloseBoot,
    /// <summary>Schedule Climate.</summary>
    ScheduleClimate,
    /// <summary>Change Seat Climate.</summary>
    ChangeSeatClimate,
    /// <summary>Battery Heating.</summary>
    BatteryHeating,
    /// <summary>Start Charging.</summary>
    StartCharging,
    /// <summary>Stop Charging.</summary>
    StopCharging,
    /// <summary>Save Charging Schedule.</summary>
    SaveChargingSchedule,
}
/// <summary>Provider acknowledgement, not independent physical verification of the vehicle.</summary>
public enum CommandOutcome
{
    /// <summary>The provider confirmed the command; this is not independent physical verification.</summary>
    Confirmed,
    /// <summary>The provider reported a terminal failure.</summary>
    Failed,
    /// <summary>Unavailable or unverified. For command outcomes, verify the vehicle before another attempt.</summary>
    Unknown,
}
/// <summary>Client-bound, vehicle-bound, single-use approval expiring after sixty seconds. Create only after explicit confirmation.</summary>
public sealed class CommandApproval
{
    internal Guid ClientId { get; }
    internal string Vin { get; }
    /// <summary>Exact command approved by the caller.</summary>
    public VehicleCommand Command { get; }
    /// <summary>Climate setpoint from 15 through 31 degrees Celsius; defaults to 21.</summary>
    public int TemperatureC { get; }
    /// <summary>Climate duration: 10, 15, 20, 25 or 30 minutes; defaults to 10.</summary>
    public int DurationMinutes { get; }
    /// <summary>Immutable command parameters captured by the approval.</summary>
    public CommandSettings Settings { get; }
    private readonly DateTimeOffset expires = DateTimeOffset.UtcNow.AddSeconds(60);
    private int consumed;
    internal CommandApproval(Guid client, string vin, VehicleCommand command, int temperature, int duration)
        : this(client, vin, command, new CommandSettings { TemperatureC = temperature, DurationMinutes = duration }) { }
    internal CommandApproval(Guid client, string vin, VehicleCommand command, CommandSettings settings)
    { ClientId = client; Vin = vin; Command = command; Settings = settings; TemperatureC = settings.TemperatureC; DurationMinutes = settings.DurationMinutes; }
    internal bool Consume(Guid client) => client == ClientId && DateTimeOffset.UtcNow <= expires && Interlocked.Exchange(ref consumed, 1) == 0;
    /// <inheritdoc/>
    public override string ToString() => "Single-use vehicle command confirmation";
}
