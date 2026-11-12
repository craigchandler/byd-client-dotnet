# BYD Client for .NET

An unofficial C# client for developers building applications, scripts and integrations with international BYD accounts. Read vehicle status, location and advertised features, with optional, explicitly approved remote controls. No Python, desktop UI or background service is required.

**Experimental prerelease; not affiliated with or endorsed by BYD.** The API can change without notice. Live evidence is limited to one Australian Atto 3; the country catalog is not a compatibility guarantee.

## Install

Requires .NET 10. Install the experimental [Byd.Client 0.1.0-alpha.1 prerelease from NuGet](https://www.nuget.org/packages/Byd.Client/0.1.0-alpha.1):

```sh
dotnet add package Byd.Client --version 0.1.0-alpha.1
```

For local development, build your own package:

```sh
dotnet restore Byd.Client.slnx --locked-mode
dotnet test Byd.Client.slnx -c Release --no-restore
dotnet pack src/Byd.Client/Byd.Client.csproj -c Release --no-restore -o artifacts/packages
```

In your own project, replace `/absolute/path/to/artifacts/packages` with the local feed path:

```sh
dotnet add package Byd.Client --version 0.1.0-alpha.1 --no-restore
dotnet restore --source /absolute/path/to/artifacts/packages --source https://api.nuget.org/v3/index.json
```

The local-feed restore also needs nuget.org (or its cached packages) for MQTTnet. Use an isolated package cache when testing a locally built candidate against an already published version.

## Quick start

This is a complete `Program.cs` for a .NET 10 console project. It asks for credentials locally, defaults to read-only access, lets the user select a vehicle and makes one status request. It never requests a PIN or sends a vehicle command. **Running it contacts BYD and may sign the mobile app out.**

```csharp
using Byd.Client;

Console.Write("BYD account country (e.g. AU): ");
var market = BydMarkets.ForCountry(Console.ReadLine() ?? "");
Console.Write("IANA timezone (e.g. Australia/Adelaide): ");
var zone = Console.ReadLine();
Console.Write("BYD email: ");
var email = Console.ReadLine() ?? "";
Console.Write("BYD password: ");
var password = "";
while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
{
    if (key.Key == ConsoleKey.Backspace)
        password = password.Length == 0 ? "" : password[..^1];
    else if (!char.IsControl(key.KeyChar)) password += key.KeyChar;
}
Console.WriteLine();
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
using var client = new BydClient(new()
{
    Market = market,
    TimeZone = string.IsNullOrWhiteSpace(zone) ? "UTC" : zone,
    EnableRemoteControls = false
});
try
{
    await client.LoginAsync(email, password, stop.Token);
    password = ""; // Drops this reference; managed strings cannot be securely erased.
    var vehicles = await client.GetVehiclesAsync(stop.Token);
    if (vehicles.Count == 0) { Console.WriteLine("No vehicles reported."); return; }
    for (var i = 0; i < vehicles.Count; i++)
        Console.WriteLine($"{i + 1}: {vehicles[i].DisplayName}"); // Local UI only; contains a VIN suffix.
    Console.Write("Select vehicle number: ");
    if (!int.TryParse(Console.ReadLine(), out var number) || number < 1 || number > vehicles.Count)
        return;
    var status = await client.GetStatusAsync(vehicles[number - 1].Vin, stop.Token);
    Console.WriteLine($"Battery: {status.BatteryPercent?.ToString("0.#") ?? "Unknown"}% | {status.ChargingState} | Locks: {status.LockState}");
    Console.WriteLine(status.IsStale(DateTimeOffset.UtcNow) ? "Stale or unverified reading" : "Recent vehicle timestamp");
}
catch (BydException error) { Console.Error.WriteLine(error.Message); }
catch (OperationCanceledException) { Console.Error.WriteLine("Read cancelled."); }
finally { password = ""; }
```

The same code is compiled from this README during package validation. [ReadStatus](samples/ReadStatus/Program.cs) adds a default offline mode: `dotnet run --project samples/ReadStatus -- --offline` lists bundled regions without creating a client. Restore the sample from NuGet first:

```sh
dotnet restore samples/ReadStatus/ReadStatus.csproj --source https://api.nuget.org/v3/index.json
dotnet run --project samples/ReadStatus --no-restore -- --offline
```

Only `--live` runs the interactive example. Never pass credentials as arguments, commit them or send them in issues.

## Features and evidence

| Area | Implemented | Evidence |
| --- | --- | --- |
| Email authentication, vehicle list, realtime status | Yes; nullable/unknown fields, freshness and transport metadata | Synthetic protocol/HTTP/MQTT tests; user-verified on one AU Atto 3 |
| GPS location | Explicit request only; no background tracking | Synthetic tests; user compared map with official app |
| Lock/unlock, climate start/stop | Per-client opt-in, exact single-use confirmation and PIN | Synthetic tests; user-reported success on that vehicle |
| Charging, climate/comfort, bookings, energy, feature reads | Typed snapshots and conservative advertised capabilities | Synthetic tests; feature list loaded live; other advanced reads not live-verified |
| Windows, boot, comfort, battery heating, climate bookings, charging/schedule writes | Capability-gated implementation | Synthetic tests only; no live support claim |
| Regions | Local discovery: 101 account countries, 16 international server regions | Catalog and signing tests; only AU live-verified |

Mainland China, other sign-in methods and find-my-car/horn/light commands are outside this implementation. See [compatibility and evidence](docs/compatibility.md) before depending on a model or operation. The separate Avalonia `Byd.Tray` application is the first consumer; its public repository URL has not been established here.

## Discover countries and resources

```csharp
using Byd.Client;

var regions = BydDiscovery.GetRegions(); // No account or network required.
var resources = BydDiscovery.GetResources();
var uk = BydMarkets.ForCountry("GB"); // Select the account country, not the car's current location.
using var client = new BydClient(new() { Market = uk, TimeZone = "Europe/London" });
// After explicit LoginAsync and GetVehiclesAsync, with a selected vehicle:
// var features = await client.GetVehicleFeaturesAsync(selected.Vin);
```

The catalog is versioned by `SchemaVersion` and `RegionCatalogRevision`. `ReportedAvailable` means an advertised flag, not verified operation. `NotReported` or `Unknown` is inconclusive for read endpoints. There is no region probing or automatic fallback.

## Sessions, polling and safety

Login is explicit and session tokens stay in memory. A provider-invalidated session or the local twelve-hour limit requires a new sign-in and vehicle list. No password retention, token export, automatic reauthentication or request replay is implemented. Signing in with the same account can displace the official mobile session. A separate account authorised through the official sharing flow may help, subject to account/region permissions; it needs its own control PIN.

Reads are user-driven. Status/location can wake the car, and cached readings may still be returned; inspect timestamps and `IsStale`. There is no built-in periodic poller. For integrations, start with manual refresh; if scheduling reads, use a conservative interval (for example fifteen minutes), pause after failures and let the user disable it. There is no verified universal safe polling rate. Do not run a sign-in loop or continually reconnect competing clients.

A refresh sends its trigger once. Bounded result lookups and transient TLS MQTT reads run concurrently; no background MQTT listener remains. `EnablePushTelemetry = false` uses HTTP alone. `RequestTimeout` limits each HTTP request, not the total operation; use a cancellation token for an overall deadline. Cancel and await outstanding work before disposing. Async operations are serialized; call synchronous `ConfirmCommand` only after outstanding operations complete.

`VehicleStatus.ChargingState` (`VehicleChargingState`) and `LockState` (`VehicleLockState`) are intended for application decisions and UI translation. Missing or unrecognized state stays `Unknown`; enum values are semantic states, not BYD wire codes. The original `Charging` and `Locks` string properties and positional constructor remain available for compatibility. The typed properties always reflect those labels, including after a record `with` expression.

Remote controls are **off by default**. `BydClientOptions.EnableRemoteControls` sets initial local permission. After an explicit user choice, `await client.SetRemoteControlsEnabledAsync(true)` enables controls on the same client without performing another sign-in, checking a PIN or making a network request. `RemoteControlsEnabled` reports the local permission only, not session validity or vehicle support. `SetRemoteControlsEnabledAsync(false)` disables controls. Every actual permission change invalidates all earlier approvals; re-enabling needs fresh confirmation. Changes are serialized with other operations: await completion before relying on the new permission. They do not interrupt an already-running operation, undo a dispatched command or cancel vehicle-stored schedules. Do not restore control opt-in automatically at startup.

A consuming app must show the exact vehicle/action/settings, obtain confirmation, then call `ConfirmCommand` and `ExecuteAsync` with a locally entered six-digit PIN. Approvals expire after sixty seconds and are consumed once. Commands are submitted at most once. `Unknown` or `OutcomeUnknown` means check the official app/vehicle before another attempt; **never wrap commands in automatic retries**. Scheduled actions can run after your app exits.

Catch `BydException` and inspect `Error`, optional numeric `ApiCode` and `CommandStage`; messages omit response text and personal data. `AuthenticationRequired` needs explicit sign-in; `VehicleNotSelected` needs the current vehicle list; `Unsupported` is a support check/read rejection. `CapabilityCheck` and `PinVerification` failures precede dispatch. Cancellation during a read throws a sanitized `OperationCanceledException`; preflight cancellation reports `Cancelled`, while a timeout reports `Network`. Cancellation/transport failure during or after command submission reports `OutcomeUnknown`.

[Privacy and authentication](docs/privacy.md) explains data recipients, memory handling and logging. [Protocol notes](docs/protocol.md) describe timestamp, charging-state and response-correlation boundaries. Record string output is redacted; serializers and structured-log destructuring still expose public fields, so do not log these objects.

## Development and provenance

See [CONTRIBUTING.md](CONTRIBUTING.md), [SECURITY.md](SECURITY.md), [release instructions](docs/releasing.md) and [CHANGELOG.md](CHANGELOG.md). Offline tests need no BYD account or vehicle. First dependency restore requires NuGet access. Python is needed only for optional reference-fixture regeneration.

The protocol, Bangcle cipher/table and reference fixtures derive from **[jkaberg/pyBYD 0.0.79 at commit 995ef70](https://github.com/jkaberg/pyBYD/tree/995ef70a2b6bd287d58c1f8564e6b50eabc7f868)** (MIT, Joel Kåberg). The international country catalog derives from pinned MIT-licensed hass-byd-vehicle metadata. This is an independent implementation with adapted code; changing language does not remove upstream licence obligations. Full provenance, hashes and retained licences are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Original project code is under the existing [MIT licence](LICENSE).
