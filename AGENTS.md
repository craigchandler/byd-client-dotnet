# Working agreement

- Keep this repository independent of the tray app. Public API changes require compatibility review.
- Run offline protocol and HTTP simulation tests after changes. Tests must not require live BYD credentials or access.
- Never execute a real vehicle command without explicit approval of that exact command, vehicle and parameters.
- Never print or persist real passwords, PINs, VINs, tokens, raw responses or precise location in source, tests, logs or chat.
- Use synthetic fixtures. Do not introduce telemetry dumps, automatic command retries or automatic authentication loops.
- Report offline, live account, vehicle, Linux desktop and Windows evidence separately.
- Do not commit, publish, push, tag or create remote repositories without an explicit request.
