---
name: framework-support-chart
description: Builds a bar chart of how many direct references each version of a set of NuGet packages has across repositories, with the .NET target framework of every version and its support status (out of support in red stripes, support ending soon in orange stripes), as an HTML page and a PNG for wikis such as Confluence. Use it whenever someone wants to see, refresh or share which package versions are in use across their repositories and whether those versions target an obsolete or soon-unsupported .NET, even if they only say "regenerate the chart", "update the version graph" or "which of our libraries are on an old .NET".
---

# Framework support chart

The chart answers one question for a set of internal NuGet packages: which versions do the repositories
use, and does each of those versions target a .NET that is still supported? Each package gets a band, each
version a bar whose length is its number of direct references. A reference is one declaration in a project
or MSBuild file, as `dependencies.csv` lists them: it is not a count of projects, since a project file may
declare a package twice under conditions and a shared `.props` file declares it for many projects. Keep the
chart's wording on references. The label shows the version and its
target framework, e.g. `2.0.1 (net9.0)`. Bars of a version whose framework is out of support are striped
red; those whose support ends within a few months are striped orange; a legend gives the dates.

When `dependencies.csv` has the `Usage` column (depinv 1.0.0-preview.5 and later), each bar also tells how many
of its references are classified as used: those whose package's types the code references, and the `BuildOnly`
ones (analyzers, build assets), which act on the build without any type in the code. The solid start of the bar
is classified as used, the washed-out rest is declared but not used, a hatched grey part is undetermined, and the
text after the bar reads `21 · 5 used`, or `not used` when none is. Without the column, the chart shows
references only, as before.

It reads two reports of `depinv` (this repository's tool) and needs nothing else, except Microsoft's
public release metadata for the support dates.

## 1. Get the two reports

- `dependencies.csv`: who uses which version. It comes from `depinv packages`, filtered on the packages to
  chart. To classify the usage of internal packages, pass the feed that publishes them: without it, they cannot
  be read and their usage is `Undetermined`:
  ```
  depinv packages --provider azure --url "https://dev.azure.com/<organization>/<project>" --pattern "<glob>" --feed <feed>
  ```
- `package-frameworks.csv`: the frameworks each published version ships. It comes from `depinv feed`, which
  reads the package feed; `--versions-from` limits it to the versions in use:
  ```
  depinv feed --provider azure --url "https://dev.azure.com/<organization>/<project>" --feed <feed> --versions-from dependency-report/dependencies.csv
  ```

A `dependencies.csv` written with `depinv packages --branch-mode all` lists every scanned branch of a repository,
each with its own lines: the chart would count those references once per branch. `build_chart.py` warns when a
repository has several branches; chart one of them with `--branch` (a glob, repeatable).

The user usually runs these commands (they hold the credentials) and hands over the two files. If they
only have one of them, explain which command produces the other; do not build the chart from a guess.

## 2. Ask for the presentation

Ask the user, in one message, before building anything:

- the **title** and, optionally, the **small line above it** (for instance a team or a scope). Never reuse
  a title from an earlier chart or invent one from the package names: the chart may be shared, and its
  wording is the user's;
- the **language** of the chart texts: English or French (`--lang en|fr`);
- which **packages** to show, as globs on package ids (`--include`, repeatable); all by default;
- when the report lists several branches per repository, which **branch** to chart (`--branch`);
- whether to leave out the inactive repositories (`--active-since YYYY-MM-DD`, with `--repositories` giving the
  `repositories.csv` of the same scan, depinv 1.0.0-preview.8 and later): only the repositories whose scanned
  commit dates from that day or later are counted; all by default;
- which **kinds of projects** to count (`--project-type`, repeatable: Application, Library, Test, Sample), when
  `dependencies.csv` has the ProjectType column (depinv 1.0.0-preview.8 and later); all by default. Charting
  `Application` only answers "which applications use which version", without libraries and tests;
- the **date** of the support status (`--as-of`, today by default) and how many **months** ahead counts
  as "ending soon" (`--warn-months`, 6 by default).

Propose the defaults so that the user can answer "ok".

## 3. Build it

```bash
python3 <skill-dir>/scripts/build_chart.py \
  --dependencies <dependencies.csv> --frameworks <package-frameworks.csv> \
  --title "<title>" [--eyebrow "<line above>"] [--lang fr] [--include "<glob>" ...] [--branch "<glob>" ...] [--project-type <type> ...] \
  [--repositories <repositories.csv> --active-since YYYY-MM-DD] \
  [--as-of YYYY-MM-DD] [--warn-months 6] \
  --output <dir>/chart.html
node <skill-dir>/scripts/render_png.js <dir>/chart.html <dir>/chart.png
```

Write the outputs in the scratchpad, or wherever the user asks; they are not part of the repository.

`build_chart.py` prints the packages, the reference count, one line per framework group and, with the usage,
how many references are used, not used and undetermined. Many undetermined references usually mean the scan
ran without `--feed`. Its unit tests
run with `python3 -m unittest discover -s <skill-dir>/scripts`. Read it: the
total should match the number of references the user expects, and each framework should have the status
you expect from its dates. Then look once at the PNG for clipped labels or overlaps.

`render_png.js` needs Playwright for Node, which the cloud sessions provide. It removes the collapsible
data table, which an image cannot open, and renders at twice the size so that the image stays sharp.

## 4. Deliver it

- Publish the HTML page as an artifact: it keeps the tooltips and the data table. It is private until the
  user shares it.
- Send the PNG: it is the version to paste into Confluence or another wiki, which do not run the page's
  script.

Tell the user the date of the support status: the chart is right on that date, and orange bars turn red
when their support ends.

## How the statuses are decided

- **.NET 5 and later, .NET Core** (`net8.0`, `netcoreapp3.1`, `net8.0-windows`...): the end-of-support
  date of the channel comes from Microsoft's release metadata
  (`https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json`), read at each build.
  Without network access, pass `--eol-file` with a JSON object such as `{"8.0": "2026-11-10"}`.
- **.NET Standard** has no end of support of its own: a library targeting it runs on the .NET
  implementations that support that .NET Standard version. That is not all of them: .NET Framework stops at
  .NET Standard 2.0, so a `netstandard2.1` library does not run on it. Keep the chart's wording to that.
- **.NET Framework** (`net48`, `net462`...) is not in that metadata. The script holds its dates in
  `NET_FRAMEWORK_EOL`, checked in October 2026. When a chart contains .NET Framework versions, check
  Microsoft's lifecycle page for .NET Framework and update the table if a date has changed.
- A version that ships several frameworks takes the status of its best-supported one: consumers can use
  that one. Each framework is still colored in the label.
- A version missing from `package-frameworks.csv` (not read from the feed) shows `(?)` and counts in a
  "framework unknown" legend line, so a gap in the data is never shown as healthy. A framework that was read
  but that the script does not classify (`uap10.0`, `monoandroid`...) has its own "framework not recognized"
  line: the data is there, the classification is missing.
- References without a literal version (an MSBuild property, a range) cannot be placed on a bar; the
  footer counts them.

Versions are ordered as NuGet orders them, prereleases before their release; `2.0` and `2.0.0` are one
version. Packages are ordered by number of references; the seven most used get the categorical colors in a fixed order, the others grey.
