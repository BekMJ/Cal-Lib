# Technology Stack

## Scope
- Repository is a single .NET class library project in `dotnet/XHale.Core`.
- Core implementation lives in `dotnet/XHale.Core/XHaleEngine.cs` and `dotnet/XHale.Core/IXHaleEngine.cs`.

## Primary Languages
- C# is the only implementation language (`.cs` files under `dotnet/XHale.Core`).
- XML is used for project/package configuration in `dotnet/XHale.Core/XHale.Core.csproj`.
- YAML is used for CI/CD workflow definitions in `.github/workflows/publish.yml`.

## Runtime and Framework Targets
- Multi-targeted .NET runtime: `net9.0;net8.0` in `dotnet/XHale.Core/XHale.Core.csproj`.
- SDK style project uses `Microsoft.NET.Sdk` in `dotnet/XHale.Core/XHale.Core.csproj`.
- Nullability and implicit usings enabled via `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>`.

## Packaging and Distribution
- NuGet package ID is `XHale.Core` in `dotnet/XHale.Core/XHale.Core.csproj`.
- Current package version is `0.1.0-alpha.6` in `dotnet/XHale.Core/XHale.Core.csproj`.
- Package README is embedded from `dotnet/XHale.Core/README.md` via `<PackageReadmeFile>` and `<None Include=... Pack=true>`.
- Local package output directory is `artifacts/` (see `dotnet/XHale.Core/README.md` and `.github/workflows/publish.yml`).

## Dependency Profile
- No third-party NuGet dependencies declared (no `<PackageReference>` entries in `dotnet/XHale.Core/XHale.Core.csproj`).
- Uses only .NET base class library namespaces (`System`, `System.Collections.Generic`, `System.Linq`) in `dotnet/XHale.Core/XHaleEngine.cs`.
- No native bindings or platform-specific assets are included (also stated in `dotnet/XHale.Core/README.md`).

## Project Structure (Tech-Relevant)
- Public interface contract: `dotnet/XHale.Core/IXHaleEngine.cs`.
- Core domain model record(s): `dotnet/XHale.Core/Models.cs`.
- Main algorithm/calibration implementation: `dotnet/XHale.Core/XHaleEngine.cs`.
- Build and package metadata: `dotnet/XHale.Core/XHale.Core.csproj`.
- CI publishing workflow: `.github/workflows/publish.yml`.

## Build Toolchain
- Build/pack command documented as `dotnet pack ./dotnet/XHale.Core/XHale.Core.csproj -c Release -o ./artifacts` in `dotnet/XHale.Core/README.md`.
- CI runs on `ubuntu-latest` and installs `.NET 9` with `actions/setup-dotnet@v4` in `.github/workflows/publish.yml`.
- Restore step uses `dotnet restore` scoped to the single project file in `.github/workflows/publish.yml`.

## Testing and Quality Tooling
- No test project or test framework files found in repository tree.
- No linting/formatting/analyzer config files detected in repo root (for example no `.editorconfig`, `Directory.Build.props`, or analyzer package refs).
- Current quality signal is primarily compilation/packaging success in CI workflow `.github/workflows/publish.yml`.

## Maturity Signals
- Pre-release semantic versioning (`alpha`) indicates early-stage package lifecycle.
- `RepositoryUrl` is placeholder `https://example.invalid` in `dotnet/XHale.Core/XHale.Core.csproj`, so repository metadata is not production-ready yet.
- Multiple generated package artifacts already present in `artifacts/` (`XHale.Core.0.1.0-alpha.1` through `alpha.6`).

## Practical Stack Summary
- Stack is intentionally minimal: C# class library + .NET 8/9 + NuGet packaging + GitHub Actions publish pipeline.
- External runtime dependencies are effectively none beyond the .NET platform itself.
- Integration surface is package-consumer APIs, not service/network integrations.
