using System.Globalization;
using System.Text.Json.Nodes;
using Byd.Client.Crypto;

namespace Byd.Client;

public sealed partial class BydClient
{
    /// <summary>Reads reported configuration flags for a vehicle in the current account list. Does not verify a PIN or dispatch controls.</summary>
    /// <param name="vin">VIN selected from the latest GetVehiclesAsync result in this session; never log it.</param>
    /// <param name="cancellation">Caller cancellation/deadline. After possible command dispatch, cancellation means unknown outcome.</param>
    /// <exception cref="BydException">Sanitized authentication, selection, protocol or command failure; inspect Error and CommandStage.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public Task<VehicleCapabilities> GetCapabilitiesAsync(string vin, CancellationToken cancellation = default) => Run(async () => { RequireVehicle(vin); return await LoadCapabilities(vin, cancellation); }, cancellation);
    /// <summary>Reads dedicated charging and schedule information. May wake the vehicle; unknown values remain null.</summary>
    /// <param name="vin">VIN selected from the latest GetVehiclesAsync result in this session; never log it.</param>
    /// <param name="cancellation">Caller cancellation/deadline. After possible command dispatch, cancellation means unknown outcome.</param>
    /// <exception cref="BydException">Sanitized authentication, selection, protocol or command failure; inspect Error and CommandStage.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public Task<ChargingSnapshot> GetChargingAsync(string vin, CancellationToken cancellation = default) => Run(async () =>
    {
        RequireVehicle(vin); return ChargingSnapshot.Parse(await ReadObject("/control/smartCharge/homePage", vin, cancellation));
    }, cancellation);
    /// <summary>Reads climate and comfort status. May wake the vehicle; does not change climate settings.</summary>
    /// <param name="vin">VIN selected from the latest GetVehiclesAsync result in this session; never log it.</param>
    /// <param name="cancellation">Caller cancellation/deadline. After possible command dispatch, cancellation means unknown outcome.</param>
    /// <exception cref="BydException">Sanitized authentication, selection, protocol or command failure; inspect Error and CommandStage.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public Task<ClimateSnapshot> GetClimateAsync(string vin, CancellationToken cancellation = default) => Run(async () =>
    {
        RequireVehicle(vin); return ClimateSnapshot.Parse(await ReadObject("/control/getStatusNow", vin, cancellation));
    }, cancellation);
    /// <summary>Reads reported climate bookings. Empty results do not prove that no bookings exist.</summary>
    /// <param name="vin">VIN selected from the latest GetVehiclesAsync result in this session; never log it.</param>
    /// <param name="cancellation">Caller cancellation/deadline. After possible command dispatch, cancellation means unknown outcome.</param>
    /// <exception cref="BydException">Sanitized authentication, selection, protocol or command failure; inspect Error and CommandStage.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public Task<ClimateBookings> GetClimateBookingsAsync(string vin, CancellationToken cancellation = default) => Run(async () =>
    {
        RequireVehicle(vin); var n = await ReadObject("/control/getBookingList", vin, cancellation);
        var list = n["listInfo"] as JsonArray;
        var items = list?.OfType<JsonObject>().Select(item =>
        {
            var id = long.TryParse(Data.Text(item, "bookingId"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : 0;
            return new ClimateBooking(id, VehicleStatus.Timestamp(item, "bookingTime"), Data.Temperature(item, "mainSettingTempNew"), Data.Int(item, "timeSpan") is >= 1 and <= 5 ? Data.Int(item, "timeSpan") * 5 + 5 : null);
        }).Where(item => item.Id > 0).Take(50).ToArray() ?? [];
        return new ClimateBookings(Array.AsReadOnly(items), DateTimeOffset.UtcNow);
    }, cancellation);
    /// <summary>Reads reported energy statistics. Unit and freshness depend on provider data.</summary>
    /// <param name="vin">VIN selected from the latest GetVehiclesAsync result in this session; never log it.</param>
    /// <param name="cancellation">Caller cancellation/deadline. After possible command dispatch, cancellation means unknown outcome.</param>
    /// <exception cref="BydException">Sanitized authentication, selection, protocol or command failure; inspect Error and CommandStage.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public Task<EnergySnapshot> GetEnergyAsync(string vin, CancellationToken cancellation = default) => Run(async () =>
    {
        var vehicle = RequireVehicle(vin);
        var inner = Inner(vin).ToDictionary(p => p.Key, p => (object?)p.Value);
        inner["powerType"] = vehicle.EnergyType.ToString(CultureInfo.InvariantCulture); inner["requestType"] = 0; inner["autoModelNameOut"] = vehicle.Model;
        var active = RequireSession(); var response = await Post("/vehicleInfo/vehicle/getEnergyConsumption", Protocol.Token(active, inner, Now()), cancellation);
        CheckReadCode(response); var decoded = DecodeTokenData(response, active.ContentKey);
        if (decoded is not JsonObject) throw new BydException(BydError.InvalidResponse);
        return EnergySnapshot.Parse(decoded);
    }, cancellation);
    private async Task<JsonObject> ReadObject(string endpoint, string vin, CancellationToken cancellation)
    {
        try { return await TokenPost(endpoint, Inner(vin), cancellation) as JsonObject ?? throw new BydException(BydError.InvalidResponse); }
        catch (BydException e) when (e.ApiCode == "1001") { throw new BydException(BydError.Unsupported, e.ApiCode); }
    }
    private void CheckReadCode(JsonNode response)
    {
        if (Data.Text(response, "code") == "1001") throw new BydException(BydError.Unsupported, "1001");
        CheckCode(response);
    }
    private static bool IsChargingCommand(VehicleCommand command) => command is VehicleCommand.StartCharging or VehicleCommand.StopCharging or VehicleCommand.SaveChargingSchedule;
    private static CommandOutcome ParseCommandOutcome(JsonNode node, bool charging) => charging && Data.Int(node, "res") is { } res
        ? res switch { 1 => CommandOutcome.Unknown, 2 => CommandOutcome.Confirmed, _ => CommandOutcome.Failed } : ParseOutcome(node);
    private async Task<Dictionary<string, string>> BuildCommandPayload(CommandApproval approval, VehicleCapabilities capabilities, string pin, CancellationToken cancellation)
    {
        var s = approval.Settings; var command = approval.Command; var inner = Inner(approval.Vin);
        if (IsChargingCommand(command))
        {
            inner["timeZone"] = "";
            inner["status"] = command == VehicleCommand.SaveChargingSchedule ? s.Enabled ? "1" : "0" : command == VehicleCommand.StartCharging ? "1" : "0";
            if (command == VehicleCommand.SaveChargingSchedule)
            {
                inner["startChargeTime"] = s.ChargeStart.ToString("HH:mm", CultureInfo.InvariantCulture);
                inner["endChargeTime"] = s.ChargeUntilFull ? "full" : s.ChargeEnd.ToString("HH:mm", CultureInfo.InvariantCulture);
                inner["chargeWay"] = s.Repeat switch { ChargeRepeat.Once => "s", ChargeRepeat.Daily => "e", ChargeRepeat.Weekdays => "0,1,2,3,4", ChargeRepeat.Weekends => "5,6", _ => throw new ArgumentException("Invalid repeat.") };
            }
            return inner;
        }
        inner["commandPwd"] = Protocol.Md5(pin); inner["commandType"] = CommandType(command);
        if (command == VehicleCommand.StartClimate) inner["controlParamsMap"] = ClimateParameters(s.TemperatureC, s.DurationMinutes);
        if (command == VehicleCommand.ScheduleClimate)
            inner["controlParamsMap"] = ClimateBookingParameters(s);
        if (command == VehicleCommand.BatteryHeating) inner["controlParamsMap"] = Protocol.Json(new { batteryHeatSwitch = s.Enabled ? 1 : 0 });
        if (command == VehicleCommand.ChangeSeatClimate)
        {
            var state = ClimateSnapshot.Parse(await ReadObject("/control/getStatusNow", approval.Vin, cancellation));
            inner["controlParamsMap"] = SeatParameters(s, state, capabilities);
        }
        return inner;
    }
    internal static string ClimateBookingParameters(CommandSettings s)
    {
        var parameters = s.BookingOperation == BookingOperation.Remove
            ? new JsonObject { ["airAccuracy"] = 1, ["airConditioningMode"] = 1, ["cycleMode"] = 2 }
            : JsonNode.Parse(ClimateParameters(s.TemperatureC, s.DurationMinutes))!.AsObject();
        parameters.Remove("airSet"); parameters["remoteMode"] = (int)s.BookingOperation + 1; parameters["acSwitch"] = 0;
        if (s.BookingOperation == BookingOperation.Create) parameters["windMode"] = 0;
        if (s.BookingId != null) parameters["bookingId"] = s.BookingId.Value;
        if (s.BookingOperation != BookingOperation.Remove && s.Departure != null) parameters["bookingTime"] = s.Departure.Value.ToUnixTimeSeconds();
        return parameters.ToJsonString();
    }
    internal static string SeatParameters(CommandSettings settings, ClimateSnapshot state, VehicleCapabilities capabilities)
    {
        var parameters = new SortedDictionary<string, object?> { ["remoteMode"] = 1, ["chairType"] = settings.Seat switch { SeatFeature.DriverHeating or SeatFeature.DriverVentilation => "1", SeatFeature.PassengerHeating or SeatFeature.PassengerVentilation => "2", _ => "5" }, ["lrThirdHeatState"] = 0, ["lrThirdVentilationState"] = 0, ["rrThirdHeatState"] = 0, ["rrThirdVentilationState"] = 0 };
        foreach (var (status, command) in SeatWire.Keys)
        {
            var current = state.SeatStates.GetValueOrDefault(status);
            parameters[command] = status == "steeringWheelHeatState" ? current switch { -1 => 1, 1 => 3, _ => 0 } : current switch { 1 => 3, 2 => 2, 3 => 1, _ => 0 };
        }
        // Never replace another advertised feature's unknown state with an off/default value.
        foreach (var feature in Enum.GetValues<SeatFeature>())
            if (feature != settings.Seat && capabilities.SupportsSeat(feature) && state.GetSeat(feature) == null) throw new BydException(BydError.NoData);
        if (capabilities.Has("10030007") && state.SeatStates.GetValueOrDefault("lrSeatHeatState") is not (1 or 2 or 3)) throw new BydException(BydError.NoData);
        if (capabilities.Has("10030009") && state.SeatStates.GetValueOrDefault("rrSeatHeatState") is not (1 or 2 or 3)) throw new BydException(BydError.NoData);
        var key = SeatWire.Keys.Single(pair => pair.Status == SeatWire.StatusKey(settings.Seat)).Command;
        parameters[key] = settings.Level switch { ComfortLevel.Off => 3, ComfortLevel.Low => 2, ComfortLevel.High => 1, _ => throw new ArgumentException("Invalid comfort level.") };
        return Protocol.Json(parameters);
    }
}
