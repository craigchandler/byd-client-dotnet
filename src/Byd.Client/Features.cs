using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Byd.Client;

/// <summary>Comfort targets understood by this implementation; support must be checked for each vehicle.</summary>
public enum SeatFeature
{
    /// <summary>Driver Heating.</summary>
    DriverHeating,
    /// <summary>Driver Ventilation.</summary>
    DriverVentilation,
    /// <summary>Passenger Heating.</summary>
    PassengerHeating,
    /// <summary>Passenger Ventilation.</summary>
    PassengerVentilation,
    /// <summary>Steering Wheel Heating.</summary>
    SteeringWheelHeating,
}
/// <summary>Requested or reported comfort setting. Steering-wheel heating supports Off or High only.</summary>
public enum ComfortLevel
{
    /// <summary>Off.</summary>
    Off,
    /// <summary>Low.</summary>
    Low,
    /// <summary>High.</summary>
    High,
}
/// <summary>Operation on a vehicle-stored climate booking. Bookings can run after the caller exits.</summary>
public enum BookingOperation
{
    /// <summary>Create.</summary>
    Create,
    /// <summary>Update.</summary>
    Update,
    /// <summary>Remove.</summary>
    Remove,
}
/// <summary>Repeat pattern for a vehicle-local charging window.</summary>
public enum ChargeRepeat
{
    /// <summary>Once.</summary>
    Once,
    /// <summary>Daily.</summary>
    Daily,
    /// <summary>Weekdays.</summary>
    Weekdays,
    /// <summary>Weekends.</summary>
    Weekends,
}

/// <summary>Immutable, per-confirmation parameters. No credentials, PIN or mutable collections.</summary>
public sealed record CommandSettings
{
    /// <summary>Climate setpoint from 15 through 31 degrees Celsius; defaults to 21.</summary>
    public int TemperatureC { get; init; } = 21;
    /// <summary>Climate duration: 10, 15, 20, 25 or 30 minutes; defaults to 10.</summary>
    public int DurationMinutes { get; init; } = 10;
    /// <summary>Climate booking departure instant, two minutes through thirty days ahead when creating or updating.</summary>
    public DateTimeOffset? Departure { get; init; }
    /// <summary>Positive reported 64-bit booking ID required for update/removal; null when creating.</summary>
    public long? BookingId { get; init; }
    /// <summary>Create, update or remove a vehicle-stored climate booking.</summary>
    public BookingOperation BookingOperation { get; init; }
    /// <summary>Target comfort feature for ChangeSeatClimate.</summary>
    public SeatFeature Seat { get; init; }
    /// <summary>Requested comfort setting. Low is invalid for steering-wheel heat.</summary>
    public ComfortLevel Level { get; init; }
    /// <summary>Whether battery heating or the saved charging schedule is enabled; defaults to true.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Window start in vehicle-local clock time, with whole-minute resolution.</summary>
    public TimeOnly ChargeStart { get; init; } = new(0, 0);
    /// <summary>Window end in vehicle-local clock time, with whole-minute resolution; may cross midnight.</summary>
    public TimeOnly ChargeEnd { get; init; } = new(6, 0);
    /// <summary>Whether charging continues until full instead of ending at ChargeEnd.</summary>
    public bool ChargeUntilFull { get; init; }
    /// <summary>Charging-window repeat pattern; defaults to Daily.</summary>
    public ChargeRepeat Repeat { get; init; } = ChargeRepeat.Daily;
    /// <inheritdoc/>
    public override string ToString() => "Vehicle command settings";
    internal void Validate(VehicleCommand command)
    {
        if (!Enum.IsDefined(command) || !Enum.IsDefined(Seat) || !Enum.IsDefined(Level) || !Enum.IsDefined(BookingOperation) || !Enum.IsDefined(Repeat)
            || TemperatureC is < 15 or > 31 || DurationMinutes is not (10 or 15 or 20 or 25 or 30)) throw new ArgumentException("Invalid command settings.");
        if (command == VehicleCommand.ScheduleClimate)
        {
            if (BookingOperation != BookingOperation.Create && BookingId is not > 0) throw new ArgumentException("Select a reported climate booking.");
            if (BookingOperation != BookingOperation.Remove && (Departure == null || Departure < DateTimeOffset.UtcNow.AddMinutes(2) || Departure > DateTimeOffset.UtcNow.AddDays(30))) throw new ArgumentException("Choose a departure between two minutes and thirty days from now.");
        }
        if (command == VehicleCommand.ChangeSeatClimate && Seat == SeatFeature.SteeringWheelHeating && Level == ComfortLevel.Low) throw new ArgumentException("Steering-wheel heat supports on or off.");
        if (command == VehicleCommand.SaveChargingSchedule && !ChargeUntilFull && ChargeStart == ChargeEnd) throw new ArgumentException("Choose different charging start and end times.");
        if (command == VehicleCommand.SaveChargingSchedule && (ChargeStart.Ticks % TimeSpan.TicksPerMinute != 0 || !ChargeUntilFull && ChargeEnd.Ticks % TimeSpan.TicksPerMinute != 0)) throw new ArgumentException("Charging times must use whole minutes.");
    }
}

