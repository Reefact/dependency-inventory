# DependencyInventory

**Find every NuGet package your Git repositories declare, across a whole Azure DevOps project or GitLab group.**

|  |  |
| :-- | :-- |
| **Build** | [![ci](https://github.com/Reefact/dependency-inventory/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Reefact/dependency-inventory/actions/workflows/ci.yml) |
| **Package** | [![NuGet](https://img.shields.io/nuget/vpre/Reefact.DependencyInventory?logo=nuget)](https://www.nuget.org/packages/Reefact.DependencyInventory) ![.NET 10](https://img.shields.io/badge/.NET-10-512BD4) |
| **Project** | [![License: Apache-2.0](https://img.shields.io/badge/License-Apache%202.0-blue)](https://github.com/Reefact/dependency-inventory/blob/main/LICENSE) |

## 🚨 The problem

Which of your repositories still reference a given package, and in which version? Answering means opening
every repository, every project file and every `Directory.Packages.props`, one at a time.

## ✅ The solution

Point `depinv` at an Azure DevOps project or a GitLab group, say which packages you care about, and read
the answer in a CSV file:

~~~powershell
depinv --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "Serilog.*"
~~~

It reads the files of each repository's selected branch through the provider's API, without cloning anything,
extracts the NuGet references, keeps the ones matching your filters and writes the reports. It is read-only:
the tool only sends GET requests.

There is no implicit filter: one or more `--pattern` or `--regex` options are required.

## 📦 Install

~~~powershell
dotnet tool install --global Reefact.DependencyInventory
~~~

The command is then available as `depinv` (`depinv --help`). To update it:

~~~powershell
dotnet tool update --global Reefact.DependencyInventory
~~~

It needs the .NET 10 runtime.

## 🚀 Quick start

### Azure DevOps

The URL designates the Azure DevOps project, not a Git repository.

~~~powershell
depinv --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "Contoso.*"
~~~

The Azure DevOps login and PAT are needed (see Authentication); the PAT needs the Code (Read) scope.

### GitLab

The URL designates the GitLab group, possibly a subgroup, for example https://gitlab.example.com/red/web. Projects of subgroups are included.

~~~powershell
depinv --provider gitlab --url "https://gitlab.example.com/red/web" --pattern "Contoso.*"
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
depinv --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "Contoso.*" --pattern "Microsoft.Extensions.*"

depinv --provider gitlab --url "https://gitlab.example.com/red/web" --regex "^Serilog([.]|$)"

depinv --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "*.Hosting" --regex "^Contoso[.]Core[.]"
~~~

Each dependency appears once per declaration, even when it matches several filters. The MatchedFilters column keeps the filters that found the package.

## 📄 Reports

The dependency-report folder (configurable with --output) contains:

- dependencies.csv: filtered direct references, package, version, file, branch, commit, MatchedFilters;
- central-versions.csv: central version declarations matching the filters;
- repositories.csv: coverage of every repository found and its scan status;
- report.json: full JSON report, including the applied filters.

CSV files are UTF-8 with BOM and semicolon-separated.

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
depinv --provider gitlab --url "https://gitlab.example.com/red/web" --pattern "Contoso.*" --parallelism 4 --parallelism-mode repositories
~~~

### Transient errors

A request to Azure DevOps or GitLab is retried, up to 5 attempts in total, when it fails transiently: HTTP 429 (too many requests), 502, 503 or 504 (service unavailable), network error, or response timeout. The wait follows the Retry-After header when present, otherwise an exponential backoff with a random part; each retry delay is capped at 60 seconds and announced in the console. Other errors (400, 401, 403, 404...) are not retried. When the attempts are exhausted, the error is reported as before in repositories.csv.

The 120 s timeout applies to each attempt, including the time needed to read the full response, but excluding delays between attempts. A timeout on a file makes the repository partial without stopping the scan; only Ctrl+C stops it.

### Diagnosing a repository

~~~powershell
depinv --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "Contoso.*" --repository api-order-bo --diagnostics
~~~

The diagnostics.json file lists every NuGet reference extracted, even outside the filters, the available branches, the selected one, the commits and the files examined.

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
