# DependencyInventory

.NET 10 command-line tool built with Spectre.Console.Cli to inventory NuGet references in Azure DevOps projects or GitLab groups.

Architecture: Git provider → branch selection → NuGet extraction → glob/regex filters → reports. There is no implicit filter: one or more patterns or regexes are required.

## Azure DevOps

The URL designates the Azure DevOps project, not a Git repository.

~~~powershell
dotnet run --project ./src/DependencyInventory -- --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "Stime.*"
~~~

The Azure DevOps login and PAT are needed (see Authentication); the PAT needs the Code (Read) scope.

## GitLab

The URL designates the GitLab group, possibly a subgroup, for example https://gitlab.example.com/red/web. Projects of subgroups are included.

~~~powershell
dotnet run --project ./src/DependencyInventory -- --provider gitlab --url "https://gitlab.example.com/red/web" --pattern "Stime.*"
~~~

Only a PAT is needed (see Authentication), with the read_api scope and access rights on the group. The GitLab provider uses PRIVATE-TOKEN; no login is needed.

## Authentication

Credentials are resolved in this order: a credentials file when --credentials-file is given (Linux and macOS only), then the credentials stored in the system vault, then an interactive prompt.

### Windows: store the credentials once

~~~powershell
dotnet run --project ./src/DependencyInventory -- login --provider azure --url "https://dev.azure.com/Organization/Project"
dotnet run --project ./src/DependencyInventory -- login --provider gitlab --url "https://gitlab.example.com/red/web"
~~~

login asks once for the Azure DevOps login and PAT, or for the GitLab PAT, and stores them in the Windows Credential Manager, encrypted with your Windows account. There is one entry per Azure DevOps organization and per GitLab instance. Scans then use them without prompting; a scan never writes to the vault. logout, with the same options, deletes them.

--credentials-file is not available on Windows.

### Linux and macOS: credentials file or prompt

The system vaults are not supported yet (#16); until then, credentials are either typed at each scan, or read from a file with --credentials-file:

- With --credentials-file and no value, the CLI reads .azure-credentials or .gitlab-credentials, depending on --provider, in the current folder.
- With --credentials-file followed by a path, it uses that explicit path (e.g. "$HOME/secrets/azure-credentials"; a quoted ~ is not expanded by the shell).

An Azure DevOps file holds two lines, the login then the PAT; a GitLab file holds the PAT alone:

~~~text
firstname.lastname@company.com
AZURE_DEVOPS_PAT
~~~

Credentials files hold secrets in clear text: do not commit them, restrict their permissions and use read-only tokens. The default files are excluded by .gitignore. Credentials files will be removed once the macOS and Linux vaults are supported.

## NuGet filtering

Repeatable options, combined with OR:
- --pattern "Stime.*": glob applied to the whole package ID. * = zero or more characters, ? = exactly one character. Dots are literal.
- --regex "^Serilog([.]|$)": .NET regex, substring search (unless anchored).
- --case-sensitive: optional; matching is case-insensitive by default.

~~~powershell
dotnet run --project ./src/DependencyInventory -- --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "Stime.*" --pattern "Microsoft.Extensions.*"

dotnet run --project ./src/DependencyInventory -- --provider gitlab --url "https://gitlab.example.com/red/web" --regex "^Serilog([.]|$)"

dotnet run --project ./src/DependencyInventory -- --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "*.Hosting" --regex "^Stime[.]Core[.]"
~~~

Each dependency appears once per declaration, even when it matches several filters. The MatchedFilters column keeps the filters that found the package.

## Branch selection

A single branch is scanned per repository, in the order main → release (release/* included) → develop → dev → first other branch (alphabetical). There is no fallback to a second branch when no dependency is found.

Both providers read files from each repository's selected commit without cloning it.

## Parallelism

By default the scan is sequential: one repository after the other, one file after the other. --parallelism (1 to 8) allows several simultaneous requests; --parallelism-mode is then required and specifies whether files or repositories are processed in parallel:

- files: one repository at a time, several of its files read at once; suited to large repositories and to --repository;
- repositories: several repositories at once, each reading its files one after the other; suited to groups of many small repositories.

Only one level runs in parallel, so --parallelism is also the maximum number of simultaneous HTTP requests. Reports are identical, in the same order, whatever the parallelism and the mode. More parallelism raises the risk of HTTP 429; such responses are retried (see Transient errors), but a moderate value (2 to 4) reduces the risk of hitting the providers' rate limits.

~~~powershell
dotnet run --project ./src/DependencyInventory -- --provider gitlab --url "https://gitlab.example.com/red/web" --pattern "Stime.*" --parallelism 4 --parallelism-mode repositories
~~~

## Transient errors

A request to Azure DevOps or GitLab is retried, up to 5 attempts in total, when it fails transiently: HTTP 429 (too many requests), 502, 503 or 504 (service unavailable), network error, or response timeout. The wait follows the Retry-After header when present, otherwise an exponential backoff with a random part; each retry delay is capped at 60 seconds and announced in the console. Other errors (400, 401, 403, 404...) are not retried. When the attempts are exhausted, the error is reported as before in repositories.csv.

The 120 s timeout applies to each attempt, including the time needed to read the full response, but excluding delays between attempts. A timeout on a file makes the repository partial without stopping the scan; only Ctrl+C stops it.

## NuGet extraction

Files: *.csproj, *.fsproj, *.vbproj, *.props, *.targets and packages.config. The bin, obj, node_modules and .git folders are ignored.

PackageReference, GlobalPackageReference and package are direct references; PackageVersion is a central version declaration, not a proof of direct consumption.

The scanner can match a version from the nearest Directory.Packages.props, without fully evaluating MSBuild. It does not resolve transitive dependencies and does not run a NuGet restore.

## Reports

The dependency-report folder (configurable with --output) contains:

- dependencies.csv: filtered direct references, package, version, file, branch, commit, MatchedFilters;
- central-versions.csv: central version declarations matching the filters;
- repositories.csv: coverage of every repository found and its scan status;
- report.json: full JSON report, including the applied filters.

CSV files are UTF-8 with BOM and semicolon-separated.

## Diagnosing a repository

~~~powershell
dotnet run --project ./src/DependencyInventory -- --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "Stime.*" --repository api-order-bo --diagnostics
~~~

The diagnostics.json file lists every NuGet reference extracted, even outside the filters, the available branches, the selected one, the commits and the files examined.

## Build / tests

~~~powershell
dotnet build ./src/DependencyInventory/DependencyInventory.csproj
dotnet test ./tests/DependencyInventory.Tests/DependencyInventory.Tests.csproj
~~~

## Commit messages

Commits follow [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/) and are written in English: `<type>[(scope)][!]: <description>`, with a type among feat, fix, build, chore, ci, docs, perf, refactor, revert, style, test, a lowercase imperative description, no trailing period, and a header of 72 characters at most. A breaking change is signalled by a `!` before the colon, by a `BREAKING CHANGE:` (or `BREAKING-CHANGE:`) footer, or both.

The commit-lint workflow checks every commit of a pull request. For the same check locally, enable the hook once per clone:

~~~powershell
git config core.hooksPath .githooks
~~~

## References

- https://learn.microsoft.com/en-us/rest/api/azure/devops/git/
- https://docs.gitlab.com/api/groups/
- https://docs.gitlab.com/api/branches/
- https://docs.gitlab.com/api/repositories/
- https://docs.gitlab.com/api/repository_files/
