using System.Text.Json.Nodes;
using Xunit;

namespace Byd.Client.Tests;

public partial class ClientSafetyTests
{
    [Fact]
    public async Task Local_opt_in_preserves_the_session_and_selection_without_sending_a_request()
    {
        var server = new Server(); using var client = Make(server); await Connect(client);
        Assert.False(client.RemoteControlsEnabled);
        var before = server.Paths.ToArray();
        await client.SetRemoteControlsEnabledAsync(true);
        Assert.True(client.RemoteControlsEnabled); Assert.Equal(before, server.Paths);
        var approval = client.ConfirmCommand(Vin, VehicleCommand.Lock);
        Assert.Equal(before, server.Paths); // Neither permission nor approval verifies a PIN or dispatches.
        Assert.Equal(CommandOutcome.Confirmed, await client.ExecuteAsync(approval, "123456"));
        Assert.Equal(1, server.Paths.Count(p => p == "/app/account/login"));
        Assert.Equal(1, server.Paths.Count(p => p == "/vehicle/vehicleswitch/getLatestConfig"));
        Assert.Equal(1, server.Paths.Count(p => p == "/vehicle/vehicleswitch/verifyControlPassword"));
        Assert.Equal(1, server.Paths.Count(p => p == "/control/remoteControl"));
    }

    [Fact]
    public async Task Disabling_blocks_every_control_entry_and_reenabling_requires_a_new_approval()
    {
        var server = new Server(); using var client = Make(server, true); await Connect(client);
        Assert.True(client.RemoteControlsEnabled);
        var old = client.ConfirmCommand(Vin, VehicleCommand.Lock);
        var before = server.Paths.ToArray();
        await client.SetRemoteControlsEnabledAsync(false);
        Assert.False(client.RemoteControlsEnabled);
        Assert.Equal(BydError.ControlsDisabled, Assert.Throws<BydException>(() => client.ConfirmCommand(Vin, VehicleCommand.Lock)).Error);
        Assert.Equal(BydError.ControlsDisabled, (await Assert.ThrowsAsync<BydException>(() => client.CheckCommandAccessAsync(Vin, VehicleCommand.Lock, "123456"))).Error);
        Assert.Equal(BydError.ControlsDisabled, (await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(old, "123456"))).Error);
        await client.SetRemoteControlsEnabledAsync(true);
        Assert.Equal(BydError.ApprovalRequired, (await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(old, "123456"))).Error);
        Assert.Equal(before, server.Paths);
        Assert.Equal(CommandOutcome.Confirmed, await client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.Lock), "123456"));
        Assert.Equal(1, server.Paths.Count(p => p == "/app/account/login"));
    }

    [Fact]
    public async Task Local_opt_in_grants_neither_authentication_nor_approval_or_pin_access()
    {
        var server = new Server(); using var client = Make(server);
        await client.SetRemoteControlsEnabledAsync(true);
        Assert.Empty(server.Paths);
        Assert.Equal(BydError.AuthenticationRequired, Assert.Throws<BydException>(() => client.ConfirmCommand(Vin, VehicleCommand.Lock)).Error);
        await Connect(client);
        var before = server.Paths.ToArray();
        Assert.Equal(BydError.ApprovalRequired, (await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(null!, "123456"))).Error);
        Assert.Equal(BydError.InvalidPin, (await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.Lock), ""))).Error);
        Assert.Equal(before, server.Paths);
        server.ReplyCode["/vehicle/vehicleswitch/verifyControlPassword"] = "5005";
        var rejected = await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.Lock), "123456"));
        Assert.Equal(BydError.PinRejected, rejected.Error);
        Assert.Equal(CommandStage.PinVerification, rejected.CommandStage);
        Assert.DoesNotContain("/control/remoteControl", server.Paths);
    }

    [Fact]
    public async Task Permission_changes_wait_for_the_current_operation_without_sending_an_extra_request()
    {
        var server = new Server(); using var client = Make(server); await Connect(client);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnRequest = async (_, token) => { started.TrySetResult(); await finish.Task.WaitAsync(token); };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = client.GetVehiclesAsync(deadline.Token); await started.Task.WaitAsync(deadline.Token);
        var change = client.SetRemoteControlsEnabledAsync(true, deadline.Token);
        Assert.False(change.IsCompleted); Assert.False(client.RemoteControlsEnabled);
        finish.TrySetResult(); await read; await change;
        Assert.True(client.RemoteControlsEnabled);
        Assert.Equal(new[] { "/app/account/login", "/app/account/getAllListByUserId", "/app/account/getAllListByUserId" }, server.Paths);
    }

    [Fact]
    public async Task Cancelled_wait_cannot_silently_enable_controls()
    {
        var server = new Server(); using var client = Make(server); await Connect(client);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnRequest = async (_, token) => { started.TrySetResult(); await finish.Task.WaitAsync(token); };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = client.GetVehiclesAsync(deadline.Token); await started.Task.WaitAsync(deadline.Token);
        using var cancel = new CancellationTokenSource();
        var change = client.SetRemoteControlsEnabledAsync(true, cancel.Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change);
        finish.TrySetResult(); await read;
        Assert.False(client.RemoteControlsEnabled);
        Assert.Equal(3, server.Paths.Count);
    }

    [Fact]
    public async Task Disposed_clients_report_controls_off_and_cannot_change_permission()
    {
        var server = new Server(); using var client = Make(server, true);
        client.Dispose(); Assert.False(client.RemoteControlsEnabled);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SetRemoteControlsEnabledAsync(true));
        Assert.Empty(server.Paths);
    }
}

