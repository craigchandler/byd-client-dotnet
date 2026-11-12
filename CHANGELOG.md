# Changelog

## [0.1.0-alpha.1](https://www.nuget.org/packages/Byd.Client/0.1.0-alpha.1) — 2026-11-12

Initial experimental public prerelease. Published from tag `v0.1.0-alpha.1` at commit `402e8f87fe4d36093541cf145ed43754c164f4a2`.

Publication used the protected GitHub Actions workflow and NuGet trusted publishing. Fresh public-feed installation, the compiling quick start, offline API checks, repository signature, published symbols and Source Link were verified. The immutable package retains the candidate-era README and changelog; this repository records the completed publication.

- Additive typed charging/lock states, preserving the original string properties and constructors.
- Local async control opt-in/out without reauthentication; actual permission changes revoke earlier approvals while retaining session/vehicle selection.
- Native .NET 10 international email authentication, explicit session lifecycle and bundled region/resource discovery.
- Vehicle list, typed realtime status/details, explicit GPS, advertised features and advanced read APIs.
- Optional capability/PIN-gated controls with expiring exact approvals, single submission and conservative unknown outcomes. Advanced controls remain unverified live.
- Synthetic protocol, HTTP and MQTT regression tests, including session invalidation, response correlation, privacy, cancellation and no replay.
- Public XML docs, retained upstream MIT notices, NuGet README/symbols and offline package-consumer verification.
- Manual protected OIDC publication workflow, separate from PR/push validation.

Compared with the pre-publication tray development build: unlisted VINs now report the appended `VehicleNotSelected` category; vehicle/telemetry record string output is redacted; queued calls cannot start after disposal; preflight timeouts are distinguished from user cancellation. Existing constructors, core command signatures, fields and original enum values are preserved. See docs/compatibility.md.
