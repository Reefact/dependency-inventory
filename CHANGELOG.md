# Changelog

All notable, user-facing changes to **`depinv`** (the `DependencyInventory` package) are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Releases are cut from a `v*` tag (see [RELEASING.md](RELEASING.md)).

## [Unreleased]

## [1.0.0-preview.7] - 2026-10-28

### Added

- **`--branch` and `--branch-file` choose the branch scanned in each repository**, in `depinv packages` and
  `depinv frameworks`. `--branch`, repeatable, lists branches in priority order: names, globs such as `release/*`
  (the highest match in natural order), and `@default` for the repository's default branch. `--branch-file` reads
  the list from a file, where `[repository]` sections give matching repositories their own list. The scan header
  prints the branches looked for.
- **`--branch-mode all` inventories every listed branch of each repository**, instead of the first one that exists.
  Each branch gets its own lines in the reports, told apart by their Branch and Commit columns; `repositories.csv`
  has one line per repository and branch. It needs `--branch` or `--branch-file`; `--branch "*"` lists every branch.
  Branches on the same commit are scanned once, and a file content already read on another branch is not downloaded
  again.

### Changed

- **Without `--branch` or `--branch-file`, each repository is scanned on its default branch**, as set in Azure DevOps
  or GitLab, instead of the first of main, release (or release/*), develop and dev, then the first branch in
  alphabetical order. Reports can therefore differ for the same command. The previous order, without its alphabetical
  fallback, is `--branch main --branch release --branch "release/*" --branch develop --branch dev`.
- **`used-types.csv` gains the Branch and Commit columns**, after Repository, as in the other reports, so that the
  types used on each branch can be told apart.
- **A repository that has none of the requested branches is reported `NoBranch`** in `repositories.csv`, with the
  branches looked for, instead of being scanned on another branch.

### Fixed

- **`depinv feed --help` shows the help again** instead of failing with "Encountered malformed markup tag": the
  description of `--feed` contained a bracketed part of the feed URL.

## [1.0.0-preview.6] - 2026-10-28

### Fixed

- **`depinv packages` downloads packages from Azure Artifacts feeds**, which respond to download requests with an
  HTTP 303 redirect to blob storage. Such packages were reported `Undetermined`, with "Azure Artifacts returned HTTP
  303" in `diagnostics.json`. The redirect is followed without forwarding credentials to the blob storage host.
- **`depinv packages` no longer reads source files for packages it cannot read.** The C# files of a project are
  only read when at least one package it references was found in a source. Without `--feed`, a scan of packages
  unavailable on nuget.org no longer fetches any C# source files, reducing unnecessary requests compared with
  1.0.0-preview.5.

## [1.0.0-preview.5] - 2026-10-28

### Added

- **`depinv packages` tells whether the code uses each referenced package.** The new Usage column of
  `dependencies.csv` says `Used`, `Unused`, `BuildOnly` (analyzers, build or tool assets, development dependencies) or
  `Undetermined`, from the public types of the package's assemblies and the syntax of the project's C# files, without
  compiling. The packages are read from the Azure Artifacts feeds given by the new repeatable `--feed` option, then from
  nuget.org unless `--no-nuget-org`. The new Source and Feed columns give the real origin of each package: a feed, or
  nuget.org even when read from a feed's upstream cache. `--used-types` also writes `used-types.csv`, the types of each
  package referenced by the code. Package contents are cached locally for the next runs, and the NuGet global packages
  folder is read before any download. `diagnostics.json` gives the reason of every undetermined usage.
- **`depinv packages --transitive` lists the packages each project depends on without declaring them**, per target
  framework, in `transitive-dependencies.csv`, with the path that brings each one (`Via`). The graph is read from the
  project's `packages.lock.json` when the repository has one (`Resolution`: `LockFile`), otherwise computed from the
  `.nuspec` of the packages following the NuGet rules, central transitive pinning included (`Computed`). `--pattern`
  and `--regex` also match the packages reached transitively, and the `Usage` column flags the code that uses a package
  it does not declare. `dependencies.csv` is unchanged.
- **`--feed` accepts a feed URL**, `https://dev.azure.com/organization[/project]/_packaging/feed`, in `depinv feed` and
  `depinv packages`. A feed of another organization is read with that organization's credentials.

## [1.0.0-preview.4] - 2026-10-27

### Changed

- **Breaking: the package scan is now the `packages` command (alias `pkg`).** It is no longer the default command:
  replace `depinv --provider ...` with `depinv packages --provider ...`. The options and reports are unchanged.
- **Breaking: an unknown option is now an error.** A misspelled option, such as `--patern`, used to be silently
  ignored; the command now stops with an error naming it.

### Added

- **`depinv frameworks` (alias `fwk`) lists the target framework of every .NET project**, with no filter. It reads
  `TargetFramework`, `TargetFrameworks` and the legacy `TargetFrameworkVersion` (`v4.7.2` is written `net472`),
  falls back to the nearest `Directory.Build.props`, and writes `frameworks.csv`, `repositories.csv` and
  `report.json` in the `framework-report` folder. Projects whose framework cannot be resolved are listed as
  `Unresolved`.
- **`depinv feed` reads published package versions from an Azure Artifacts feed**: the frameworks each version
  ships and its dependencies, framework references included. The versions come from `--package ID[@VERSION]`,
  from `--versions-from` (a `dependencies.csv`) or from `--pattern` / `--regex` on the feed's packages, with
  `--latest` to keep the highest one. `--dependency-pattern` / `--dependency-regex` filter the dependencies. It writes
  `package-frameworks.csv`, `package-dependencies.csv`, `packages.csv` and `report.json` in the `feed-report` folder;
  `packages.csv` also gives each version's publication (date, listed, views) and all metadata fields from its `.nuspec` file
  (authors, description, license, source repository and commit...). The PAT needs the Packaging (Read) scope.

### Fixed

- **`depinv packages` respects the case of Git paths.** Two project files whose paths differ only by case, such as
  `/src/App/App.csproj` and `/src/app/App.csproj`, are now both reported; one of them used to be dropped. A project
  no longer takes a central version from a `Directory.Packages.props` in a folder that differs from its own only by
  case.

## [1.0.0-preview.3] - 2026-10-26

### Changed

- **Breaking: the package is now `DependencyInventory`, not `Reefact.DependencyInventory`.** The command is still
  `depinv`. Versions 1.0.0-preview.1 and 1.0.0-preview.2 were published under the old id, which receives no
  further release. To migrate, run `dotnet tool uninstall --global Reefact.DependencyInventory`, then
  `dotnet tool install --global DependencyInventory --prerelease`.

### Added

- **An icon**, on the package (shown on nuget.org and in NuGet clients) and on the Windows executable.

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
- **Distribution as a .NET tool**: `dotnet tool install --global Reefact.DependencyInventory --prerelease` installs the `depinv`
  command. The package is licensed under Apache-2.0.

[Unreleased]: https://github.com/Reefact/dependency-inventory/compare/v1.0.0-preview.7...HEAD
[1.0.0-preview.7]: https://github.com/Reefact/dependency-inventory/compare/v1.0.0-preview.6...v1.0.0-preview.7
[1.0.0-preview.6]: https://github.com/Reefact/dependency-inventory/compare/v1.0.0-preview.5...v1.0.0-preview.6
[1.0.0-preview.5]: https://github.com/Reefact/dependency-inventory/compare/v1.0.0-preview.4...v1.0.0-preview.5
[1.0.0-preview.4]: https://github.com/Reefact/dependency-inventory/compare/v1.0.0-preview.3...v1.0.0-preview.4
[1.0.0-preview.3]: https://github.com/Reefact/dependency-inventory/compare/v1.0.0-preview.2...v1.0.0-preview.3
[1.0.0-preview.2]: https://github.com/Reefact/dependency-inventory/compare/v1.0.0-preview.1...v1.0.0-preview.2
[1.0.0-preview.1]: https://github.com/Reefact/dependency-inventory/releases/tag/v1.0.0-preview.1