/// <summary>Optional readings included in status. Missing or invalid values are null; tyre pressures are normalized to kPa.</summary>
public sealed record VehicleDetails
{
    /// <inheritdoc/>
    public override string ToString() => "VehicleDetails (telemetry redacted)";
    /// <summary>Reported cabin temperature in Celsius, or null.</summary>
    public double? CabinTemperatureC { get; init; }
    /// <summary>Reported outside temperature in Celsius, or null.</summary>
    public double? OutsideTemperatureC { get; init; }
    /// <summary>Reported total distance in kilometres, or null.</summary>
    public double? OdometerKm { get; init; }
    /// <summary>Front-left tyre pressure in kPa, or null when units/value are unavailable.</summary>
    public double? FrontLeftTyreKpa { get; init; }
    /// <summary>Front-right tyre pressure in kPa, or null when units/value are unavailable.</summary>
    public double? FrontRightTyreKpa { get; init; }
    /// <summary>Rear-left tyre pressure in kPa, or null when units/value are unavailable.</summary>
    public double? RearLeftTyreKpa { get; init; }
    /// <summary>Rear-right tyre pressure in kPa, or null when units/value are unavailable.</summary>
    public double? RearRightTyreKpa { get; init; }
    /// <summary>Reported minutes until full, or null.</summary>
    public int? MinutesToFull { get; init; }
    internal static VehicleDetails Parse(JsonNode n)
    {
        double? Tyre(string key)
        {
            var value = Data.Number(n, key) ?? Data.Number(n, key.Replace("Pressure", "pressure", StringComparison.Ordinal));
            if (value is not > 0) return null;
            var kpa = Data.Number(n, "tirePressUnit") switch { 1 => value * 100, 2 => value * 6.894757, 3 => value, _ => null };
            return kpa is > 0 and < 1000 ? kpa : null;
        }
        return new()
        {
            CabinTemperatureC = Data.Temperature(n, "tempInCar"), OutsideTemperatureC = Data.Temperature(n, "tempOutCar"), OdometerKm = Data.Nonnegative(n, "totalMileage"),
            FrontLeftTyreKpa = Tyre("leftFrontTirePressure"), FrontRightTyreKpa = Tyre("rightFrontTirePressure"), RearLeftTyreKpa = Tyre("leftRearTirePressure"), RearRightTyreKpa = Tyre("rightRearTirePressure"),
            MinutesToFull = Data.Minutes(n)
        };
    }
}

/// <summary>Advertised feature flags, not proof of physical support or successful execution.</summary>
public sealed class VehicleCapabilities
{
    private readonly HashSet<string> codes;
    /// <summary>Whether the authenticated vehicle list positively reported window learning.</summary>
    public bool WindowVentingLearned { get; }
    internal VehicleCapabilities(IEnumerable<string> codes, bool windowVentingLearned) { this.codes = new(codes, StringComparer.Ordinal); WindowVentingLearned = windowVentingLearned; }
    /// <summary>Returns advertised support for the command and optional comfort target; false for unknown command values. Does not authenticate, verify a PIN or guarantee execution.</summary>
    public bool Supports(VehicleCommand command, CommandSettings? settings = null) => command switch
    {
        VehicleCommand.Lock => Has("1005"), VehicleCommand.Unlock => Has("1006"),
        VehicleCommand.StartClimate or VehicleCommand.StopClimate or VehicleCommand.ScheduleClimate => Has("1001", "10300001", "1015"),
        VehicleCommand.CloseWindows => Has("1026"), VehicleCommand.VentWindows => Has("1026") && WindowVentingLearned,
        VehicleCommand.OpenBoot => Has("1020"), VehicleCommand.CloseBoot => Has("1021"), VehicleCommand.BatteryHeating => Has("10300002"),
        VehicleCommand.StartCharging or VehicleCommand.StopCharging or VehicleCommand.SaveChargingSchedule => Has("1012"),
        VehicleCommand.ChangeSeatClimate => SupportsSeat((settings ?? new()).Seat), _ => false
    };
    /// <summary>Returns advertised support for a comfort target; false for unknown targets.</summary>
    public bool SupportsSeat(SeatFeature seat) => seat switch
    {
        SeatFeature.DriverHeating => Has("10030002", "10300003"), SeatFeature.DriverVentilation => Has("10030001", "10300003"),
        SeatFeature.PassengerHeating => Has("10030005", "10300003"), SeatFeature.PassengerVentilation => Has("10030004", "10300003"),
        SeatFeature.SteeringWheelHeating => Has("10030010", "10300004"), _ => false
    };
    internal bool Has(params string[] values) => values.Any(codes.Contains);
    /// <inheritdoc/>
    public override string ToString() => "Reported vehicle capabilities";
}

