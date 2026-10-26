# DependencyInventory — guide for Claude Code

.NET 10 CLI that inventories the NuGet references declared in the Git repositories of an Azure DevOps project or a GitLab group. The README describes usage.

## Build & test

- Build: `dotnet build DependencyInventory.sln`
- Test: `dotnet test DependencyInventory.sln`
- The projects target `net10.0` (LTS).
- CI (`.github/workflows/ci.yml`) builds and tests in Release on Ubuntu and Windows with the .NET 10 SDK.

## Repository map

- `src/DependencyInventory/`
  - `Program.cs`, `ScanCommand.cs`: CLI options, prompts, scan loop, console output.
  - `RepositoryProviders.cs`, `AzureDevOpsClient.cs`, `GitLabRepositoryProvider.cs`: Git hosting APIs.
  - `CredentialsFile.cs`: credentials file reading.
  - `BranchSelector.cs`: the one branch scanned per repository.
  - `NuGetDependencyParser.cs`: NuGet references extracted from project files.
  - `DependencyMatcher.cs`: `--pattern` / `--regex` filters.
  - `ReportWriter.cs`, `Models.cs`: reports and their data.
- `tests/DependencyInventory.Tests/`: unit tests.
- `tools/commit-lint/`, `.githooks/`: commit message lint.

## Rules

- **Commits** follow Conventional Commits 1.0.0 and are written in English. `tools/commit-lint/lint-commit-message.sh` checks them, in CI on every pull request and locally once the hook is enabled with `git config core.hooksPath .githooks`.
- **Reports are a contract.** `dependencies.csv`, `central-versions.csv`, `repositories.csv`, `report.json` and `diagnostics.json` must not change unless the change is intended. Enum names (`PackageKind`, `VersionSource`, `CoverageStatus`) are written as-is in them: renaming a member changes the output.
- **Read-only.** The tool never writes to Azure DevOps or GitLab: GET requests only.
- **Secrets** (PAT, login) are never logged nor serialized.
- **Tests and warnings.** Every behavior change comes with a test. The build treats warnings as errors.
- **Languages.** README and CLI messages are in French; code, comments and commit messages are in English.
