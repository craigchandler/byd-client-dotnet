using Xunit;

namespace Byd.Client.Tests;

public partial class ClientSafetyTests
{
    [Fact]
    public async Task An_unlisted_vehicle_fails_before_a_vehicle_request_without_echoing_its_identity()
    {
        var server = new Server(); using var client = Make(server); await Connect(client);
        var before = server.Paths.Count;
        var error = await Assert.ThrowsAsync<BydException>(() => client.GetStatusAsync("SYNTHETIC000000002"));
        Assert.Equal(BydError.VehicleNotSelected, error.Error);
        Assert.DoesNotContain("SYNTHETIC", error.ToString());
        Assert.Equal(before, server.Paths.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"token\":{\"userId\":{},\"signToken\":\"synthetic\",\"encryToken\":\"synthetic\"}}")]
    [InlineData("[]")]
    public async Task Malformed_login_clears_the_previous_session_and_never_retries(string reply)
    {
        var server = new Server(); using var client = Make(server); await Connect(client);
        server.LoginReplyJson = reply;
        var error = await Assert.ThrowsAsync<BydException>(() => client.LoginAsync("synthetic@example.invalid", Password));
        Assert.Equal(BydError.InvalidResponse, error.Error);
        Assert.Null(error.InnerException);
        Assert.Equal(BydError.AuthenticationRequired, (await Assert.ThrowsAsync<BydException>(() => client.GetVehiclesAsync())).Error);
        Assert.Equal(2, server.Paths.Count(p => p == "/app/account/login"));
    }

    [Theory]
    [InlineData(false, BydError.Network)]
    [InlineData(true, BydError.Cancelled)]
    public async Task Preflight_timeout_and_user_cancellation_are_distinct_and_cannot_dispatch(bool cancel, BydError expected)
    {
        var server = new Server(); using var client = Make(server, true); await Connect(client);
        using var stop = new CancellationTokenSource();
        server.OnRequest = (path, _) =>
        {
            if (path.EndsWith("getLatestConfig"))
            {
                if (cancel) stop.Cancel();
                throw new OperationCanceledException("synthetic-private-diagnostic");
            }
            return Task.CompletedTask;
        };
        var error = await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.Lock), "123456", stop.Token));
        Assert.Equal(expected, error.Error); Assert.Equal(CommandStage.CapabilityCheck, error.CommandStage);
        Assert.DoesNotContain("synthetic-private-diagnostic", error.ToString());
        Assert.DoesNotContain("/control/remoteControl", server.Paths);
    }

    [Fact]
    public async Task Cancellation_after_dispatch_reports_unknown_consumes_approval_and_never_replays()
    {
        var server = new Server(); using var client = Make(server, true); await Connect(client);
        using var stop = new CancellationTokenSource();
        server.OnRequest = (path, _) =>
        {
            if (path == "/control/remoteControl") { stop.Cancel(); throw new OperationCanceledException("synthetic-private-diagnostic"); }
            return Task.CompletedTask;
        };
        var approval = client.ConfirmCommand(Vin, VehicleCommand.Lock);
        var error = await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(approval, "123456", stop.Token));
        Assert.Equal(BydError.OutcomeUnknown, error.Error); Assert.Equal(CommandStage.Dispatch, error.CommandStage);
        Assert.Equal(BydError.ApprovalRequired, (await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(approval, "123456"))).Error);
        Assert.Equal(1, server.Paths.Count(p => p == "/control/remoteControl"));
        Assert.DoesNotContain("synthetic-private-diagnostic", error.ToString());
    }

    [Fact]
    public async Task Read_cancellation_preserves_the_callers_token_and_discards_transport_diagnostics()
    {
        var server = new Server(); using var client = Make(server); await Connect(client);
        using var stop = new CancellationTokenSource();
        server.OnRequest = (_, _) => { stop.Cancel(); throw new OperationCanceledException("synthetic-private-diagnostic"); };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetVehiclesAsync(stop.Token));
        Assert.Equal(stop.Token, error.CancellationToken); Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-private-diagnostic", error.ToString());
    }

    [Fact]
    public async Task A_queued_operation_cannot_send_after_disposal()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new Server(); using var client = Make(server); await Connect(client);
        server.OnRequest = async (_, _) => { started.TrySetResult(); await finish.Task; };
        var active = client.GetVehiclesAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = client.GetVehiclesAsync(); var requests = server.Paths.Count;
        client.Dispose(); client.Dispose(); finish.TrySetResult();
        try { await active; } catch (ObjectDisposedException) { }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetVehiclesAsync());
        Assert.Equal(requests, server.Paths.Count);
    }

    [Fact]
    public async Task Missing_approval_pin_or_settings_fail_safely_before_dispatch()
    {
        var server = new Server(); using var client = Make(server, true); await Connect(client);
        var requests = server.Paths.Count;
        Assert.Throws<ArgumentNullException>(() => client.ConfirmCommand(Vin, VehicleCommand.Lock, null!));
        Assert.Equal(BydError.ApprovalRequired, (await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(null!, "123456"))).Error);
        Assert.Equal(BydError.InvalidPin, (await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.Lock), null!))).Error);
        Assert.Equal(requests, server.Paths.Count);
    }
}

