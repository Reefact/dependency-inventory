# Contributing to DependencyInventory

## Building and testing

* Target framework: **.NET 10** (LTS).
* Build: `dotnet build DependencyInventory.sln`
* Test: `dotnet test DependencyInventory.sln`

From a clone of the repository, run the tool without installing it by replacing `depinv` in the README examples with `dotnet run --project ./src/DependencyInventory --`.

See [`CLAUDE.md`](CLAUDE.md) for the project layout and the rules that apply to every change: the reports are a contract, the tool is read-only, secrets are never logged, every behavior change comes with a test, warnings are errors, and everything is written in English.

## Commit messages

Commits follow [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/) and are written in English: `<type>[(scope)][!]: <description>`, with a type among feat, fix, build, chore, ci, docs, perf, refactor, revert, style, test, a lowercase imperative description, no trailing period, and a header of 72 characters at most. A breaking change is signalled by a `!` before the colon, by a `BREAKING CHANGE:` (or `BREAKING-CHANGE:`) footer, or both.

The commit-lint workflow checks every commit of a pull request. For the same check locally, enable the hook once per clone:

~~~powershell
git config core.hooksPath .githooks
~~~

## Pull requests

Fill in the [pull request template](.github/pull_request_template.md) and check only what you actually ran.

## Releasing

See [RELEASING.md](RELEASING.md).
