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
extracts the NuGet references, keeps the ones matching your filters and writes the reports. For every reference, it
also tells whether the code actually uses the package, without compiling anything (see
[Package usage](#-package-usage)). It is read-only: the tool only sends GET requests.

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
Packaging (Read) scope for `depinv feed` and the `--feed` option of `depinv packages`.

### GitLab

The URL designates the GitLab group, possibly a subgroup, for example https://gitlab.example.com/red/web. Projects of subgroups are included.

~~~powershell
depinv packages --provider gitlab --url "https://gitlab.example.com/red/web" --pattern "Contoso.*"
~~~

Only a PAT is needed (see Authentication), with the read_api scope and access rights on the group. The GitLab provider uses PRIVATE-TOKEN; no login is needed.

## 🔐 Authentication

Credentials are resolved in this order: a credentials file when --credentials-file is given (Linux and macOS only), then the credentials stored in the system vault, then an interactive prompt.

### Store the credentials once

~~~powershell
depinv login --provider azure --url "https://dev.azure.com/Organization/Project"
depinv login --provider gitlab --url "https://gitlab.example.com/red/web"
~~~

login asks once for the Azure DevOps login and PAT, or for the GitLab PAT, and stores them in the system vault: the Windows Credential Manager, encrypted with your Windows account, on macOS the login keychain, or on Linux the Secret Service (GNOME Keyring, KWallet). There is one entry per Azure DevOps organization and per GitLab instance. Scans then use them without prompting; a scan never writes to the vault. logout, with the same options, deletes them.

On Linux, the Secret Service is reached through the secret-tool command (package libsecret-tools on Debian and Ubuntu, libsecret elsewhere); the PAT is passed on its standard input, never on its command line. Each entry is labelled "depinv" followed by its key, with the key and the login as attributes. Where no Secret Service can be reached, such as a server without a desktop session, login and logout end with the reason and exit code 2, and scans print it and ask for the credentials, unless --credentials-file is given.

On macOS, each entry is a generic password of the default keychain, usually the login keychain, whose service is its key and whose account is the login. macOS may ask once to let depinv read it, for example after an update of the tool. Where the keychain cannot be used, such as a locked keychain in an SSH session, login and logout end with the reason and exit code 2, and scans print it and ask for the credentials, unless --credentials-file is given.

--credentials-file is not available on Windows.

### Linux and macOS: credentials file

Where the vault cannot be used, such as unattended runs on a server, credentials are either typed at each scan, or read from a file with --credentials-file.

- With --credentials-file and no value, the CLI reads .azure-credentials or .gitlab-credentials, depending on --provider, in the current folder.
- With --credentials-file followed by a path, it uses that explicit path (e.g. "$HOME/secrets/azure-credentials"; a quoted ~ is not expanded by the shell).

An Azure DevOps file holds two lines, the login then the PAT; a GitLab file holds the PAT alone:

~~~text
firstname.lastname@company.com
AZURE_DEVOPS_PAT
~~~

Credentials files hold secrets in clear text: do not commit them, restrict their permissions and use read-only tokens. The default files are excluded by .gitignore.

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

- dependencies.csv: filtered direct references: Scope, Repository, Branch, Commit, File, ProjectType (see
  [Project types](#project-types)), Package, Version, VersionSource, MatchedFilters, the usage of the package: Usage,
  Source and Feed (see [Package usage](#-package-usage)), and Url;
- central-versions.csv: central version declarations matching the filters: Scope, Repository, Branch, Commit, File,
  Package, Version, VersionSource, MatchedFilters and Url;
- repositories.csv: coverage of every repository found: Scope, Repository, Branch, Commit, CommitDate (see
  [Activity](#activity)), its scan Status, FilesExamined, References, Details and the Url of the repository;
- used-types.csv: with --used-types only, the types of each package referenced by the code;
- transitive-dependencies.csv: with --transitive only, the packages each project depends on without declaring them
  (see [Transitive dependencies](#-transitive-dependencies));
- report.json: full JSON report, including the applied filters, the usage and the used types.

In every report, Scope is the Azure DevOps project or the GitLab group (subgroup included) of the repository, not a
.NET project.

CSV files are UTF-8 with BOM and comma-separated; a value holding the separator, a quote or a line break is quoted.
`--csv-delimiter` chooses another separator, in every command that writes reports: `--csv-delimiter ";"`, which Excel
opens directly where the decimal separator is a comma (as in French), or `--csv-delimiter tab`. `depinv feed
--versions-from` reads a dependencies.csv whatever its separator.

## 🧩 Package usage

For every reference, `depinv packages` tells whether the code of the project uses the package. It reads the package's
assemblies to know its public types, then the C# files of the project to know the names they reference: no restore,
no build, so it works on solutions that need manual steps to compile. The Usage column of dependencies.csv says:

- Used: at least one type of the package is referenced by the C# code of the project;
- Unused: the package exposes types and none is referenced;
- BuildOnly: the package acts on the build without being referenced in code: analyzers, source generators, packages
  with build, tool or content assets only, development dependencies, test adapters (xunit.runner.visualstudio,
  Microsoft.NET.Test.Sdk...). It is never reported unused;
- UsedTransitively: with --transitive, no type of the package is referenced, but it is the only declared reference
  bringing a package that the code of the project uses without declaring it: removing it would break the build. When
  several references bring that package, none is needed on its own and they stay Unused, but removing all of them
  would break the build: diagnostics.json names the others. Only the transitive packages matching --pattern or --regex
  are known; without --transitive, or when the package the code uses does not match the filters, the reference stays
  Unused. Check by building before removing an Unused reference;
- Undetermined: the usage could not be computed, e.g. the package was found in no source, the version is an MSBuild
  property, the project is in VB or F#, or a source file could not be read. --diagnostics gives the reason.

The packages are read from the Azure Artifacts feeds given by --feed, in the order given, then from nuget.org:

~~~powershell
depinv packages --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "Contoso.*" --feed my-feed --used-types

depinv packages --provider gitlab --url "https://gitlab.example.com/red/web" --pattern "Contoso.*" --feed "https://dev.azure.com/Contoso/_packaging/internal" --feed "https://dev.azure.com/Partner/Shared/_packaging/libs" --no-nuget-org
~~~

- --feed (repeatable): `my-feed` or `Project/my-feed` for a feed of the --url organization, as in `depinv feed`, or the
  feed URL: https://dev.azure.com/organization[/project]/_packaging/feed, or its pkgs.dev.azure.com service index. The
  URL form is required with --provider gitlab, and reads feeds of other organizations. A feed of the --url organization
  is read with the same credentials; another organization's feeds with its own, resolved like the others (vault entry of
  that organization, `depinv login --provider azure --url` with one of its projects, or the prompt; with
  --credentials-file, the Azure credentials file). The PAT needs the Packaging (Read) scope.
- nuget.org is read anonymously, unless --no-nuget-org is given; with --no-nuget-org, at least one --feed is required.
  Without --feed, internal packages end up Undetermined.
- --used-types also writes used-types.csv: one line per type referenced, with Scope, Repository, Branch, Commit,
  File, Package, Version and Source to join dependencies.csv, the full name of the Type, and Files, the number of C# files
  referencing it (0 for a type only named by a Using item of the project file).

The Source column gives the real origin of the package: Feed, with the feed in the Feed column, for a package
published to a feed; NuGetOrg for a public package, even when read from a feed that saved it from nuget.org as an
upstream source. A version is looked up among those a feed holds, never fetched through its upstream sources, so
reading it does not save it to the feed.

How the usage is decided:

- The types of a package are the public types of its assemblies, read from ref/{framework} when present, otherwise
  lib/{framework}, for every framework. Assemblies are read, never loaded.
- The C# files of a project are those under its folder, except bin and obj, and except those of a project in a
  subfolder. A file references a type of the package when it names it (Bus, [Subscribe] for SubscribeAttribute) in a
  namespace it imports: using, global using, the enclosing namespace, the Using items of the project file and of the
  nearest Directory.Build.props, and the implicit usings of its SDK (ImplicitUsings). A fully qualified name, a
  using static, an alias, and an extension method called in a file importing its namespace count as well. Code in an #if
  branch whose condition depends on symbols (#if DEBUG, #if NET8_0) may or may not be compiled, since the symbols a
  project defines are unknown: a package only referenced there is Undetermined, never Used. Code never compiled
  (#if false) is ignored.
- A reference declared in a Directory.Build.props, packages.config or another imported file applies to every project
  under its folder; for Directory.Build.props, Directory.Build.targets and Directory.Packages.props, which MSBuild
  imports automatically, only to the projects for which it is the nearest of its name.
- A package without assemblies of its own, such as xunit, brings the types of its dependencies, for the target
  frameworks of the project.
- The version read is the one NuGet restores: the lowest version published in the sources that satisfies the declared
  one (1.2.3 means 1.2.3 or later) or range, the highest one for a floating version (1.*); diagnostics.json gives it as
  ResolvedVersion.

Accepted limits: there is no semantic model. A package used only through reflection, configuration (such as Serilog
sinks declared in appsettings.json) or dependency injection by convention can be reported Unused, and a package used
only through another package (the project calls Z, Z uses Y) is not used by the project. Only C# is analyzed.

The content of each package version is cached in the local application data folder (depinv/package-content), as a
published version never changes; the next runs do not download it again. The NuGet global packages folder
(NUGET_PACKAGES, or ~/.nuget/packages) is read before any download. Reading every C# file takes more requests than the
project files only: --parallelism applies to them as well. The C# files are only read for the references whose
package was found in a source: scanning internal packages without --feed reads none, their usage being undetermined.

## 🕸️ Transitive dependencies

A project that references Z also depends on Y when Z depends on Y, even though it never asked for Y. With --transitive,
`depinv packages` lists those packages too, per project and target framework, so that --pattern and --regex also find
the projects exposed to a package only through another one:

~~~powershell
depinv packages --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "Contoso.Messaging" --feed my-feed --transitive
~~~

Every declared reference takes part in the graph, whether it matches the filters or not; only the transitive packages
matching them are written to transitive-dependencies.csv: Scope, Repository, Branch, Commit, File (the project file),
ProjectType, Framework, Package, Version, Via, Resolution, MatchedFilters, Usage, Source and Feed. dependencies.csv keeps the
declared references only.

- Via is the path from the declared reference to the package, such as A > B > Y.
- Resolution says how the graph was found:
  - LockFile: the packages.lock.json next to the project, read as is; it holds the exact resolution of each framework.
  - Computed: otherwise, from the .nuspec of the packages, read from the same sources as the usage (--feed, then
    nuget.org), following the NuGet rules without restore. A declared reference always wins; any other package takes,
    among the ranges all the packages of the graph ask for it, the one with the highest minimum, whatever their depth
    (NuGet's cousin dependencies); each range gives the lowest published version satisfying it; the dependency group
    nearest to the framework applies, an empty group meaning no dependency. With CentralPackageTransitivePinningEnabled
    (the project's value wins over those of Directory.Packages.props and Directory.Build.props), the central versions of
    Directory.Packages.props apply to transitive packages too. Via is the shortest path to the package.
- Usage, Source and Feed follow the rules of [Package usage](#-package-usage). Usage tells whether the code uses a
  package it does not declare: such a project breaks the day the package that brings it stops depending on it.

Only the .nuspec of each package is read, not the whole package, except for those matching the filters, whose usage
is analyzed; both are cached. A branch that cannot be followed (a version found in no source, an MSBuild property)
stops there; with --diagnostics, diagnostics.json gives it under Transitive, with the resolution of every project and
framework. It also reports a central version pinned below what the graph asks for, which NuGet refuses (NU1109), one pinned
above, which NuGet only warns of (NU1608), and a
target framework that could not be resolved: the graph then merges the dependency groups of every framework.

Accepted limits: a computed graph is right in the vast majority of cases, not always: MSBuild conditions, floating
versions and packages missing from every source can make it differ from a real restore; Resolution says which lines
are exact. The packages a project gets through a project reference are listed on the project that declares them,
unless a lock file is read. project.assets.json, a build output, is not read.

## 🧱 Target frameworks

`depinv frameworks` (alias `depinv fwk`) lists the target framework of every .NET project found, with no filter:

~~~powershell
depinv frameworks --provider azure --url "https://dev.azure.com/Organization/Project"
~~~

It takes the same options as `depinv packages` except the filters (`--pattern`, `--regex`, `--case-sensitive`).
The framework-report folder (configurable with --output) contains:

- frameworks.csv: one line per project file and target framework: Scope, Repository, Branch, Commit, ProjectFile,
  ProjectType (see [Project types](#project-types)), Framework, FrameworkSource and Url;
- repositories.csv: coverage of every repository found, as for `depinv packages`, with the number of Projects found
  instead of References;
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
project feed, or gives its URL, https://dev.azure.com/organization[/project]/_packaging/feed; a feed of another
organization is read with that organization's credentials. GitLab package registries are not supported yet.

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
- packages.csv: one line per package version with everything else known about it: whether it could be read (Status:
  OK, NotFound or Failed, and Details), where its frameworks were read (FrameworksFrom: Nuspec or PackageFolders),
  its publication in the feed (Published, Listed, Views) and all metadata fields from its .nuspec file (Id, Title, Authors,
  Owners, Description, Summary, ReleaseNotes, Copyright, Language, Tags, ProjectUrl, IconUrl, Icon, Readme, License,
  LicenseType, LicenseUrl, RequireLicenseAcceptance, DevelopmentDependency, Serviceable, MinClientVersion,
  PackageTypes, RepositoryType, RepositoryUrl, RepositoryBranch, RepositoryCommit);
- report.json: full JSON report, including the applied filters.

Package and Version are written as requested, so these files join dependencies.csv on these two columns.

The frameworks come from the dependency groups of each version's .nuspec. A package without dependency groups names
no framework there: its lib/ and ref/ folders are read instead. Frameworks are written as short monikers, such as
netstandard2.0 or net8.0, like in frameworks.csv. A dependency without framework applies to all of them.

--parallelism (1 to 8) reads several versions at once; --diagnostics writes every dependency read, unfiltered, to
diagnostics.json.

## 🔎 How it works

### Branch selection

A single branch is scanned per repository: its default branch, as set in Azure DevOps or GitLab, unless --branch or --branch-file gives the branches to look for. `depinv packages` and `depinv frameworks` take both options.

--branch, repeatable, lists the branches in priority order; the first one that exists in the repository is scanned:

~~~powershell
depinv packages --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "Contoso.*" --branch release --branch master --branch "@default"
~~~

- a name, compared case-insensitively;
- a glob, such as `release/*`: when several branches match, the highest in natural order is taken (release/1.10 before release/1.9);
- `@default`: the repository's default branch. Last in the list, it is the fallback for repositories that have none of the other branches.

--branch-file reads the list from a file, one entry per line; blank lines and lines starting with # are ignored. The lines before any section form the list of every repository; a `[repository]` line, a name or a glob, opens a section whose list replaces it for the repositories whose name matches. The first matching section applies; lists are not merged. A repository that matches no section, in a file without a list before its sections, is scanned on its default branch.

~~~text
# Every repository
release
master
@default

# Libraries
[core-*]
master
~~~

The file is only read when --branch-file names it. --branch and --branch-file cannot be combined. The scan header prints the branches looked for and where they come from.

A repository that has none of the branches of its list, and no `@default` in it, is not scanned: repositories.csv reports it with the NoBranch status and the branches looked for. Without either option, a repository without a default branch (an empty one) is reported NoBranch too. There is no fallback to another branch when no dependency is found.

#### Several branches

--branch-mode all scans every branch of the list that exists, instead of the first one: in declaration order, then in natural order within a glob, each branch once. It needs --branch or --branch-file; `--branch "*"` scans every branch.

~~~powershell
depinv packages --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "Contoso.*" --branch release --branch develop --branch-mode all
~~~

Each branch gets its own lines, with no deduplication: a reference declared on release and on develop is reported twice, told apart by the Branch and Commit columns. repositories.csv then has one line per repository and branch, and report.json and diagnostics.json one entry per repository and branch. Counting references across branches counts each one once per branch: filter on one branch to count them.

The work is shared between branches, the reports are not: branches on the same commit are scanned once, and a file whose content was already read on another branch of the repository is not downloaded again.

Both providers read files from each repository's selected commit without cloning it.

### Project types

Every project file gets a ProjectType, written in dependencies.csv, transitive-dependencies.csv and frameworks.csv, so that applications can be told from libraries, tests and samples. The first rule that applies decides:

| ProjectType | Rule |
|---|---|
| Test | IsTestProject is true, the project references Microsoft.NET.Test.Sdk, xunit, xunit.core, NUnit, nunit.framework, MSTest.TestFramework or Microsoft.VisualStudio.QualityTools.UnitTestFramework, or its legacy ProjectTypeGuids is a test project's |
| Sample | a folder of its path is samples, sample, examples, example or demo |
| Application | OutputType is Exe or WinExe, one of its SDKs is Microsoft.NET.Sdk.Web, Microsoft.NET.Sdk.Worker or Microsoft.NET.Sdk.BlazorWebAssembly, AzureFunctionsVersion is set, or its legacy ProjectTypeGuids is a web application's |
| Library | any other readable project |
| Unknown | the project file could not be read |

OutputType, IsTestProject, AzureFunctionsVersion and the test framework references come from the project file, then from the nearest Directory.Build.props, the one MSBuild imports; a property of the project wins, even declared empty, while a test framework referenced by that Directory.Build.props makes a test whatever IsTestProject says. A packages.config takes the type of the project file next to it. A reference declared in a file shared by several projects, such as Directory.Build.props, has no ProjectType. These rules are heuristics: with --diagnostics, diagnostics.json gives under Projects the rule that classified every project file.

### Activity

repositories.csv gives, in its CommitDate column, the committer timestamp recorded in the commit currently pointed to by the scanned branch, in UTC (2026-10-28T09:42:56Z). It is not the time that commit was merged into or became the tip of that branch, and it does not prove that a repository is active or abandoned; it can help identify repositories whose latest commits are old. The column is empty for a repository that was not scanned (Disabled, NoBranch, or a failure before its branch was read). Reading the date takes one more GET request per scanned commit; when it fails, the date stays empty and Details says why, without changing the status.

### NuGet extraction

Files: *.csproj, *.fsproj, *.vbproj, *.props, *.targets and packages.config. The bin, obj, node_modules and .git folders are ignored.

PackageReference, GlobalPackageReference and package are direct references; PackageVersion is a central version declaration, not a proof of direct consumption.

The scanner can match a version from the nearest Directory.Packages.props, without fully evaluating MSBuild. It does not run a NuGet restore; transitive dependencies are only resolved with --transitive.

### Parallelism

By default the scan is sequential: one repository after the other, one file after the other. --parallelism (1 to 8) allows several simultaneous requests; --parallelism-mode is then required and specifies whether files or repositories are processed in parallel:

- files: one repository at a time, several of its files read at once; suited to large repositories and to --repository;
- repositories: several repositories at once, each reading its files one after the other; suited to groups of many small repositories.

Only one level runs in parallel, so --parallelism is also the maximum number of simultaneous HTTP requests. Reports are identical, in the same order, whatever the parallelism and the mode. More parallelism raises the risk of HTTP 429; such responses are retried (see Transient errors), but a moderate value (2 to 4) reduces the risk of hitting the providers' rate limits.

~~~powershell
depinv packages --provider gitlab --url "https://gitlab.example.com/red/web" --pattern "Contoso.*" --parallelism 4 --parallelism-mode repositories
~~~

### Transient errors

A request to Azure DevOps, Azure Artifacts, GitLab or nuget.org is retried, up to 5 attempts in total, when it fails transiently: HTTP 429 (too many requests), 502, 503 or 504 (service unavailable), network error, or response timeout. The wait follows the Retry-After header when present, otherwise an exponential backoff with a random part; each retry delay is capped at 60 seconds and announced in the console. Other errors (400, 401, 403, 404...) are not retried. When the attempts are exhausted, the error is reported as before in repositories.csv.

The 120 s timeout applies to each attempt, including the time needed to read the full response, but excluding delays between attempts. A timeout on a file makes the repository partial without stopping the scan; only Ctrl+C stops it.

### Diagnosing a repository

~~~powershell
depinv packages --provider azure --url "https://dev.azure.com/Organization/Project" --pattern "Contoso.*" --repository api-order-bo --diagnostics
~~~

The diagnostics.json file lists every NuGet reference extracted, even outside the filters, the available branches, the selected one, the commits and their dates and the files examined, and in Usages how the usage of every reference was decided: the projects and source files analyzed, where the package was read from (Cache, GlobalPackagesFolder or the source), and the reason of an Undetermined or BuildOnly usage. With `depinv frameworks`, it lists the frameworks declared by every file examined instead. diagnostics.json is a troubleshooting aid: unlike the other reports, its content may change in any version, so do not build on it.

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
