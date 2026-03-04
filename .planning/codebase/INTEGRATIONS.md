# External Integrations

## Scope
- This document covers external systems, services, and platform touchpoints used by the repository.
- Direct evidence comes from `.github/workflows/publish.yml`, `dotnet/XHale.Core/XHale.Core.csproj`, and `dotnet/XHale.Core/README.md`.

## CI/CD Platform Integration
- GitHub Actions is used for automated packaging and publishing in `.github/workflows/publish.yml`.
- Workflow name is `publish-nuget` and triggers on:
- Git tags matching `v*` in `.github/workflows/publish.yml`.
- Manual dispatch via `workflow_dispatch` in `.github/workflows/publish.yml`.
- Actions marketplace dependencies:
- `actions/checkout@v4` for repository checkout.
- `actions/setup-dotnet@v4` for .NET SDK setup.

## Package Registry Integration
- Publishing target is GitHub Packages NuGet feed:
- `https://nuget.pkg.github.com/${{ github.repository_owner }}/index.json`
- Push command uses `dotnet nuget push` in `.github/workflows/publish.yml`.
- Authentication uses GitHub-provided token secret:
- `${{ secrets.GITHUB_TOKEN }}` in `.github/workflows/publish.yml`.
- Workflow permissions explicitly include `packages: write` and `contents: read`.

## .NET SDK and CLI Integration
- Build lifecycle depends on `dotnet` CLI commands:
- `dotnet restore` in `.github/workflows/publish.yml`.
- `dotnet pack` in `.github/workflows/publish.yml` and `dotnet/XHale.Core/README.md`.
- Project targets .NET 8 and .NET 9 via `dotnet/XHale.Core/XHale.Core.csproj`, so consumers must have compatible runtime/tooling.

## Consumer/Application Integration Surface
- Library is intended for app integration through managed API contract `IXHaleEngine` in `dotnet/XHale.Core/IXHaleEngine.cs`.
- Integration pattern for clients is in-process library consumption, not HTTP/service calls.
- Public methods support raw numeric and byte-oriented sensor input APIs:
- `AnalyzeBreath(...)` overloads for arrays and byte pairs.
- `DecodeTemperatureC(...)` and `DecodeCoPpmFromBytes(...)` for device payload decoding.
- Expected integration context is mobile/device flows (stated in description and README) but delivered as a generic .NET package.

## Device Data/Protocol Assumptions
- CO raw sample bytes are interpreted as big-endian in byte-array analysis path in `dotnet/XHale.Core/XHaleEngine.cs`.
- Temperature decoding supports specific byte conventions (2-byte 0x2A6E centi-degrees C and 4-byte uint formats) via `IXHaleEngine` and `XHaleEngine` APIs.
- Device serial prefixes influence calibration constants through internal mapping table in `dotnet/XHale.Core/XHaleEngine.cs`.
- These are data-format integrations with external device firmware/protocols, even though no transport stack is implemented here.

## Security and Secret Handling
- No hard-coded external API keys in source files reviewed.
- Publishing auth is delegated to GitHub secret management in `.github/workflows/publish.yml`.
- License key accepted by `Initialize(string licenseKey)` is held in memory (`_licenseKey`) and not transmitted externally in current code paths (`dotnet/XHale.Core/XHaleEngine.cs`).

## Not Present (Important for Planning)
- No outbound HTTP clients or REST/GraphQL integrations found.
- No cloud SDK integrations (AWS/Azure/GCP) detected.
- No database integrations, message brokers, or cache clients detected.
- No telemetry/log shipping integrations observed.
- No mobile-native bridge packages or P/Invoke/native library loading present.

## Operational Integration Notes
- Release process is tag-driven; creating a `v*` git tag is the external trigger for package publication.
- Artifacts are staged in local/CI `artifacts/` directory before registry push.
- `<RepositoryUrl>` currently points to placeholder `https://example.invalid` in `dotnet/XHale.Core/XHale.Core.csproj`; package metadata integration with source hosting should be corrected before broad distribution.
