# First public release

Current candidate: `Byd.Client` **0.1.0-alpha.1**, targeting **net10.0**. Neither identity nor target was changed for release preparation. The NuGet flat-container lookup returned 404 on 11 November 2026; that is not a reservation, ownership check or publication guarantee. Recheck the ID before launch. The target stays .NET 10 LTS; .NET 8/9 are now out of support according to Microsoft's policy. No unnecessary multi-targeting was added.

## Decisions and external setup

- The verified public repository is [craigchandler/byd-client-dotnet](https://github.com/craigchandler/byd-client-dotnet). `RepositoryUrl` is configured in the library project and `PackageProjectUrl` follows it. Release checks must run from a clean committed checkout with this repository as its remote. The workflows additionally derive the URL from the actual executing GitHub repository. Do not invent an author, support email or tray-app link. Authors retain the existing licence's `BYD Desktop contributors` attribution.
- Keep the existing MIT licence and all three third-party notice/licence files. Upstream MIT licences were compared with their pinned sources. The Bangcle table's origin in an upstream native cipher is disclosed in THIRD-PARTY-NOTICES.md; these notices are not a representation that BYD authorises the API or use of the protocol.
- Review the candidate tree, existing remote contents and all history to be pushed. Source pushes run offline CI only; tags, GitHub releases and NuGet publication remain separate maintainer actions.
- In GitHub, enable private vulnerability reporting, secret scanning/push protection and branch protection requiring Offline validation. Configure default `GITHUB_TOKEN` permissions to read repository contents. Review enabled Actions and protect the default branch/release workflow.
- Create an environment named **nuget**, restrict it to the default branch, require maintainer approval and prevent self-review where your plan/reviewer setup supports it. Do not run publication until protections exist. A workflow's `environment` declaration alone does not create review protection.
- Create/sign in to the intended nuget.org owner account, enable MFA and confirm package-name ownership/availability. Set environment secret **NUGET_USER** to the NuGet profile username (not an email). No long-lived API key is needed.
- On nuget.org → Trusted Publishing, register the exact GitHub owner, repository, workflow filename **release.yml** (not its directory path), environment **nuget**, and scope the policy to the exact `Byd.Client` package ID. For a new ID, allow publishing new packages as well as versions. Confirm policy activation before the run. Use the correct individual/organisation package owner.

## Local review and checks

```sh
dotnet restore Byd.Client.slnx --locked-mode
dotnet build Byd.Client.slnx -c Release --no-restore
dotnet test Byd.Client.slnx -c Release --no-build --no-restore
dotnet pack src/Byd.Client/Byd.Client.csproj -c Release --no-build --no-restore -o artifacts/packages
python tools/verify-package.py artifacts/packages/Byd.Client.0.1.0-alpha.1.nupkg
```

The verifier checks package files, licence/dependencies/XML docs/portable symbols, compiles the exact README quick start against a fresh local-feed install, compiles the sample and runs its offline catalog branch. It never calls BYD. Dependency restore contacts NuGet. Before actual publication, repeat on a clean committed checkout with `-p:ReleaseBuild=true -p:ContinuousIntegrationBuild=true` and `--require-source-link` on the verifier. The latter also checks the repository commit and Source Link mapping. Full Source Link verification is impossible in an unborn repository without a remote/commit; do not publish the initial local artifacts.

The .NET SDK includes Source Link; no runtime dependency was added for it. `EmbedUntrackedSources=false` prevents packaging arbitrary untracked source inside symbols; build paths are mapped away from the developer's home directory. The `.nupkg` intentionally includes only the managed assembly, XML API docs, README, changelog, licence/notices and Markdown guidance. The `.snupkg` carries the portable PDB. Source is retrieved from the committed repository via Source Link rather than copied wholesale into the package. At pack time, README documentation links are converted to absolute URLs pinned to the repository commit; the repository README keeps relative links. The verifier checks that this conversion preserves the compiling quick start. Packaging does not include tools, tests, Python, credentials or the tray app.

## Repository launch and first publication (maintainer actions)

1. Confirm author attribution and recheck the package ID. Review the complete local tree and notices; stage only intended source/docs/fixtures/workflows. The public repository already exists at `https://github.com/craigchandler/byd-client-dotnet`; inspect its current contents before connecting the local checkout. Make the initial local commit, configure the remote and push `main` **only when separately approved**. Preserve any existing remote files or history.
2. Complete the GitHub/NuGet protection and trusted-publisher setup above. Let the Linux and Windows Offline validation jobs pass on the committed source. Inspect their evidence; a cross-build is not Windows runtime proof.
3. Review the exact default-branch commit. Leave the version prerelease. Update README/changelog publication wording only when publication is actually planned; the changelog is currently Unreleased. Create and push the matching **v0.1.0-alpha.1** tag at that commit only after approval. Do not move a published tag or overwrite a package version.
4. From the default branch, manually run **Publish approved NuGet prerelease**, selecting that existing tag, first with **publish unchecked**. The tag must match both the project version and the current default-branch commit. Inspect the resulting `.nupkg` and `.snupkg`, metadata, licences, Source Link and offline-consumer check.
5. Run again on the same reviewed commit with **publish checked**, then approve the protected **nuget** environment. Only this job receives `id-token: write`. It downloads this run's validated artifact, exchanges OIDC for a short-lived credential with `NuGet/login`, then pushes the main package and its adjacent symbol package. Ordinary pushes, PRs, tags and rehearsal runs cannot invoke publication. There is no stored publishing API key and no automatic GitHub release creation.
6. Verify the NuGet listing, prerelease/version, README, repository commit, symbols and fresh public-feed install after indexing. Record the actual publication in the changelog/README and optionally create GitHub release notes with compatibility limits. If publication partially fails, inspect NuGet before any manual retry; versions are immutable and the workflow deliberately does not hide duplicates with `--skip-duplicate`.

For later versions, update the project version, sample PackageReference, changelog and documentation together. Update lock files intentionally when dependencies change. Release automation accepts prereleases only; a stable release should be an explicit maturity decision and workflow change.

## Official references checked during preparation

- [Microsoft: NuGet package metadata with MSBuild](https://learn.microsoft.com/en-us/nuget/create-packages/creating-a-package-msbuild)
- [Microsoft: symbol packages](https://learn.microsoft.com/en-us/nuget/create-packages/symbol-packages-snupkg)
- [dotnet: SDK-integrated Source Link](https://github.com/dotnet/sourcelink)
- [NuGet: trusted publishing and policy setup](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
- [NuGet/login action](https://github.com/NuGet/login)
- [GitHub: secure use of Actions and immutable SHA pinning](https://docs.github.com/en/actions/reference/security/secure-use)
- [GitHub: protected deployment environments](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments)
- [Microsoft: .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
