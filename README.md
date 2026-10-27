# DependencyInventory

**Find every NuGet package your Git repositories declare, across a whole Azure DevOps project or GitLab group.**

|  |  |
| :-- | :-- |
| **Build** | [![ci](https://github.com/Reefact/dependency-inventory/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Reefact/dependency-inventory/actions/workflows/ci.yml) |
| **Package** | [![NuGet](https://img.shields.io/nuget/vpre/DependencyInventory?logo=nuget)](https://www.nuget.org/packages/DependencyInventory) ![.NET 10](https://img.shields.io/badge/.NET-10-512BD4) |
| **Project** | [![License: Apache-2.0](https://img.shields.io/badge/License-Apache%202.0-blue)](https://github.com/Reefact/dependency-inventory/blob/main/LICENSE) |

## 🚨 The problem

Which of your repositories still reference a given package, and which version do they use? Answering means opening
every repository, every project file and every `Directory.Packages.props`, one at a time.

## ✅ The solution

Point `depinv` at an Azure DevOps project or a GitLab group, say which packages you care about (here `Serilog` and
the packages whose name starts with `Serilog.`), and read the answer in a CSV file:

~~~powershell
depinv packages --provider azure --url "https://dev.azure.com/Organization/Project" --regex "^Serilog([.]|$)"
~~~

It reads the files of each repository's selected branch through the provider's API, without cloning anything,
extracts the NuGet references, keeps the ones matching your filters and writes the reports. It is read-only:
the tool only sends GET requests.

There is no implicit filter: one or more `--pattern` or `--regex` options are required. `depinv pkg` is a shorter
alias of `depinv packages`.

To inventory the target frameworks of every .NET project, use `depinv frameworks` (see
[Target frameworks](#-target-frameworks)). To read the frameworks and dependencies of published package versions
from an Azure Artifacts feed, use `depinv feed` (see [Published packages](#-published-packages)).

## 📦 Install

~~~powershell
dotnet tool install --global DependencyInventory --prerelease
~~~

The command is then available as `depinv` (`depinv --help`). To update it:

~~~powershell
dotnet tool update --global DependencyInventory --prerelease
~~~

`--prerelease` is required while only preview versions are published (1.0.0 is not released yet): without it, the install fails with "no stable version". Drop it once a stable version exists.

It needs the .NET 10 runtime.

Coming from `Reefact.DependencyInventory` (versions 1.0.0-preview.1 and 1.0.0-preview.2)? The package was renamed and both provide the `depinv` command, so remove the old one before installing the new one:

~~~powershell
dotnet tool uninstall --global Reefact.DependencyInventory
dotnet tool install --global DependencyInventory --prerelease
~~~

## 🚀 Quick start

### Azure DevOps

The URL designates the Azure DevOps project, not a Git repository.

~~~powershell
depinv packages --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "Contoso.*"
~~~

The Azure DevOps login and PAT are needed (see Authentication); the PAT needs the Code (Read) scope, and the
Packaging (Read) scope for `depinv feed`.

### GitLab

The URL designates the GitLab group, possibly a subgroup, for example https://gitlab.example.com/red/web. Projects of subgroups are included.

~~~powershell
depinv packages --provider gitlab --url "https://gitlab.example.com/red/web" --pattern "Contoso.*"
~~~

Only a PAT is needed (see Authentication), with the read_api scope and access rights on the group. The GitLab provider uses PRIVATE-TOKEN; no login is needed.

## 🔐 Authentication

Credentials are resolved in this order: a credentials file when --credentials-file is given (Linux and macOS only), then the credentials stored in the system vault, then an interactive prompt.

### Windows: store the credentials once

~~~powershell
depinv login --provider azure --url "https://dev.azure.com/Organization/Project"
depinv login --provider gitlab --url "https://gitlab.example.com/red/web"
~~~

login asks once for the Azure DevOps login and PAT, or for the GitLab PAT, and stores them in the Windows Credential Manager, encrypted with your Windows account. There is one entry per Azure DevOps organization and per GitLab instance. Scans then use them without prompting; a scan never writes to the vault. logout, with the same options, deletes them.

--credentials-file is not available on Windows.

### Linux and macOS: credentials file or prompt

The system vaults are not supported yet ([#16](https://github.com/Reefact/dependency-inventory/issues/16)); until then, credentials are either typed at each scan, or read from a file with --credentials-file:

- With --credentials-file and no value, the CLI reads .azure-credentials or .gitlab-credentials, depending on --provider, in the current folder.
- With --credentials-file followed by a path, it uses that explicit path (e.g. "$HOME/secrets/azure-credentials"; a quoted ~ is not expanded by the shell).

An Azure DevOps file holds two lines, the login then the PAT; a GitLab file holds the PAT alone:

~~~text
firstname.lastname@company.com
AZURE_DEVOPS_PAT
~~~

Credentials files hold secrets in clear text: do not commit them, restrict their permissions and use read-only tokens. The default files are excluded by .gitignore. Credentials files will be removed once the macOS and Linux vaults are supported.

## 🎯 NuGet filtering

Repeatable options, combined with OR:
- --pattern "Contoso.*": glob applied to the whole package ID. * = zero or more characters, ? = exactly one character. Dots are literal.
- --regex "^Serilog([.]|$)": .NET regex, substring search (unless anchored).
- --case-sensitive: optional; matching is case-insensitive by default.

~~~powershell
depinv packages --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "Contoso.*" --pattern "Microsoft.Extensions.*"

depinv packages --provider gitlab --url "https://gitlab.example.com/red/web" --regex "^Serilog([.]|$)"

depinv packages --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "*.Hosting" --regex "^Contoso[.]Core[.]"
~~~

Each dependency appears once per declaration, even when it matches several filters. The MatchedFilters column keeps the filters that found the package.

## 📄 Reports

The dependency-report folder (configurable with --output) contains:

- dependencies.csv: filtered direct references, package, version, file, branch, commit, MatchedFilters;
- central-versions.csv: central version declarations matching the filters;
- repositories.csv: coverage of every repository found and its scan status;
- report.json: full JSON report, including the applied filters.

CSV files are UTF-8 with BOM and semicolon-separated.

## 🧱 Target frameworks

`depinv frameworks` (alias `depinv fwk`) lists the target framework of every .NET project found, with no filter:

~~~powershell
depinv frameworks --provider azure --url "https://dev.azure.com/Organization/Project"
~~~

It takes the same options as `depinv packages` except the filters (`--pattern`, `--regex`, `--case-sensitive`).
The framework-report folder (configurable with --output) contains:

- frameworks.csv: one line per project file and target framework: ProjectFile, Framework, FrameworkSource, branch, commit;
- repositories.csv: coverage of every repository found and its scan status, with the number of projects found;
- report.json: full JSON report.

Files: *.csproj, *.fsproj, *.vbproj and Directory.Build.props, outside the bin, obj, node_modules and .git folders.

- TargetFrameworks gives one line per framework; the legacy TargetFrameworkVersion is written as a moniker
  (v4.7.2 becomes net472).
- A project that declares no framework inherits it from the nearest Directory.Build.props, the only one MSBuild
  imports automatically; FrameworkSource is then DirectoryBuildProps instead of Project.
- A framework that cannot be read literally, such as `$(DefaultTargetFramework)`, or a project whose framework is
  found nowhere, or a project file that cannot be read, gives a line with an empty Framework and FrameworkSource
  Unresolved: the project is still listed. An unreadable file also makes the repository Partial in repositories.csv.
- MSBuild conditions are not evaluated: every declared value is listed.

## 📦 Published packages

`depinv feed` reads published package versions from an Azure Artifacts feed: the frameworks each version ships and
its dependencies, framework references included. Where `depinv frameworks` tells what the code of a repository
targets today, `depinv feed` tells what each published version actually contains, such as `2.0.0-beta.6`.

~~~powershell
depinv feed --provider azure --url "https://dev.azure.com/Organization/Project" --feed my-feed --package "Contoso.Messaging@2.0.4"
~~~

`--provider`, `--url` and the credentials are those of the other commands; the PAT needs the Packaging (Read) scope.
`--feed` names the feed as an Azure Pipelines task does: `my-feed` for an organization feed, `Project/my-feed` for a
project feed. GitLab package registries are not supported yet.

The versions to read come from one or more of these options, combined:

- --package "Contoso.Messaging@2.0.4": one version; --package "Contoso.Messaging" reads every published version (repeatable);
- --versions-from dependency-report/dependencies.csv: every package version listed by `depinv packages`, which are the
  versions your repositories reference; lines without a literal version are skipped;
- --pattern "Contoso.*" or --regex: every published version of the feed's packages whose id matches.

--latest keeps only the highest version, prereleases included, of the packages read without a version.
--dependency-pattern "Microsoft.AspNetCore.*" and --dependency-regex keep only the matching dependencies; all of them are
reported by default. --case-sensitive applies to both kinds of filters.

~~~powershell
depinv packages --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "*.Vnext.*"
depinv feed --provider azure --url "https://dev.azure.com/Organization/Project" --feed my-feed --versions-from dependency-report/dependencies.csv --dependency-pattern "Microsoft.AspNetCore.*"
~~~

The feed-report folder (configurable with --output) contains:

- package-frameworks.csv: one line per package version and framework shipped;
- package-dependencies.csv: one line per package version, framework and dependency, with its VersionRange and its
  Kind: Package, or FrameworkReference (such as Microsoft.AspNetCore.App, whose version is the framework's);
- packages.csv: whether each package version could be read: OK, NotFound or Failed;
- report.json: full JSON report, including the applied filters.

Package and Version are written as requested, so these files join dependencies.csv on these two columns.

The frameworks come from the dependency groups of each version's .nuspec. A package without dependency groups names
no framework there: its lib/ and ref/ folders are read instead. Frameworks are written as short monikers, such as
netstandard2.0 or net8.0, like in frameworks.csv. A dependency without framework applies to all of them.

--parallelism (1 to 8) reads several versions at once; --diagnostics writes every dependency read, unfiltered, to
diagnostics.json.

## 🔎 How it works

### Branch selection

A single branch is scanned per repository, in the order main → release (release/* included) → develop → dev → first other branch (alphabetical). There is no fallback to a second branch when no dependency is found.

Both providers read files from each repository's selected commit without cloning it.

### NuGet extraction

Files: *.csproj, *.fsproj, *.vbproj, *.props, *.targets and packages.config. The bin, obj, node_modules and .git folders are ignored.

PackageReference, GlobalPackageReference and package are direct references; PackageVersion is a central version declaration, not a proof of direct consumption.

The scanner can match a version from the nearest Directory.Packages.props, without fully evaluating MSBuild. It does not resolve transitive dependencies and does not run a NuGet restore.

### Parallelism

By default the scan is sequential: one repository after the other, one file after the other. --parallelism (1 to 8) allows several simultaneous requests; --parallelism-mode is then required and specifies whether files or repositories are processed in parallel:

- files: one repository at a time, several of its files read at once; suited to large repositories and to --repository;
- repositories: several repositories at once, each reading its files one after the other; suited to groups of many small repositories.

Only one level runs in parallel, so --parallelism is also the maximum number of simultaneous HTTP requests. Reports are identical, in the same order, whatever the parallelism and the mode. More parallelism raises the risk of HTTP 429; such responses are retried (see Transient errors), but a moderate value (2 to 4) reduces the risk of hitting the providers' rate limits.

~~~powershell
depinv packages --provider gitlab --url "https://gitlab.example.com/red/web" --pattern "Contoso.*" --parallelism 4 --parallelism-mode repositories
~~~

### Transient errors

A request to Azure DevOps or GitLab is retried, up to 5 attempts in total, when it fails transiently: HTTP 429 (too many requests), 502, 503 or 504 (service unavailable), network error, or response timeout. The wait follows the Retry-After header when present, otherwise an exponential backoff with a random part; each retry delay is capped at 60 seconds and announced in the console. Other errors (400, 401, 403, 404...) are not retried. When the attempts are exhausted, the error is reported as before in repositories.csv.

The 120 s timeout applies to each attempt, including the time needed to read the full response, but excluding delays between attempts. A timeout on a file makes the repository partial without stopping the scan; only Ctrl+C stops it.

### Diagnosing a repository

~~~powershell
depinv packages --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "Contoso.*" --repository api-order-bo --diagnostics
~~~

The diagnostics.json file lists every NuGet reference extracted, even outside the filters, the available branches, the selected one, the commits and the files examined. With `depinv frameworks`, it lists the frameworks declared by every file examined instead.

## 🤝 Contributing

Build, tests and commit conventions are described in [CONTRIBUTING.md](https://github.com/Reefact/dependency-inventory/blob/main/CONTRIBUTING.md).

## 📜 License

[Apache-2.0](https://github.com/Reefact/dependency-inventory/blob/main/LICENSE).

## 📚 References

- https://learn.microsoft.com/en-us/rest/api/azure/devops/git/
- https://docs.gitlab.com/api/groups/
- https://docs.gitlab.com/api/branches/
- https://docs.gitlab.com/api/repositories/
- https://docs.gitlab.com/api/repository_files/
