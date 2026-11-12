# Compatibility and validation evidence

This is the first experimental release candidate, not a model/region certification matrix. Evidence below comes from user-reported tests in the first consuming tray application on 11 November 2026 and synthetic offline checks. No live BYD APIs are used by release validation or CI.

| Scope | Live evidence | Limits |
| --- | --- | --- |
| Australian international email account, Atto 3 | Sign-in and vehicle list succeeded; status matched the official app, including full/not-charging while plugged in; one refresh was 2.7 seconds | One account/vehicle; latency is not a performance guarantee |
| GPS | User compared the map marker with the official app | No ongoing accuracy or availability guarantee |
| Lock, unlock, start and stop climate | User reported success; Lock explicitly returned provider confirmation | Only tested settings; acknowledgement is not independent physical verification |
| Feature catalog | List loaded without error | Missing flags/unknown entries do not establish absence of support |
| Other models, non-AU countries, advanced reads and writes | None | Protocol implementations and synthetic tests only |
| Linux | Offline builds/tests; consuming application used on Arch/KDE | Desktop startup, KWallet and map rendering belong to the separate app |
| Windows | CI configured; previous Windows-target cross-build of the consuming app | No Windows runtime/account/vehicle validation |

The bundled catalog contains 101 account countries grouped into 16 international server regions. `BydDiscovery.GetRegions()` and `BydMarkets.All` describe implemented routing metadata; they do not query BYD or assert operational support. Choose the country associated with the account. Mainland China and other login protocols are excluded. No arbitrary credential destination can be supplied through the public API.

All implemented paths have synthetic parsing/protocol/HTTP simulation coverage, with focused MQTT correlation checks. This does not reproduce all provider or firmware behaviour. New compatibility reports should state country, model, method and safe outcome; omit VIN, email, account IDs, coordinates and payloads.

## Consumer compatibility

The original client constructors, core async method signatures, command approval overload and first four command enum values remain unchanged. The target remains `net10.0`; no UI or tray dependencies were added. `VehicleNotSelected` is appended to `BydError` for an unlisted vehicle instead of the previous misleading `InvalidResponse`. Update exhaustive error switches if necessary.

`Vehicle.ToString()` and telemetry-record string formatting now return redacted descriptions. Use the explicit fields for local UI; `Vehicle.DisplayName` still contains the model and VIN suffix. Record constructors, fields and equality remain unchanged. Never use default formatting as a machine-readable contract. The separate tray source already selects explicit fields and `DisplayName`.

Disposed clients reject queued operations before sending requests. Callers should cancel and await active operations before disposal. Preflight timeout now reports `Network` rather than incorrectly reporting a user cancellation. No command is automatically replayed.

Additive API refinements on 12 November 2026: `VehicleStatus.ChargingState` and `LockState` expose semantic enums for application logic; existing string properties and constructors remain. Unknown labels map to `Unknown`, including for client-created records. The new getters also appear in default JSON serialization; applications that serialize snapshots must account for additive fields and protect personal data. Enum ordinals must not be cast to/from BYD protocol codes.

`BydClient.RemoteControlsEnabled` and `SetRemoteControlsEnabledAsync` separate local permission from authentication. The existing option remains the initial value, preserving callers that opt in when creating a client. A permission change preserves authentication and the vehicle list, sends no network request and invalidates prior command approvals. It must be awaited and does not interrupt an operation already running. The tray checkbox uses this API; toggling it retains the selected vehicle/readings and no longer needs another sign-in. Fresh approval, capabilities and PIN checks remain mandatory. These refinements have offline tests, not new live vehicle or Windows-runtime validation.
