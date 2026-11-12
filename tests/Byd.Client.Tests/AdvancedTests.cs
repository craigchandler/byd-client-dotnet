using System.Text.Json.Nodes;
using Xunit;

namespace Byd.Client.Tests;

public partial class ClientSafetyTests
{
    private static Server AdvancedServer(bool venting = false)
    {
        var server = new Server();
        server.ReplyJson["/app/account/getAllListByUserId"] = "[{\"vin\":\"SYNTHETIC000000001\",\"modelName\":\"Synthetic Atto 3\",\"energyType\":\"0\",\"vehicleFunLearnInfo\":{\"openWindowLearnInfo\":" + (venting ? "1" : "0") + "}}]";
        server.ReplyJson["/vehicle/vehicleswitch/getLatestConfig"] = new JsonObject { [Vin] = new JsonObject { ["cfFixedList"] = new JsonArray(new[] { "1005", "1006", "1001", "1026", "1020", "1021", "1012", "10300002", "10030001", "10030002", "10030004", "10030005", "10030010" }.Select(code => (JsonNode)new JsonObject { ["functionNo"] = code }).ToArray()) } }.ToJsonString();
        server.ReplyJson["/control/smartCharge/homePage"] = "{\"soc\":78,\"chargingState\":1,\"connectState\":1,\"fullHour\":1,\"fullMinute\":35,\"smartChargeDto\":{\"startChargeTime\":\"23:00\",\"endChargeTime\":\"full\",\"chargeWay\":\"0,1,2,3,4\",\"status\":\"1\"}}";
        server.ReplyJson["/control/getStatusNow"] = "{\"tempInCar\":27,\"tempOutCar\":23,\"acSwitch\":0,\"mainSeatHeatState\":1,\"mainSeatVentilationState\":3,\"copilotSeatHeatState\":2,\"copilotSeatVentilationState\":1,\"steeringWheelHeatState\":-1}";
        server.ReplyJson["/control/getBookingList"] = "{\"listInfo\":[{\"bookingId\":1234567890123456789,\"bookingTime\":1900000000,\"mainSettingTempNew\":21,\"timeSpan\":2}]}";
        server.ReplyJson["/vehicleInfo/vehicle/getEnergyConsumption"] = "{\"cumulativeEnergyConsumption\":{\"avgEvConsumption\":\"15.2\",\"totalMileage\":12500},\"nearestEnergyConsumption\":{\"avgEvConsumption\":14.7,\"driveDistribution\":76,\"airDistribution\":18,\"otherDistribution\":6},\"selfGraph\":{\"energyConsumption\":[15.1,14.2,-1,0],\"energyConsumptionUnit\":\"kWh/100 km\"}}";
        server.ReplyJson["/control/smartCharge/changeChargeStatue"] = "{\"requestSerial\":\"charge-1\"}";
        server.ReplyJson["/control/smartCharge/saveOrUpdate"] = "{\"requestSerial\":\"charge-1\"}";
        server.ReplyJson["/control/smartCharge/changeResult"] = "{\"res\":2}";
        return server;
    }
    [Fact]
    public async Task Advanced_reads_never_verify_pin_or_dispatch_and_keep_missing_data_unknown()
    {
        var server = AdvancedServer(); using var client = Make(server); await Connect(client);
        var capabilities = await client.GetCapabilitiesAsync(Vin);
        Assert.True(capabilities.Supports(VehicleCommand.CloseWindows)); Assert.False(capabilities.Supports(VehicleCommand.VentWindows));
        var charging = await client.GetChargingAsync(Vin); Assert.Equal(95, charging.MinutesToFull); Assert.True(charging.Schedule!.UntilFull); Assert.Null(charging.Schedule.End); Assert.Equal("Weekdays", charging.Schedule.Repeat);
        var climate = await client.GetClimateAsync(Vin); Assert.Equal(27, climate.CabinTemperatureC); Assert.Equal(ComfortLevel.High, climate.GetSeat(SeatFeature.SteeringWheelHeating));
        var bookings = await client.GetClimateBookingsAsync(Vin); Assert.Equal(1234567890123456789L, Assert.Single(bookings.Items).Id); Assert.Equal(15, bookings.Items[0].DurationMinutes);
        var energy = await client.GetEnergyAsync(Vin); Assert.Equal(15.2, energy.LifetimeKwhPer100Km); Assert.Equal(new double?[] { 15.1, 14.2, null, 0 }, energy.SevenDayConsumption);
        var request = server.InnerRequests.Last(); Assert.Equal(0, request["requestType"]!.GetValue<int>()); Assert.Equal("0", request["powerType"]!.GetValue<string>()); Assert.Equal("Synthetic Atto 3", request["autoModelNameOut"]!.GetValue<string>());
        Assert.DoesNotContain(server.Paths, p => p.EndsWith("verifyControlPassword") || p is "/control/remoteControl" or "/control/smartCharge/saveOrUpdate" or "/control/smartCharge/changeChargeStatue");
    }
    [Theory]
    [InlineData(VehicleCommand.CloseWindows, "CLOSEWINDOW")]
    [InlineData(VehicleCommand.VentWindows, "OPENWINDOW")]
    [InlineData(VehicleCommand.OpenBoot, "OPENTRUNK")]
    [InlineData(VehicleCommand.CloseBoot, "CLOSETRUNK")]
    [InlineData(VehicleCommand.BatteryHeating, "BATTERYHEAT")]
    public async Task Extra_remote_commands_require_single_use_approval_and_keep_wire_actions_distinct(VehicleCommand command, string expected)
    {
        var server = AdvancedServer(true); using var client = Make(server, true); await Connect(client);
        var approval = client.ConfirmCommand(Vin, command, new CommandSettings { Enabled = false });
        Assert.Equal(CommandOutcome.Confirmed, await client.ExecuteAsync(approval, "123456"));
        Assert.Equal(1, server.Paths.Count(p => p == "/control/remoteControl"));
        var payload = server.InnerRequests.Single(n => n["commandType"]?.ToString() == expected && n["commandPwd"]?.ToString() != "");
        if (command == VehicleCommand.BatteryHeating) Assert.Equal(0, JsonNode.Parse(payload["controlParamsMap"]!.ToString())!["batteryHeatSwitch"]!.GetValue<int>());
        Assert.Equal(BydError.ApprovalRequired, (await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(approval, "123456"))).Error);
    }
    [Fact]
    public async Task Venting_requires_learning_flag_even_with_window_capability()
    {
        var server = AdvancedServer(); using var client = Make(server, true); await Connect(client);
        var e = await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.VentWindows), "123456"));
        Assert.Equal(BydError.Unsupported, e.Error); Assert.Equal(CommandStage.CapabilityCheck, e.CommandStage);
        Assert.DoesNotContain("/vehicle/vehicleswitch/verifyControlPassword", server.Paths); Assert.DoesNotContain("/control/remoteControl", server.Paths);
    }
    [Theory]
    [InlineData(VehicleCommand.StartCharging, "1")]
    [InlineData(VehicleCommand.StopCharging, "0")]
    public async Task Charging_has_its_own_trigger_and_poll_with_no_remote_control_replay(VehicleCommand command, string status)
    {
        var server = AdvancedServer(); using var client = Make(server, true); await Connect(client);
        Assert.Equal(CommandOutcome.Confirmed, await client.ExecuteAsync(client.ConfirmCommand(Vin, command), "123456"));
        var payload = server.InnerRequests.Single(n => n["status"] != null); Assert.Equal(status, payload["status"]!.GetValue<string>()); Assert.Equal("", payload["timeZone"]!.GetValue<string>()); Assert.Null(payload["commandPwd"]);
        Assert.Equal(1, server.Paths.Count(p => p == "/control/smartCharge/changeChargeStatue")); Assert.DoesNotContain("/control/remoteControl", server.Paths);
        Assert.Contains(server.InnerRequests, n => n["requestSerial"]?.ToString() == "charge-1");
    }
    [Fact]
    public async Task Charging_schedule_parameters_are_bound_and_confirmed_without_resending()
    {
        var server = AdvancedServer(); using var client = Make(server, true); await Connect(client);
        var settings = new CommandSettings { ChargeStart = new(23, 0), ChargeUntilFull = true, Repeat = ChargeRepeat.Weekdays, Enabled = false };
        var approval = client.ConfirmCommand(Vin, VehicleCommand.SaveChargingSchedule, settings);
        var changedCopy = settings with { ChargeStart = new(1, 0) }; Assert.NotEqual(settings, changedCopy);
        Assert.Equal(CommandOutcome.Confirmed, await client.ExecuteAsync(approval, "123456"));
        var payload = server.InnerRequests.Single(n => n["startChargeTime"] != null);
        Assert.Equal("23:00", payload["startChargeTime"]!.ToString()); Assert.Equal("full", payload["endChargeTime"]!.ToString()); Assert.Equal("0,1,2,3,4", payload["chargeWay"]!.ToString()); Assert.Equal("0", payload["status"]!.ToString());
        Assert.Equal(1, server.Paths.Count(p => p == "/control/smartCharge/saveOrUpdate"));
    }
    [Fact]
    public async Task Seat_change_preserves_other_states_and_uses_steering_and_seat_scales_separately()
    {
        var server = AdvancedServer(); using var client = Make(server, true); await Connect(client);
        Assert.Equal(CommandOutcome.Confirmed, await client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.ChangeSeatClimate, new CommandSettings { Seat = SeatFeature.DriverHeating, Level = ComfortLevel.High }), "123456"));
        var payload = server.InnerRequests.Single(n => n["controlParamsMap"] != null); var p = JsonNode.Parse(payload["controlParamsMap"]!.ToString())!;
        Assert.Equal(1, p["mainHeat"]!.GetValue<int>()); Assert.Equal(1, p["mainVentilation"]!.GetValue<int>()); Assert.Equal(2, p["copilotHeat"]!.GetValue<int>()); Assert.Equal(3, p["copilotVentilation"]!.GetValue<int>()); Assert.Equal(1, p["steeringWheelHeatState"]!.GetValue<int>()); Assert.Equal("1", p["chairType"]!.ToString());
    }
    [Fact]
    public async Task Unknown_other_supported_seat_state_blocks_change_before_trigger()
    {
        var server = AdvancedServer(); server.ReplyJson["/control/getStatusNow"] = "{\"mainSeatHeatState\":1}";
        using var client = Make(server, true); await Connect(client);
        var e = await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.ChangeSeatClimate, new CommandSettings { Seat = SeatFeature.DriverHeating, Level = ComfortLevel.High }), "123456"));
        Assert.Equal(BydError.NoData, e.Error); Assert.Contains("No vehicle command was sent", e.Message); Assert.DoesNotContain("/control/remoteControl", server.Paths);
    }
    [Fact]
    public async Task Bookings_bind_64_bit_identity_and_future_departure_and_never_send_on_invalid_settings()
    {
        var server = AdvancedServer(); using var client = Make(server, true); await Connect(client);
        Assert.Throws<ArgumentException>(() => client.ConfirmCommand(Vin, VehicleCommand.ScheduleClimate));
        var when = DateTimeOffset.UtcNow.AddDays(1);
        var settings = new CommandSettings { BookingOperation = BookingOperation.Update, BookingId = 1234567890123456789L, Departure = when, TemperatureC = 22, DurationMinutes = 20 };
        Assert.Equal(CommandOutcome.Confirmed, await client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.ScheduleClimate, settings), "123456"));
        var p = JsonNode.Parse(server.InnerRequests.Single(n => n["controlParamsMap"] != null)["controlParamsMap"]!.ToString())!;
        Assert.Equal(2, p["remoteMode"]!.GetValue<int>()); Assert.Equal(1234567890123456789L, p["bookingId"]!.GetValue<long>()); Assert.Equal(when.ToUnixTimeSeconds(), p["bookingTime"]!.GetValue<long>()); Assert.Null(p["airSet"]); Assert.Equal(8, p["mainSettingTemp"]!.GetValue<int>());
    }
    [Fact]
    public void Detailed_readings_filter_sentinels_and_do_not_guess_tyre_units_or_minutes()
    {
        var d = VehicleDetails.Parse(JsonNode.Parse("{\"tempInCar\":-129,\"tempOutCar\":0,\"totalMileage\":-1,\"leftFrontTirePressure\":2.5,\"tirePressUnit\":1,\"fullHour\":1,\"fullMinute\":-1}")!);
        Assert.Null(d.CabinTemperatureC); Assert.Equal(0, d.OutsideTemperatureC); Assert.Null(d.OdometerKm); Assert.Equal(250, d.FrontLeftTyreKpa); Assert.Null(d.MinutesToFull);
        Assert.Null(VehicleDetails.Parse(JsonNode.Parse("{\"leftFrontTirePressure\":2.5}")!).FrontLeftTyreKpa);
    }
    [Fact]
    public async Task Unsupported_read_and_failed_charge_result_are_safe_and_do_not_retry()
    {
        var server = AdvancedServer(); server.ReplyCode["/control/smartCharge/homePage"] = "1001";
        using var client = Make(server, true); await Connect(client);
        Assert.Equal(BydError.Unsupported, (await Assert.ThrowsAsync<BydException>(() => client.GetChargingAsync(Vin))).Error);
        server.ReplyCode["/control/smartCharge/changeResult"] = "6002";
        var e = await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.StartCharging), "123456"));
        Assert.Equal(BydError.OutcomeUnknown, e.Error); Assert.Equal(1, server.Paths.Count(p => p == "/control/smartCharge/changeChargeStatue")); Assert.DoesNotContain("private-token", e.ToString());
    }
}