/// <summary>A reported charge window in vehicle-local clock time. Null values indicate missing fields.</summary>
/// <param name="Start">Vehicle-local charging start time, or null.</param>
/// <param name="End">Vehicle-local charging end time, or null; not used when UntilFull is true.</param>
/// <param name="UntilFull">Whether the reported window continues until full.</param>
/// <param name="Repeat">Reported repeat label, or Unknown. Not a machine-readable scheduling contract.</param>
/// <param name="Enabled">Reported enablement flag, or null.</param>
public sealed record ChargingSchedule(TimeOnly? Start, TimeOnly? End, bool UntilFull, string Repeat, bool? Enabled)
{
    /// <inheritdoc/>
    public override string ToString() => "ChargingSchedule (telemetry redacted)";
}
/// <summary>A reported departure/off-peak schedule in vehicle-local clock time; currently read-only.</summary>
/// <param name="ReadyAt">Vehicle-local departure time, or null.</param>
/// <param name="OffPeakStart">Vehicle-local off-peak start time, or null.</param>
/// <param name="OffPeakEnd">Vehicle-local off-peak end time, or null.</param>
/// <param name="Repeat">Reported repeat label, or Unknown. Not a machine-readable scheduling contract.</param>
/// <param name="Enabled">Reported enablement flag, or null.</param>
public sealed record JourneySchedule(TimeOnly? ReadyAt, TimeOnly? OffPeakStart, TimeOnly? OffPeakEnd, string Repeat, bool? Enabled)
{
    /// <inheritdoc/>
    public override string ToString() => "JourneySchedule (telemetry redacted)";
}
/// <summary>Dedicated charging endpoint readings, separate from realtime status. Null indicates unavailable data.</summary>
/// <param name="BatteryPercent">Reported battery percentage from zero through one hundred, or null.</param>
/// <param name="Charging">Reported charging state; unavailable/unrecognized data remains unknown.</param>
/// <param name="Connected">Reported charging-cable connection, or null; never inferred from battery percentage.</param>
/// <param name="MinutesToFull">Reported estimate in minutes, or null.</param>
/// <param name="Schedule">Reported standard charge window, or null.</param>
/// <param name="Journey">Reported read-only departure/off-peak schedule, or null.</param>
/// <param name="RetrievedAt">UTC-compatible instant when the client retrieved/created the snapshot; not the vehicle measurement time.</param>
/// <param name="VehicleTime">Provider measurement timestamp, or null. Inspect freshness separately.</param>
/// <param name="VehicleTimeZone">Provider-reported timezone, or null; not automatically applied to other snapshots.</param>
public sealed record ChargingSnapshot(double? BatteryPercent, bool? Charging, bool? Connected, int? MinutesToFull, ChargingSchedule? Schedule, JourneySchedule? Journey, DateTimeOffset RetrievedAt, DateTimeOffset? VehicleTime, string? VehicleTimeZone)
{
    /// <inheritdoc/>
    public override string ToString() => "ChargingSnapshot (telemetry redacted)";
    internal static ChargingSnapshot Parse(JsonNode n) => new(Data.Nonnegative(n, "soc", 100) ?? Data.Nonnegative(n, "elecPercent", 100), Data.Number(n, "chargingState") switch { 1 => true, 0 or 15 => false, _ => null }, Data.Flag(n, "connectState"), Data.Minutes(n),
        n["smartChargeDto"] is JsonObject c ? new(Data.Time(c, "startChargeTime"), Data.Time(c, "endChargeTime"), Data.Text(c, "endChargeTime") == "full", Data.Repeat(c), Data.Flag(c, "status")) : null,
        n["smartJourneyDto"] is JsonObject j ? new(Data.Time(j, "useVehicleTime"), Data.Time(j, "startDiscountPrice"), Data.Time(j, "endDiscountPrice"), Data.Repeat(j), Data.Flag(j, "status")) : null,
        DateTimeOffset.UtcNow, Data.Timestamp(n), Data.Text(n, "vehicleTimeZone"));
}
/// <summary>A reported climate booking. IDs are 64-bit values; unavailable fields are null.</summary>
/// <param name="Id">Positive provider booking identifier; retain the full 64-bit value.</param>
/// <param name="Departure">Reported climate departure instant, or null.</param>
/// <param name="TemperatureC">Reported climate temperature in Celsius, or null.</param>
/// <param name="DurationMinutes">Reported climate duration in minutes, or null.</param>
public sealed record ClimateBooking(long Id, DateTimeOffset? Departure, double? TemperatureC, int? DurationMinutes)
{
    /// <inheritdoc/>
    public override string ToString() => "ClimateBooking (telemetry redacted)";
}
/// <summary>Reported bookings. An empty list does not establish that the vehicle has no bookings.</summary>
/// <param name="Items">Read-only booking list. Empty does not prove there are no bookings.</param>
/// <param name="RetrievedAt">UTC-compatible instant when the client retrieved/created the snapshot; not the vehicle measurement time.</param>
public sealed record ClimateBookings(IReadOnlyList<ClimateBooking> Items, DateTimeOffset RetrievedAt)
{
    /// <inheritdoc/>
    public override string ToString() => "ClimateBookings (telemetry redacted)";
}
/// <summary>Climate and comfort readings. Missing or unrecognized state remains unknown.</summary>
/// <param name="CabinTemperatureC">Reported cabin temperature in Celsius, or null.</param>
/// <param name="OutsideTemperatureC">Reported outside temperature in Celsius, or null.</param>
/// <param name="Running">Reported climate running flag, or null.</param>
/// <param name="RetrievedAt">UTC-compatible instant when the client retrieved/created the snapshot; not the vehicle measurement time.</param>
public sealed record ClimateSnapshot(double? CabinTemperatureC, double? OutsideTemperatureC, bool? Running, DateTimeOffset RetrievedAt)
{
    /// <inheritdoc/>
    public override string ToString() => "ClimateSnapshot (telemetry redacted)";
    internal IReadOnlyDictionary<string, int?> SeatStates { get; init; } = new ReadOnlyDictionary<string, int?>(new Dictionary<string, int?>());
    /// <summary>Returns the reported comfort state, or null when unavailable. Invalid seat values throw ArgumentException.</summary>
    public ComfortLevel? GetSeat(SeatFeature seat) => seat == SeatFeature.SteeringWheelHeating
        ? SeatStates.GetValueOrDefault("steeringWheelHeatState") switch { -1 => ComfortLevel.High, 1 => ComfortLevel.Off, _ => null }
        : SeatStates.GetValueOrDefault(SeatWire.StatusKey(seat)) switch { 1 => ComfortLevel.Off, 2 => ComfortLevel.Low, 3 => ComfortLevel.High, _ => null };
    internal static ClimateSnapshot Parse(JsonNode n) => new(Data.Temperature(n, "tempInCar"), Data.Temperature(n, "tempOutCar"), Data.Flag(n, "acSwitch"), DateTimeOffset.UtcNow)
    { SeatStates = new ReadOnlyDictionary<string, int?>(SeatWire.Keys.ToDictionary(pair => pair.Status, pair => Data.Int(n, pair.Status))) };
}
/// <summary>Reported energy statistics. SevenDayConsumption contains at most seven nullable readings.</summary>
/// <param name="OdometerKm">Reported odometer distance in kilometres, or null.</param>
/// <param name="LifetimeKwhPer100Km">Reported lifetime electric consumption in kWh per 100 km, or null.</param>
/// <param name="RecentKwhPer100Km">Reported recent electric consumption in kWh per 100 km, or null.</param>
/// <param name="SevenDayConsumption">At most seven nullable daily readings in the reported Unit.</param>
/// <param name="Unit">Provider-supplied chart unit or a fixed unavailable label; do not assume chart units.</param>
/// <param name="DrivingShare">Reported driving share from zero through one hundred percent, or null.</param>
/// <param name="ClimateShare">Reported climate share from zero through one hundred percent, or null.</param>
/// <param name="OtherShare">Reported other-energy share from zero through one hundred percent, or null.</param>
/// <param name="RetrievedAt">UTC-compatible instant when the client retrieved/created the snapshot; not the vehicle measurement time.</param>
/// <param name="VehicleTime">Provider measurement timestamp, or null. Inspect freshness separately.</param>
public sealed record EnergySnapshot(double? OdometerKm, double? LifetimeKwhPer100Km, double? RecentKwhPer100Km, IReadOnlyList<double?> SevenDayConsumption, string Unit, double? DrivingShare, double? ClimateShare, double? OtherShare, DateTimeOffset RetrievedAt, DateTimeOffset? VehicleTime)
{
    /// <inheritdoc/>
    public override string ToString() => "EnergySnapshot (telemetry redacted)";
    internal static EnergySnapshot Parse(JsonNode n)
    {
        var cumulative = n["cumulativeEnergyConsumption"] as JsonObject ?? new(); var recent = n["nearestEnergyConsumption"] as JsonObject ?? new();
        var graph = n["selfGraph"] as JsonObject ?? new();
        return new(Data.Nonnegative(cumulative, "totalMileage"), Data.Nonnegative(cumulative, "avgEvConsumption"), Data.Nonnegative(recent, "avgEvConsumption"),
            Array.AsReadOnly((graph["energyConsumption"] as JsonArray)?.Take(7).Select(x => Data.ScalarNumber(x) is >= 0 and < 1000 ? Data.ScalarNumber(x) : null).ToArray() ?? []),
            Data.Text(graph, "energyConsumptionUnit") is { Length: < 40 } unit ? unit : "Unit not supplied", Data.Nonnegative(recent, "driveDistribution", 100), Data.Nonnegative(recent, "airDistribution", 100), Data.Nonnegative(recent, "otherDistribution", 100), DateTimeOffset.UtcNow, Data.Timestamp(n));
    }
}
internal static class Data
{
    internal static string? Text(JsonNode n, string key) => n is JsonObject && n[key] is JsonValue v ? v.ToString() : null;
    internal static double? ScalarNumber(JsonNode? n) => n is JsonValue && double.TryParse(n.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    internal static double? Number(JsonNode n, string key) => n is JsonObject ? ScalarNumber(n[key]) : null;
    internal static int? Int(JsonNode n, string key) => Number(n, key) is { } v && v == Math.Truncate(v) && v >= int.MinValue && v <= int.MaxValue ? (int)v : null;
    internal static double? Nonnegative(JsonNode n, string key, double max = 10_000_000) => Number(n, key) is { } v && v >= 0 && v <= max ? v : null;
    internal static double? Temperature(JsonNode n, string key) => Number(n, key) is >= -60 and <= 85 ? Number(n, key) : null;
    internal static bool? Flag(JsonNode n, string key) => Text(n, key) switch { "1" or "true" => true, "0" or "false" => false, _ => null };
    internal static int? Minutes(JsonNode n)
    {
        var h = Int(n, "fullHour") ?? Int(n, "remainingHours"); var m = Int(n, "fullMinute") ?? Int(n, "remainingMinutes");
        return h is >= 0 and <= 168 && m is >= 0 and <= 59 ? h * 60 + m : null;
    }
    internal static TimeOnly? Time(JsonNode n, string key) => TimeOnly.TryParseExact(Text(n, key), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    internal static string Repeat(JsonNode n) => Text(n, "chargeWay") switch { "s" => "Once", "e" => "Daily", "0,1,2,3,4" => "Weekdays", "5,6" => "Weekends", { } v when v.Length < 20 && v.All(c => char.IsAsciiDigit(c) || c == ',') => "Days: " + v, _ => "Unknown" };
    internal static DateTimeOffset? Timestamp(JsonNode n) => VehicleStatus.Timestamp(n, "time") ?? VehicleStatus.Timestamp(n, "updateTime") ?? VehicleStatus.Timestamp(n, "timestamp");
}

internal static class SeatWire
{
    internal static readonly (string Status, string Command)[] Keys = [("mainSeatHeatState", "mainHeat"), ("mainSeatVentilationState", "mainVentilation"), ("copilotSeatHeatState", "copilotHeat"), ("copilotSeatVentilationState", "copilotVentilation"), ("lrSeatHeatState", "lrSeatHeatState"), ("lrSeatVentilationState", "lrSeatVentilationState"), ("rrSeatHeatState", "rrSeatHeatState"), ("rrSeatVentilationState", "rrSeatVentilationState"), ("steeringWheelHeatState", "steeringWheelHeatState")];
    internal static string StatusKey(SeatFeature s) => s switch { SeatFeature.DriverHeating => Keys[0].Status, SeatFeature.DriverVentilation => Keys[1].Status, SeatFeature.PassengerHeating => Keys[2].Status, SeatFeature.PassengerVentilation => Keys[3].Status, SeatFeature.SteeringWheelHeating => Keys[8].Status, _ => throw new ArgumentException("Invalid seat feature.") };
}
