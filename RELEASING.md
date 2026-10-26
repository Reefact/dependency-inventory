# Releasing

The tool is published on nuget.org as `DependencyInventory` (command: `depinv`).

## One-time setup

1. On nuget.org, create a **trusted publishing policy**: owner `Reefact`, repository `dependency-inventory`, workflow `release.yml`.
2. In the GitHub repository settings, add the **variable** `NUGET_USER` (Settings → Secrets and variables → Actions → Variables) with the nuget.org username (profile name, not the email). It is a variable, not a secret: it identifies the account and is not a credential.
3. Check the `DependencyInventory` package id is free on nuget.org. If nuget.org does not accept a first push of a not-yet-existing package through a policy, publish the first version once with an API key, then let the workflow publish every later version.

## Rehearsal

Run the **release** workflow manually (Actions → release → Run workflow) with a throwaway version such as `0.0.0-dry.1` and *Dry run* left ticked. It builds, tests, packs, attests and logs in to nuget.org through OIDC, but pushes nothing and creates no GitHub Release.

## Release

Before tagging, turn the `## [Unreleased]` heading of [CHANGELOG.md](CHANGELOG.md) into `## [<version>] - <date>`, add a fresh empty `## [Unreleased]` above it, and merge that change to `main`.

Then tag a commit that is on `main` and push the tag:

~~~bash
git tag v1.0.0-preview.1
git push origin v1.0.0-preview.1
~~~

The tag (without the `v`) becomes the package version; a label such as `v1.0.0-preview.1` makes a pre-release. The workflow refuses a tag that is not on `main`, publishes the package on nuget.org and attaches the attested `.nupkg` to a GitHub Release. nuget.org versions are immutable: a published version cannot be replaced.
