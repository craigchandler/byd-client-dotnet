# Contributing

Keep this a small independent .NET library. Discuss broad API changes first; preserve existing callers and enum values where practical. Do not add UI, background services, credential persistence or automatic command/authentication retries.

1. Use a supported .NET 10 SDK (global.json allows newer 10.0 feature bands).
2. Run `dotnet restore Byd.Client.slnx --locked-mode` and `dotnet test Byd.Client.slnx -c Release --no-restore`.
3. Run `dotnet pack src/Byd.Client/Byd.Client.csproj -c Release --no-restore -o artifacts/packages`, then `python tools/verify-package.py artifacts/packages/Byd.Client.0.1.0-alpha.1.nupkg`.
4. Add focused synthetic tests for parsing, session/error handling or a changed command path. Every new write must prove a single trigger, no replay and conservative unknown outcomes.
5. Update XML documentation, compatibility evidence and changelog. Never upgrade live-support claims from synthetic tests alone.

The package verifier compiles/runs an offline-only consumer against a fresh local-feed install and verifies README quick-start compilation. It needs NuGet access (or cached dependencies), but no account or vehicle. It never runs the `--live` sample.

Fixtures: use a Python virtual environment with `pybyd==0.0.79`, then run `tools/generate-fixtures.py` offline. Review regenerated vectors and preserve THIRD-PARTY notices. The optional `tools/probe-pybyd.py` is an interactive AU read-only development probe; do not run it in CI or as part of release checks. Credentials must be entered locally, never in shell arguments, reports or issues. Do not add request captures or real VIN/location fixtures.

Before a PR, inspect `git diff --check`, the complete candidate diff and relevant history for private data. Safe reports should name operation, country/model, category/stage and synthetic reproduction. Follow SECURITY.md for sensitive findings.
