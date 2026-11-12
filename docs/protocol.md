# Protocol and behavioural boundaries

The implementation follows pinned pyBYD 0.0.79; it is not an official API contract. The native Bangcle lookup table, MD5/AES/signing layout, API paths, MQTT credentials/events and mappings are adapted from that source with retained MIT notices. See THIRD-PARTY-NOTICES.md for exact revisions/hashes. Old protocol primitives exist for wire compatibility and should not be reused as general-purpose cryptography.

## Read requests

Status and GPS each send one trigger, then read matching results. HTTP and transient MQTT readers run concurrently; the first usable current result wins, and the unused readers are cancelled and observed. MQTT events must match the VIN and request serial and must not be retained. An old/undated HTTP snapshot cannot outrun a matching live push; it can remain a fallback, with its original timestamp. Retrieval time is not vehicle time. Missing, over-thirty-minute-old or over-five-minute-future timestamps make `IsStale` true; even a recent timestamp is not proof of physical freshness.

Entire zero-filled status templates are rejected. Zero battery/range can still be valid when other authoritative fields support the reading. Missing or unrecognized data remains null/Unknown rather than being inferred from the battery percentage. Realtime `chargeState` 1 means charging; 0 or 15 means no charging in progress. State 15 does not establish cable connection. Secondary `chargingState` is not a fallback for realtime status. The dedicated charging endpoint interprets its own state separately.

Passenger door/window completeness and compartment states are distinct. Positive window-opening percentages take precedence over conflicting closed/default flags. Tyre pressures are normalized only when the reported unit is understood. GPS rejects missing/invalid coordinates and near-zero placeholders; it is never read implicitly by status.

## Commands

Controls require per-client opt-in, current signed-in vehicle selection, a fresh exact approval and six-digit PIN. Local opt-in can be changed with `SetRemoteControlsEnabledAsync` independently of sign-in, without a BYD request. Actual permission changes invalidate every previous approval. The change waits for other operations and must be awaited; it does not stop an operation already running or undo a vehicle command. The approval is immutable, client/vehicle/settings-bound, expires in sixty seconds and is consumed once. Capabilities and PIN access are checked again before dispatch. `CheckCommandAccessAsync` is a support/PIN check only, not approval or proof of actuation.

Commands are dispatched once. Result polling never repeats a trigger. A direct HTTPS result lookup can omit the serial echo because the lookup itself is scoped to the requested serial, following the pinned pyBYD decoder. Explicit conflicting serial/VIN/command identity is rejected. MQTT cannot use this fallback. A pending trigger without a serial remains unknown. Transport failure, cancellation or malformed response after possible dispatch means unknown outcome.

Advanced controls have synthetic coverage only. Seat changes preserve other reported comfort state and fail closed if needed state is unknown. Charging writes use the dedicated smart-charge endpoints. Climate booking IDs remain 64-bit integers; departure times are instants. Charging windows use vehicle-local clock time at minute resolution. Schedules/bookings may execute after your application exits. There is no find-my-car/horn/light command.