public class PublicPrivacyTests
{
    [Fact]
    public void Portable_symbols_map_every_source_document_away_from_private_build_paths()
    {
        using var stream = File.OpenRead(Path.ChangeExtension(typeof(BydClient).Assembly.Location, ".pdb"));
        using var provider = System.Reflection.Metadata.MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        Assert.NotEmpty(reader.Documents);
        Assert.All(reader.Documents, handle => Assert.StartsWith("/_/", reader.GetString(reader.GetDocument(handle).Name)));
    }

    [Fact]
    public void Ordinary_string_formatting_redacts_identifiers_telemetry_and_bookings()
    {
        var time = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        object[] values = [
            new Vehicle("SYNTHETIC000000001", "Synthetic private name", 0),
            new VehicleStatus(37.123, 221.456, "Charging", "Unlocked", time, time, true),
            new VehicleOpenings(2, 3, true, true, true, false, true),
            new VehicleDetails { OdometerKm = 12345.67 },
            new ChargingSchedule(new(3, 4), new(5, 6), false, "Daily", true),
            new JourneySchedule(new(3, 4), new(5, 6), new(7, 8), "Daily", true),
            new ChargingSnapshot(37.123, true, true, 49, null, null, time, time, "Synthetic zone"),
            new ClimateBooking(1234567890123456789, time, 23, 10),
            new ClimateBookings([new(1234567890123456789, time, 23, 10)], time),
            new ClimateSnapshot(23, 17, true, time),
            new EnergySnapshot(12345.67, 18, 16, [17], "Synthetic unit", 80, 10, 10, time, time),
            new VehicleLocation(-33.8568, 151.2153, time, time)
        ];
        Assert.All(values, value =>
        {
            var text = value.ToString()!;
            foreach (var sensitive in new[] { "SYNTHETIC000000001", "Synthetic private name", "37.123", "221.456", "12345.67", "1234567890123456789", "Synthetic zone", "Synthetic unit", "2026-01-02", "-33.8568", "151.2153" })
                Assert.DoesNotContain(sensitive, text);
            Assert.DoesNotContain("{", text); // Default record formatting exposes named fields.
        });
    }

    [Fact]
    public void Only_the_intended_namespace_and_no_protocol_or_transport_types_are_public()
    {
        var types = typeof(BydClient).Assembly.GetExportedTypes();
        Assert.All(types, type => Assert.Equal("Byd.Client", type.Namespace));
        Assert.DoesNotContain(types, type => type.Name is "Session" or "Protocol" or "Bangcle" or "MqttStatusFeedConnector");
    }
}