public class TypedStatusTests
{
    [Theory]
    [InlineData("{}", VehicleChargingState.Unknown)]
    [InlineData("{\"chargeState\":0}", VehicleChargingState.NotCharging)]
    [InlineData("{\"elecPercent\":100,\"chargeState\":15}", VehicleChargingState.NotCharging)]
    [InlineData("{\"elecPercent\":100,\"chargeState\":1}", VehicleChargingState.Charging)]
    [InlineData("{\"chargeState\":99,\"chargingState\":1}", VehicleChargingState.Unknown)]
    [InlineData("{\"chargeState\":null,\"chargingState\":1}", VehicleChargingState.Unknown)]
    public void Charging_uses_authoritative_state_without_inferring_from_percentage_or_secondary_fields(string json, VehicleChargingState expected)
        => Assert.Equal(expected, VehicleStatus.Parse(JsonNode.Parse(json)!, DateTimeOffset.UtcNow).ChargingState);

    [Theory]
    [InlineData("{}", VehicleLockState.Unknown)]
    [InlineData("{\"leftFrontDoorLock\":2}", VehicleLockState.Unknown)]
    [InlineData("{\"leftFrontDoorLock\":2,\"rightFrontDoorLock\":2,\"leftRearDoorLock\":2,\"rightRearDoorLock\":2}", VehicleLockState.Locked)]
    [InlineData("{\"leftFrontDoorLock\":1}", VehicleLockState.Unlocked)]
    [InlineData("{\"leftFrontDoorLock\":99,\"rightFrontDoorLock\":1}", VehicleLockState.Unlocked)]
    public void Locks_preserve_unknown_completeness_and_positive_unlock_evidence(string json, VehicleLockState expected)
        => Assert.Equal(expected, VehicleStatus.Parse(JsonNode.Parse(json)!, DateTimeOffset.UtcNow).LockState);

    [Fact]
    public void Compatibility_constructors_and_with_expressions_cannot_leave_typed_properties_stale()
    {
        var now = DateTimeOffset.UtcNow;
        var status = new VehicleStatus(100, 325, "Not charging", "Locked", now, now, true);
        Assert.Equal(VehicleChargingState.NotCharging, status.ChargingState); Assert.Equal(VehicleLockState.Locked, status.LockState);
        var changed = status with { Charging = "Charging", Locks = "Unlocked" };
        Assert.Equal(VehicleChargingState.Charging, changed.ChargingState); Assert.Equal(VehicleLockState.Unlocked, changed.LockState);
        var future = status with { Charging = "Unrecognized synthetic state", Locks = "Unrecognized synthetic state" };
        Assert.Equal(VehicleChargingState.Unknown, future.ChargingState); Assert.Equal(VehicleLockState.Unknown, future.LockState);
        Assert.Equal("Not charging", status.Charging); Assert.Equal("Locked", status.Locks);
    }
}
