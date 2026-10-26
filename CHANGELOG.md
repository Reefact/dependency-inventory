# Changelog

All notable, user-facing changes to **`depinv`** (the `Reefact.DependencyInventory` package) are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Releases are cut from a `v*` tag (see [RELEASING.md](RELEASING.md)).

## [Unreleased]

## [1.0.0-preview.2] - 2026-10-26

### Fixed

- **The installation commands shown in the README, and therefore on the nuget.org page, now include `--prerelease`.**
  While only preview versions are published, `dotnet tool install` and `dotnet tool update` fail without it.
  Version 1.0.0-preview.1 shipped the commands without the flag.

## [1.0.0-preview.1] - 2026-10-26

### Added

- **Inventory of the NuGet references declared in the Git repositories of an Azure DevOps project or a GitLab group.**
  Files are read through the provider's API from each repository's selected commit, without cloning. The tool is
  read-only: it only sends GET requests.
- **Extraction from project files**: `*.csproj`, `*.fsproj`, `*.vbproj`, `*.props`, `*.targets` and
  `packages.config`. `PackageReference`, `GlobalPackageReference` and `package` are direct references;
  `PackageVersion` is reported as a central version declaration. A version is matched from the nearest
  `Directory.Packages.props`. Transitive dependencies are not resolved.
- **Filters**: repeatable `--pattern` (glob on the package ID) and `--regex` options, combined with OR, and
  `--case-sensitive`. At least one filter is required.
- **One branch per repository**, picked in the order `main`, `release` (and `release/*`), `develop`, `dev`, then the
  first other branch alphabetically.
- **Reports** in the `--output` folder (`dependency-report` by default): `dependencies.csv`,
  `central-versions.csv`, `repositories.csv` and `report.json`. CSV files are UTF-8 with BOM and
  semicolon-separated.
- **Diagnostics**: `--repository <name> --diagnostics` writes `diagnostics.json`, listing every reference extracted
  (even outside the filters), the available branches, the selected one, the commits and the files examined.
- **Parallelism**: `--parallelism` (1 to 8) with `--parallelism-mode files|repositories`. Reports are identical, in
  the same order, whatever the parallelism.
- **Retries** of transient failures (HTTP 429, 502, 503, 504, network errors, response timeouts), up to 5 attempts,
  honouring `Retry-After`, with a 120 s timeout per attempt.
- **Credentials**: `depinv login` and `depinv logout` store and delete them in the Windows Credential Manager. On
  Linux and macOS, credentials are read from a file with `--credentials-file`, or typed at each scan.
- **Distribution as a .NET tool**: `dotnet tool install --global Reefact.DependencyInventory` installs the `depinv`
  command. The package is licensed under Apache-2.0.

[Unreleased]: https://github.com/Reefact/dependency-inventory/compare/v1.0.0-preview.2...HEAD
[1.0.0-preview.2]: https://github.com/Reefact/dependency-inventory/compare/v1.0.0-preview.1...v1.0.0-preview.2
[1.0.0-preview.1]: https://github.com/Reefact/dependency-inventory/releases/tag/v1.0.0-preview.1
