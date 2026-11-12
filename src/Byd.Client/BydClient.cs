using System.Net;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Byd.Client.Crypto;
using Byd.Client.Telemetry;

namespace Byd.Client;

/// <summary>Experimental international BYD client. No automatic sign-in or command retries.</summary>
public sealed partial class BydClient : IDisposable
{
    private readonly HttpClient http;
    private readonly BydClientOptions options;
    private readonly IStatusFeedConnector? statusFeedConnector;
    private readonly Bangcle codec = new();
    private readonly SemaphoreSlim operation = new(1, 1);
    private Guid id = Guid.NewGuid();
    private Session? session;
    private Dictionary<string, Vehicle> vehicles = [];
    private int disposeState;
    private bool remoteControlsEnabled;
    private bool IsDisposed => Volatile.Read(ref disposeState) != 0;

    /// <summary>Current local control opt-in, initially taken from BydClientOptions.EnableRemoteControls. Does not imply an authenticated session, vehicle support or command approval. False after disposal.</summary>
    public bool RemoteControlsEnabled => !IsDisposed && Volatile.Read(ref remoteControlsEnabled);

    /// <summary>Creates a client owning its HTTP/MQTT resources. No network request is made until an operation is called.</summary>
    public BydClient(BydClientOptions? options = null) : this(options ?? new(), new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        CookieContainer = new CookieContainer(),
        AutomaticDecompression = DecompressionMethods.None
    }, MqttStatusFeedConnector.Instance)
    { }
    // Test-only transport injection; production callers cannot redirect credentials to another host.
    internal BydClient(BydClientOptions options, HttpMessageHandler handler, IStatusFeedConnector? statusFeedConnector = null)
    {
        ArgumentNullException.ThrowIfNull(options.Market);
        if (options.PollAttempts is < 1 or > 30 || options.PollInterval < TimeSpan.Zero || options.PollInterval.TotalMilliseconds > uint.MaxValue - 1
            || options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout.TotalMilliseconds > int.MaxValue
            || options.PushTimeout <= TimeSpan.Zero || options.PushTimeout > TimeSpan.FromMinutes(1) || string.IsNullOrWhiteSpace(options.TimeZone))
            throw new ArgumentException("Invalid request limits.", nameof(options));
        this.options = options;
        remoteControlsEnabled = options.EnableRemoteControls;
        this.statusFeedConnector = statusFeedConnector;
        http = new HttpClient(handler, true) { Timeout = options.RequestTimeout, MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("okhttp/4.12.0");
        http.DefaultRequestHeaders.Add("accept-encoding", "identity");
    }
    private async Task<T> Run<T>(Func<Task<T>> action, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        await operation.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            cancellation.ThrowIfCancellationRequested();
            return await action().ConfigureAwait(false);
        }
        catch (BydException) { throw; }
        catch (ObjectDisposedException) { throw new ObjectDisposedException(nameof(BydClient)); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw new OperationCanceledException(cancellation); }
        catch (OperationCanceledException) { throw new BydException(BydError.Network); }
        catch (HttpRequestException) { throw new BydException(BydError.Network); }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException or InvalidOperationException or ArgumentException or OverflowException)
        { throw new BydException(BydError.InvalidResponse); }
        finally
        {
            if (IsDisposed) { session = null; vehicles.Clear(); }
            operation.Release();
        }
    }
    /// <summary>Changes local control opt-in without network requests, reauthentication or clearing vehicles/readings. Actual permission changes invalidate all previously issued command approvals.</summary>
    /// <remarks>Serialized with other async operations: await completion before relying on the new permission. Does not interrupt a running operation or undo a dispatched command. Confirmation and PIN verification are still required for every command.</remarks>
    /// <param name="enabled">Explicit local permission requested by the person using the application; never persist as automatic startup opt-in.</param>
    /// <param name="cancellation">Cancels while waiting to apply the change; a cancelled wait leaves the permission unchanged.</param>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled before the change could be applied.</exception>
    public Task SetRemoteControlsEnabledAsync(bool enabled, CancellationToken cancellation = default) => Run(() =>
    {
        if (remoteControlsEnabled != enabled)
        {
            id = Guid.NewGuid();
            Volatile.Write(ref remoteControlsEnabled, enabled);
        }
        return Task.FromResult(true);
    }, cancellation);
    /// <summary>Explicitly authenticates an international email account. Clears previous session, vehicle list and approvals before attempting sign-in; never retries or stores the password.</summary>
    /// <param name="username">International BYD account email from secure local input; outer whitespace is trimmed.</param>
    /// <param name="password">Account password from secure local input; whitespace is preserved and the password is not saved.</param>
    /// <param name="cancellation">Caller cancellation/deadline. After possible command dispatch, cancellation means unknown outcome.</param>
    /// <exception cref="BydException">Sanitized authentication, selection, protocol or command failure; inspect Error and CommandStage.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public Task LoginAsync(string username, string password, CancellationToken cancellation = default) => Run(async () =>
    {
        session = null; vehicles.Clear(); id = Guid.NewGuid();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) throw new BydException(BydError.AuthenticationRejected);
        // Email login is the verified reference flow. Password whitespace is preserved.
        username = username.Trim();
        var response = await Post("/app/account/login", Protocol.Login(username, password, options, Now(), Random()), cancellation);
        CheckCode(response, login: true);
        var decoded = JsonNode.Parse(Protocol.Decrypt(Required(response, "respondData"), Protocol.LoginKey(password)));
        var token = decoded?["token"] ?? throw new BydException(BydError.InvalidResponse);
        session = new Session(Required(token, "userId"), Protocol.Md5(Required(token, "signToken")), Protocol.Md5(Required(token, "encryToken")), Protocol.Md5(username), DateTimeOffset.UtcNow)
        { RawSignToken = Required(token, "signToken"), CountryCode = options.Market.CountryCode, Language = options.Market.Language };
        return true;
    }, cancellation);

    /// <summary>Lists vehicles in the signed-in account and replaces the local selection catalog. Call after every successful sign-in.</summary>
    /// <param name="cancellation">Caller cancellation/deadline. After possible command dispatch, cancellation means unknown outcome.</param>
    /// <exception cref="BydException">Sanitized authentication, selection, protocol or command failure; inspect Error and CommandStage.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public Task<IReadOnlyList<Vehicle>> GetVehiclesAsync(CancellationToken cancellation = default) => Run<IReadOnlyList<Vehicle>>(async () =>
    {
        var node = await TokenPost("/app/account/getAllListByUserId", Inner(), cancellation);
        if (node is not JsonArray items) throw new BydException(BydError.InvalidResponse);
        var result = items.Where(n => n != null).Select(n => new Vehicle(Required(n!, "vin"), n!["modelName"]?.ToString() ?? "BYD", (int)(VehicleStatus.Number(n!, "energyType") ?? 0))
        { SupportsWindowVenting = n!["vehicleFunLearnInfo"] is JsonObject learn && (VehicleStatus.Number(learn, "openWindowLearnInfo") == 1 || VehicleStatus.Number(learn, "openWindow499LearnInfo") == 1) }).ToList();
        vehicles = result.ToDictionary(v => v.Vin, StringComparer.Ordinal);
        return result;
    }, cancellation);

    /// <summary>Requests realtime status once, then races bounded push/result lookups. May wake the vehicle; never requests GPS or actuates controls.</summary>
    /// <param name="vin">VIN selected from the latest GetVehiclesAsync result in this session; never log it.</param>
    /// <param name="cancellation">Caller cancellation/deadline. After possible command dispatch, cancellation means unknown outcome.</param>
    /// <exception cref="BydException">Sanitized authentication, selection, protocol or command failure; inspect Error and CommandStage.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public Task<VehicleStatus> GetStatusAsync(string vin, CancellationToken cancellation = default) => Run(async () =>
    {
        var vehicle = RequireVehicle(vin);
        Dictionary<string, string> Payload(string? serial = null)
        {
            var inner = Inner(vin, serial); inner["energyType"] = vehicle.EnergyType.ToString(System.Globalization.CultureInfo.InvariantCulture); inner["tboxVersion"] = "3"; return inner;
        }
        IStatusFeed? feed = null;
        var feedUnavailable = false;
        if (options.EnablePushTelemetry && statusFeedConnector != null)
        {
            try
            {
                var broker = await TokenPost("/app/emqAuth/getEmqBrokerIp", Inner(), cancellation);
                var address = broker["emqBorker"]?.ToString() ?? broker["emqBroker"]?.ToString() ?? throw new BydException(BydError.InvalidResponse);
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                connect.CancelAfter(TimeSpan.FromSeconds(10));
                feed = await statusFeedConnector.ConnectAsync(address, RequireSession(), vin, connect.Token);
            }
            catch (BydException error) when (error.Error == BydError.AuthenticationRequired) { throw; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch { feedUnavailable = true; }
        }
        using var activeFeed = feed;
        var triggeredAt = DateTimeOffset.UtcNow;
        var response = await TokenPost("/vehicleInfo/vehicle/vehicleRealTimeRequest", Payload(), cancellation);
        var serial = response["requestSerial"]?.ToString();
        var status = VehicleStatus.Parse(response, DateTimeOffset.UtcNow);
        if (status.HasUsableData) return status with { Source = TelemetrySource.Http };
        if (string.IsNullOrEmpty(serial)) throw new BydException(BydError.NoData);
        var placeholder = status.BatteryPercent == 0 || status.RangeKm == 0;
        VehicleStatus? olderHttp = null;
        using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        async Task<VehicleStatus?> Poll()
        {
            for (var i = 0; i < options.PollAttempts; i++)
            {
                await Task.Delay(options.PollInterval, read.Token);
                var result = await TokenPost("/vehicleInfo/vehicle/vehicleRealTimeResult", Payload(serial), read.Token);
                if (result["requestSerial"] is { } echoed && echoed.ToString() != serial) continue;
                var candidate = VehicleStatus.Parse(result, DateTimeOffset.UtcNow) with { Source = TelemetrySource.Http };
                if (candidate.HasUsableData)
                {
                    // An old/undated cached HTTP snapshot must not outrun a fresh push for this request.
                    if (feed == null || candidate.VehicleTime >= triggeredAt - TimeSpan.FromSeconds(5) && !candidate.IsStale(DateTimeOffset.UtcNow)) return candidate;
                    olderHttp = candidate;
                }
                placeholder |= candidate.BatteryPercent == 0 || candidate.RangeKm == 0;
            }
            return null;
        }
        var readers = new List<Task<VehicleStatus?>>();
        if (feed != null) readers.Add(feed.WaitAsync(serial, options.PushTimeout, read.Token));
        readers.Add(Poll());
        var allReaders = readers.ToArray();
        var pushReader = feed != null ? readers[0] : null;
        Exception? failure = null;
        try
        {
            while (readers.Count > 0)
            {
                var completed = await Task.WhenAny(readers);
                readers.Remove(completed);
                try
                {
                    var result = await completed;
                    cancellation.ThrowIfCancellationRequested();
                    if (result != null) return completed == pushReader ? result with { Source = TelemetrySource.LivePush } : result;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (BydException error) when (error.Error == BydError.AuthenticationRequired) { throw; }
                catch (Exception error) { failure = error; }
            }
            if (olderHttp != null) return olderHttp;
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            throw new BydException(feedUnavailable ? BydError.LiveTelemetryUnavailable : placeholder ? BydError.PlaceholderData : BydError.NoData);
        }
        finally
        {
            read.Cancel();
            // Observe and finish the losing reader before releasing the client or disconnecting MQTT.
            foreach (var reader in allReaders) { try { await reader; } catch { } }
        }
    }, cancellation);

    /// <summary>Fetch one GPS fix on demand. No PIN, locks, climate or automatic tracking.</summary>
    /// <param name="vin">VIN selected from the latest GetVehiclesAsync result in this session; never log it.</param>
    /// <param name="cancellation">Caller cancellation/deadline. After possible command dispatch, cancellation means unknown outcome.</param>
    /// <exception cref="BydException">Sanitized authentication, selection, protocol or command failure; inspect Error and CommandStage.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public Task<VehicleLocation> GetLocationAsync(string vin, CancellationToken cancellation = default) => Run(async () =>
    {
        RequireVehicle(vin);
        IStatusFeed? feed = null;
        if (options.EnablePushTelemetry && statusFeedConnector != null)
        {
            try
            {
                var broker = await TokenPost("/app/emqAuth/getEmqBrokerIp", Inner(), cancellation);
                var address = broker["emqBorker"]?.ToString() ?? broker["emqBroker"]?.ToString() ?? throw new BydException(BydError.InvalidResponse);
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                connect.CancelAfter(TimeSpan.FromSeconds(10));
                feed = await statusFeedConnector.ConnectAsync(address, RequireSession(), vin, connect.Token, locationOnly: true);
            }
            catch (BydException error) when (error.Error == BydError.AuthenticationRequired) { throw; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch { /* HTTP remains available; no automatic sign-in is attempted. */ }
        }
        using var activeFeed = feed;
        var triggeredAt = DateTimeOffset.UtcNow;
        var response = await TokenPost("/control/getGpsInfo", Inner(vin), cancellation);
        if (VehicleLocation.Parse(response, DateTimeOffset.UtcNow) is { } immediate) return immediate;
        var serial = response["requestSerial"]?.ToString();
        if (string.IsNullOrWhiteSpace(serial)) throw new BydException(BydError.LocationUnavailable);
        using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        VehicleLocation? olderHttp = null;
        async Task<VehicleLocation?> Poll()
        {
            for (var i = 0; i < options.PollAttempts; i++)
            {
                await Task.Delay(options.PollInterval, read.Token);
                var result = await TokenPost("/control/getGpsInfoResult", Inner(vin, serial), read.Token);
                if (result["requestSerial"] is { } echoed && echoed.ToString() != serial) continue;
                if (result["data"] is JsonObject nested && nested["requestSerial"] is { } nestedSerial && nestedSerial.ToString() != serial) continue;
                var location = VehicleLocation.Parse(result, DateTimeOffset.UtcNow);
                if (location == null) continue;
                if (feed == null || location.FixTime >= triggeredAt - TimeSpan.FromSeconds(5) && !location.IsStale(DateTimeOffset.UtcNow)) return location;
                olderHttp = location;
            }
            return null;
        }
        var readers = new List<Task<VehicleLocation?>>();
        if (feed != null) readers.Add(feed.WaitLocationAsync(serial, options.PushTimeout, read.Token));
        readers.Add(Poll());
        var allReaders = readers.ToArray();
        Exception? failure = null;
        try
        {
            while (readers.Count > 0)
            {
                var completed = await Task.WhenAny(readers); readers.Remove(completed);
                try
                {
                    var location = await completed; cancellation.ThrowIfCancellationRequested();
                    if (location != null) return location;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (BydException error) when (error.Error is BydError.AuthenticationRequired or BydError.LocationUnavailable) { throw; }
                catch (Exception error) { failure = error; }
            }
            if (olderHttp != null) return olderHttp;
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            throw new BydException(BydError.LocationUnavailable);
        }
        finally { read.Cancel(); foreach (var reader in allReaders) { try { await reader; } catch { } } }
    }, cancellation);

    /// <summary>Call only after the person confirms this exact vehicle, command and parameters.</summary>
    /// <param name="vin">VIN selected from the latest GetVehiclesAsync result in this session; never log it.</param>
    /// <param name="command">Exact requested action; advertised support is checked before dispatch.</param>
    /// <param name="temperatureC">Climate setpoint, 15 through 31 Celsius.</param>
    /// <param name="durationMinutes">Climate duration: 10, 15, 20, 25 or 30 minutes.</param>
    /// <exception cref="BydException">Sanitized authentication, selection, protocol or command failure; inspect Error and CommandStage.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public CommandApproval ConfirmCommand(string vin, VehicleCommand command, int temperatureC = 21, int durationMinutes = 10)
        => ConfirmCommand(vin, command, new CommandSettings { TemperatureC = temperatureC, DurationMinutes = durationMinutes });
    /// <summary>Captures immutable settings only after explicit confirmation of the exact vehicle/action. Does not send a command. Call after awaiting other operations.</summary>
    /// <param name="vin">VIN selected from the latest GetVehiclesAsync result in this session; never log it.</param>
    /// <param name="command">Exact requested action; advertised support is checked before dispatch.</param>
    /// <param name="settings">Immutable exact parameters shown to the user before approval.</param>
    /// <exception cref="BydException">Sanitized authentication, selection, protocol or command failure; inspect Error and CommandStage.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public CommandApproval ConfirmCommand(string vin, VehicleCommand command, CommandSettings settings)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        if (!RemoteControlsEnabled) throw new BydException(BydError.ControlsDisabled);
        settings.Validate(command);
        RequireVehicle(vin);
        return new(id, vin, command, settings);
    }

    /// <summary>Consumes one exact approval, verifies support and PIN, and dispatches at most once. Unknown results or cancellation after dispatch require checking the official app before another attempt.</summary>
    /// <param name="approval">Fresh exact single-use confirmation bound to this client and vehicle.</param>
    /// <param name="pin">Six ASCII digits from secure local input. Never save or log the PIN or its hash.</param>
    /// <param name="cancellation">Caller cancellation/deadline. After possible command dispatch, cancellation means unknown outcome.</param>
    /// <exception cref="BydException">Sanitized authentication, selection, protocol or command failure; inspect Error and CommandStage.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public Task<CommandOutcome> ExecuteAsync(CommandApproval approval, string pin, CancellationToken cancellation = default) => Run(async () =>
    {
        if (!RemoteControlsEnabled) throw new BydException(BydError.ControlsDisabled);
        if (approval is null || !approval.Consume(id)) throw new BydException(BydError.ApprovalRequired);
        if (pin is null || pin.Length != 6 || !pin.All(char.IsAsciiDigit)) throw new BydException(BydError.InvalidPin);
        RequireVehicle(approval.Vin);
        approval.Settings.Validate(approval.Command);
        var capabilities = await CheckCommandAccessCore(approval.Vin, approval.Command, pin, cancellation, approval.Settings);
        Dictionary<string, string>? payload = null;
        await AtCommandStage(CommandStage.CapabilityCheck, async () => { payload = await BuildCommandPayload(approval, capabilities, pin, cancellation); }, cancellation);
        var charging = IsChargingCommand(approval.Command);
        var triggerEndpoint = charging ? approval.Command == VehicleCommand.SaveChargingSchedule ? "/control/smartCharge/saveOrUpdate" : "/control/smartCharge/changeChargeStatue" : "/control/remoteControl";
        var resultEndpoint = charging ? "/control/smartCharge/changeResult" : "/control/remoteControlResult";
        // From here, a timeout/cancellation can mean the command executed. Never replay.
        var stage = CommandStage.Dispatch;
        try
        {
            var response = await TokenPost(triggerEndpoint, payload!, cancellation);
            if (!MatchesCommandReply(response, approval)) return CommandOutcome.Unknown;
            var outcome = ParseCommandOutcome(response, charging);
            if (outcome != CommandOutcome.Unknown) return outcome;
            var serial = response["requestSerial"]?.ToString();
            if (string.IsNullOrWhiteSpace(serial)) return CommandOutcome.Unknown;
            stage = CommandStage.Result;
            for (var i = 0; i < options.PollAttempts; i++)
            {
                await Task.Delay(options.PollInterval, cancellation);
                var poll = Inner(approval.Vin, serial);
                if (!charging) { poll["commandPwd"] = ""; poll["commandType"] = CommandType(approval.Command); }
                response = await TokenPost(resultEndpoint, poll, cancellation);
                if (!MatchesCommandReply(response, approval, serial)) continue;
                outcome = ParseCommandOutcome(response, charging);
                if (outcome != CommandOutcome.Unknown) return outcome;
            }
            return CommandOutcome.Unknown;
        }
        catch (Exception e) when (e is BydException or HttpRequestException or OperationCanceledException || IsResponseFailure(e))
        { throw new BydException(BydError.OutcomeUnknown, (e as BydException)?.ApiCode, stage); }
    }, cancellation);

    private static bool MatchesCommandReply(JsonNode response, CommandApproval approval, string? requestedSerial = null)
    {
        if (response is not JsonObject reply) throw new BydException(BydError.InvalidResponse);
        if (reply["vin"] is { } vin && vin.ToString() != approval.Vin) return false;
        if (!IsChargingCommand(approval.Command) && reply["commandType"] is { } command && command.ToString() != CommandType(approval.Command)) return false;
        // A direct HTTPS result lookup is already scoped to the serial sent in that request.
        // pyBYD also binds an omitted echo to that serial. An explicit different echo is never accepted.
        // This fallback must not be used for shared-topic MQTT acknowledgements or trigger serial discovery.
        return requestedSerial == null || reply["requestSerial"] is not { } serial || string.IsNullOrEmpty(serial.ToString()) || serial.ToString() == requestedSerial;
    }

    /// <summary>Checks capability and PIN access only. Does not dispatch or approve a vehicle command, or retain the PIN.</summary>
    /// <param name="vin">VIN selected from the latest GetVehiclesAsync result in this session; never log it.</param>
    /// <param name="command">Exact requested action; advertised support is checked before dispatch.</param>
    /// <param name="pin">Six ASCII digits from secure local input. Never save or log the PIN or its hash.</param>
    /// <param name="cancellation">Caller cancellation/deadline. After possible command dispatch, cancellation means unknown outcome.</param>
    /// <exception cref="BydException">Sanitized authentication, selection, protocol or command failure; inspect Error and CommandStage.</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public Task CheckCommandAccessAsync(string vin, VehicleCommand command, string pin, CancellationToken cancellation = default) => Run(async () =>
    {
        if (!RemoteControlsEnabled) throw new BydException(BydError.ControlsDisabled);
        if (!Enum.IsDefined(command)) throw new ArgumentException("Invalid command.", nameof(command));
        if (pin is null || pin.Length != 6 || !pin.All(char.IsAsciiDigit)) throw new BydException(BydError.InvalidPin);
        RequireVehicle(vin);
        await CheckCommandAccessCore(vin, command, pin, cancellation);
        return true;
    }, cancellation);

    private async Task<VehicleCapabilities> CheckCommandAccessCore(string vin, VehicleCommand command, string pin, CancellationToken cancellation, CommandSettings? settings = null)
    {
        VehicleCapabilities? capabilities = null;
        await AtCommandStage(CommandStage.CapabilityCheck, async () =>
        {
            capabilities = await LoadCapabilities(vin, cancellation);
            if (!capabilities.Supports(command, settings)) throw new BydException(BydError.Unsupported);
        }, cancellation);
        await AtCommandStage(CommandStage.PinVerification, async () =>
        {
            var verify = Inner(vin); verify["commandPwd"] = Protocol.Md5(pin); verify["functionType"] = "remoteControl";
            var reply = await TokenPost("/vehicle/vehicleswitch/verifyControlPassword", verify, cancellation);
            if (reply is not JsonObject) throw new BydException(BydError.InvalidResponse);
            if (reply["ok"] is { } supplied)
            {
                if (supplied is not JsonValue value || !value.TryGetValue<bool>(out var ok)) throw new BydException(BydError.InvalidResponse);
                if (!ok) throw new BydException(BydError.PinRejected);
            }
        }, cancellation);
        return capabilities!;
    }

    private static async Task AtCommandStage(CommandStage stage, Func<Task> action, CancellationToken cancellation)
    {
        try { await action(); }
        catch (BydException e) { throw new BydException(e.Error, e.ApiCode, stage); }
        catch (HttpRequestException) { throw new BydException(BydError.Network, null, stage); }
        catch (OperationCanceledException) { throw new BydException(cancellation.IsCancellationRequested ? BydError.Cancelled : BydError.Network, null, stage); }
        catch (Exception e) when (IsResponseFailure(e)) { throw new BydException(BydError.InvalidResponse, null, stage); }
    }
    private static bool IsResponseFailure(Exception e) => e is JsonException or FormatException or CryptographicException or InvalidOperationException or ArgumentException or OverflowException;

    private async Task<VehicleCapabilities> LoadCapabilities(string vin, CancellationToken cancellation)
    {
        var inner = Inner(); inner["appConfigVersion"] = "2"; inner["terminalType"] = "0"; inner["vinList"] = Protocol.Json(new[] { vin });
        var config = await TokenPost("/vehicle/vehicleswitch/getLatestConfig", inner, cancellation);
        if (config is not JsonObject || config[vin] is not JsonObject vehicle || vehicle["cfFixedList"] is not JsonArray list)
            throw new BydException(BydError.InvalidResponse);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        ReadFunctions(list, codes, 0);
        return new(codes, RequireVehicle(vin).SupportsWindowVenting);
    }
    private static void ReadFunctions(JsonArray list, HashSet<string> codes, int depth)
    {
        if (depth > 8) throw new BydException(BydError.InvalidResponse);
        foreach (var node in list)
        {
            if (node is not JsonObject item) throw new BydException(BydError.InvalidResponse);
            if (item["functionNo"] is JsonValue number) codes.Add(number.ToString());
            if (item["cfFixedSecondLevelList"] is { } children)
            {
                if (children is not JsonArray array) throw new BydException(BydError.InvalidResponse);
                ReadFunctions(array, codes, depth + 1);
            }
        }
    }
    internal static string ClimateParameters(int temperature, int minutes) => Protocol.Json(new SortedDictionary<string, object?>
    {
        ["airAccuracy"] = 1,
        ["airConditioningMode"] = 1,
        ["airSet"] = null,
        ["copilotSettingTemp"] = temperature - 14,
        ["cycleMode"] = 2,
        ["mainSettingTemp"] = temperature - 14,
        ["remoteMode"] = 4,
        ["timeSpan"] = (minutes - 5) / 5
    });
    internal static CommandOutcome ParseOutcome(JsonNode node)
    {
        if (node is not JsonObject) throw new BydException(BydError.InvalidResponse);
        if (VehicleStatus.Number(node, "controlState") is { } state) return state switch { 1 => CommandOutcome.Confirmed, 2 => CommandOutcome.Failed, _ => CommandOutcome.Unknown };
        return VehicleStatus.Number(node, "res") switch { 2 => CommandOutcome.Confirmed, > 2 => CommandOutcome.Failed, _ => CommandOutcome.Unknown };
    }
    private static string CommandType(VehicleCommand command) => command switch { VehicleCommand.Lock => "LOCKDOOR", VehicleCommand.Unlock => "OPENDOOR", VehicleCommand.StartClimate => "OPENAIR", VehicleCommand.StopClimate => "CLOSEAIR", VehicleCommand.CloseWindows => "CLOSEWINDOW", VehicleCommand.VentWindows => "OPENWINDOW", VehicleCommand.OpenBoot => "OPENTRUNK", VehicleCommand.CloseBoot => "CLOSETRUNK", VehicleCommand.ScheduleClimate => "BOOKINGAIR", VehicleCommand.ChangeSeatClimate => "VENTILATIONHEATING", VehicleCommand.BatteryHeating => "BATTERYHEAT", _ => throw new ArgumentOutOfRangeException(nameof(command)) };
    private Vehicle RequireVehicle(string vin)
    {
        RequireSession();
        return !string.IsNullOrWhiteSpace(vin) && vehicles.TryGetValue(vin, out var vehicle) ? vehicle : throw new BydException(BydError.VehicleNotSelected);
    }
    private Session RequireSession()
    {
        if (session == null || DateTimeOffset.UtcNow - session.Created >= TimeSpan.FromHours(12)) throw new BydException(BydError.AuthenticationRequired);
        return session;
    }
    private Dictionary<string, string> Inner(string? vin = null, string? serial = null) => Protocol.Inner(RequireSession().DeviceId, Now(), Random(), vin, serial);
    private async Task<JsonNode> TokenPost(string endpoint, Dictionary<string, string> inner, CancellationToken cancellation)
    {
        var active = RequireSession();
        var response = await Post(endpoint, Protocol.Token(active, inner, Now()), cancellation);
        CheckCode(response);
        return DecodeTokenData(response, active.ContentKey);
    }
    private static JsonNode DecodeTokenData(JsonNode response, string contentKey)
    {
        if (response["respondData"] is null) return new JsonObject();
        if (response["respondData"] is not JsonValue value || !value.TryGetValue<string>(out var data)) throw new BydException(BydError.InvalidResponse);
        if (string.IsNullOrEmpty(data)) return new JsonObject();
        var plain = Protocol.Decrypt(data, contentKey);
        return string.IsNullOrWhiteSpace(plain) ? new JsonObject() : JsonNode.Parse(plain) ?? throw new BydException(BydError.InvalidResponse);
    }
    private async Task<JsonNode> Post(string endpoint, Dictionary<string, string> fields, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Market.ApiBaseUrl + endpoint)
        {
            Content = new StringContent(Protocol.Json(new { request = codec.Encode(Protocol.Json(fields)) }), Encoding.UTF8, "application/json")
        };
        using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new BydException(response.StatusCode == HttpStatusCode.TooManyRequests ? BydError.Busy : BydError.Network);
        var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        var wrapper = JsonNode.Parse(text) ?? throw new BydException(BydError.InvalidResponse);
        var plain = codec.Decode(Required(wrapper, "response"));
        if (plain.StartsWith("F{") || plain.StartsWith("F[")) plain = plain[1..];
        return JsonNode.Parse(plain) ?? throw new BydException(BydError.InvalidResponse);
    }
    private void CheckCode(JsonNode response, bool login = false)
    {
        var code = response["code"]?.ToString();
        if (code == "0") return;
        if (code is "1002" or "1005" or "1010") session = null;
        throw new BydException(login ? BydError.AuthenticationRejected : code switch
        {
            "1002" or "1005" or "1010" => BydError.AuthenticationRequired,
            "1008" or "6024" => BydError.Busy,
            "6002" => BydError.NoData,
            "6051" => BydError.LocationUnavailable,
            "5005" or "5006" or "5011" => BydError.PinRejected,
            _ => BydError.Rejected
        }, code);
    }
    private static string Required(JsonNode node, string key) => node is JsonObject && node[key] is JsonValue value && !string.IsNullOrWhiteSpace(value.ToString()) ? value.ToString() : throw new BydException(BydError.InvalidResponse);
    private static string Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string Random() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    /// <summary>Releases owned transports and invalidates the session. Cancel and await outstanding operations before disposing; queued operations cannot start after disposal.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0) return;
        http.Dispose();
        // Never mutate session state concurrently with an active operation. Its finally clears it.
        if (operation.Wait(0))
        {
            try { session = null; vehicles.Clear(); }
            finally { operation.Release(); }
        }
    }
}
