# Authentication, privacy and data handling

## Recipients and transport

Explicit sign-in sends the account email and password, together with password-derived protocol data and compatibility/device fields, to the selected allow-listed `https://dilinkappoversea-*.byd.auto` endpoint. In the reference protocol, the password is the `signKey` field inside the Bangcle-encoded login payload; it is not solely a one-way password hash. Protocol encryption is a compatibility mechanism, not a substitute for HTTPS. Subsequent requests send session-derived authentication, account/vehicle identifiers, timestamps and requested operation parameters to that regional BYD service. PIN verification/control requests send a protocol hash of the PIN to BYD; treat the PIN and hash as credentials.

Status and GPS reads can retrieve a broker address from the authenticated BYD API and connect to that broker over certificate-verified TLS MQTT. The subscription uses account-derived credentials and is scoped to the requested vehicle/event. It disconnects when the read finishes. Set `EnablePushTelemetry = false` to disable that additional recipient. HTTP redirects are disabled; certificate validation is not bypassed. HTTP responses are bounded to 4 MiB. The library performs no MQTT publishing.

No analytics, crash reporting, map tiles, reverse geocoding or other third-party service is used by the library. The consuming tray application's optional map tiles and KWallet/Windows credential storage are separate app behaviours. Dependency restore goes to configured NuGet feeds; it sends no BYD credentials.

## Lifetime and persistence

The library keeps session tokens/derived keys, device identity and the current vehicle list in memory. It does not persist credentials, PINs, tokens, telemetry or GPS. It does not expose token import/export. Login clears previous session/vehicles and invalidates existing approvals before sending the new attempt. A provider session-expiry response invalidates the session; the client also imposes a local twelve-hour session limit. Neither implies a BYD token lifetime guarantee. Dispose the client when finished; cancel and await active work first.

Local control permission is independent of authentication. `SetRemoteControlsEnabledAsync` changes only local state, preserves the session/vehicle list and sends nothing to BYD. It does not create or save a session, verify a PIN or approve a command. Actual permission changes revoke previous approvals; controls start off unless explicitly opted in and should not be restored from saved app settings.

A new sign-in with the same account can displace the mobile app session. Use explicit sign-in and clear recovery UI; do not automatically sign in again on each unauthorized response. If supported for the account, an officially authorised sharing account with its own PIN may reduce conflicts. This library cannot create sharing accounts or promise their permissions.

Passwords, PINs and tokens are managed strings: assigning an empty string or disposing cannot guarantee physical erasure from memory. Protect crash dumps, process access, diagnostics and device security. If your app saves credentials, use the OS secure credential store and explicit consent; this library supplies no plaintext persistence fallback.

## Errors and logs

Routine provider/transport failures use fixed messages, safe categories and numeric API codes of at most six digits. Raw responses, server message text and inner transport exceptions are not exposed. Cancellation from a read keeps the caller's token but discards transport diagnostics. Command dispatch/result failures are conservative: an unknown physical outcome cannot be treated as an unsent command.

There is no logger or response-dump feature in the library. `ToString()` on vehicle/telemetry/booking/location records redacts personal data, but **JSON serialization, reflection and structured-log destructuring read the public fields directly**. Do not serialize these objects into logs. In application logs, prefer method name, elapsed duration, `Error`, `CommandStage` and the numeric code. Avoid VINs (including suffixes), account names, tokens, PINs/hashes, exact coordinates, booking details and raw exceptions from your own secret-storage/UI layers.

## Repository material

All fixtures use synthetic account/token/VIN inputs. Location tests use a public landmark, not a vehicle location. Ciphertext/hash vectors are generated from those synthetic inputs; they are not captured provider traffic. `bangcle_tables.bin` is a public upstream protocol lookup table, not an account key. Upstream licence notices are retained.

Before publishing any change, review both the candidate tree and all history that will be pushed. Pattern scans cannot prove absence of secrets. If a credential is discovered, revoke/rotate it first, identify affected paths/commits privately and agree remediation; never silently rewrite shared history. Enable GitHub secret scanning/push protection and private vulnerability reporting before accepting public reports.
