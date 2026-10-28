#!/usr/bin/env python3
"""Build the framework support chart: direct references per package version, with each version's .NET
target frameworks and their support status on a given date.

Inputs are two reports of depinv: dependencies.csv (depinv packages) has one line per reference declaration
(not per project: a project file may declare a package twice under conditions, a shared .props file declares
it for many projects), package-frameworks.csv (depinv feed) gives the frameworks each published version ships.
When dependencies.csv has the Usage column (depinv 1.0.0-preview.5 and later), each bar also shows how many of
its references are classified as used: the code references the package's types, or the package is BuildOnly.
Support dates of .NET (Core) come from Microsoft's release metadata; .NET Framework dates from the
table below. Output: a self-contained HTML page (and a PNG with scripts/render_png.js).
"""

import argparse
import calendar
import csv
import datetime as dt
import fnmatch
import io
import json
import re
import sys
import urllib.request
from collections import defaultdict
from pathlib import Path

RELEASES_INDEX_URLS = [
    "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json",
    "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json",
]

# .NET Framework is not in the release metadata. Microsoft lifecycle, checked in October 2026
# (https://learn.microsoft.com/lifecycle/products/microsoft-net-framework): 4.5.2, 4.6 and 4.6.1 ended on
# 2022-04-26; 4.5.1 and older 4.x on 2016-01-12; 4.6.2 ends on 2027-01-12; 3.5 SP1 on 2029-01-09; 4.7 and
# later have no end date and follow the support of the Windows version they run on.
NET_FRAMEWORK_EOL = {
    "net20": "2011-07-12", "net30": "2011-07-12", "net35": "2029-01-09",
    "net40": "2016-01-12", "net403": "2016-01-12", "net45": "2016-01-12", "net451": "2016-01-12",
    "net452": "2022-04-26", "net46": "2022-04-26", "net461": "2022-04-26",
    "net462": "2027-01-12",
    "net47": None, "net471": None, "net472": None, "net48": None, "net481": None,
}

# Seven categorical hues for the packages with the most references, in this order; the others are grey.
SERIES_COLORS = ["--s0", "--s1", "--s2", "--s3", "--s4", "--s5", "--s6"]

TEXT = {
    "en": {
        "axis": "Number of direct references to this version",
        "tableSummary": "Show the data as a table",
        "tableColumns": ["Package", "Version", "Target frameworks", "Support", "References"],
        "colon": ": ",
        "references": lambda n: f"{n} reference" + ("" if n == 1 else "s"),
        "unsupported_since": lambda d: f"out of support since {d}",
        "ends_on": lambda d: f"end of support on {d}",
        "supported_until": lambda d: f"supported until {d}",
        "follows_windows": "supported with the Windows version it runs on",
        "netstandard": "no end of support, compatible with the .NET implementations that support this .NET Standard version",
        "unrecognized": "framework not recognized: support status unknown",
        "missing": "version missing from the feed report",
        "legend_supported": "Supported",
        "legend_unrecognized": "Framework not recognized",
        "legend_missing": "Framework unknown",
        "footer": lambda packages, refs: f"{packages} packages · {refs} direct references · "
                                         "Versions in semantic order · Central version declarations excluded",
        "status_date": lambda d, months: f"Support status on {d}; orange when support ends within {months} months.",
        "unresolved": lambda n: f"{n} reference{'' if n == 1 else 's'} without a literal version not shown.",
        "used": lambda n: f"{n} used",
        "not_used": "not used",
        "undetermined": lambda n: f"{n} undetermined",
        "usage_line": lambda used, total: f"{used} of {total} reference{'' if total == 1 else 's'} "
                                          f"{'is' if total == 1 else 'are'} classified as used",
        "legend_used": "Used",
        "legend_used_text": "solid part of the bar: the code references the package's types, or the package acts on the "
                            "build only (analyzer, build assets)",
        "legend_unused": "Declared but not used",
        "legend_unused_text": "faded part: the code references none of its types, the reference can likely be removed",
        "legend_undetermined": "Usage undetermined",
        "legend_undetermined_text": "hatched grey part: package not found, VB or F# project, code under #if...",
        "usage_note": "A reference declared in a shared file (Directory.Build.props...) counts as used as soon as one "
                      "of the projects it covers uses the package.",
        "usageColumns": ["Used", "Not used", "Undetermined"],
        "breakdown": lambda used, unused, undetermined: f"{used} used · {unused} not used · {undetermined} undetermined",
    },
    "fr": {
        "axis": "Nombre de références directes à cette version",
        "tableSummary": "Voir les données sous forme de tableau",
        "tableColumns": ["Package", "Version", "Frameworks cibles", "Prise en charge", "Références"],
        "colon": " : ",
        "references": lambda n: f"{n} référence" + ("" if n == 1 else "s"),
        "unsupported_since": lambda d: f"n'est plus pris en charge depuis le {d}",
        "ends_on": lambda d: f"fin de prise en charge le {d}",
        "supported_until": lambda d: f"pris en charge jusqu'au {d}",
        "follows_windows": "pris en charge selon le cycle de vie de Windows",
        "netstandard": "pas de fin de prise en charge, compatible avec les implémentations de .NET qui prennent en charge "
                       "cette version de .NET Standard",
        "unrecognized": "framework non reconnu : prise en charge inconnue",
        "missing": "version absente du rapport du flux",
        "legend_supported": "Pris en charge",
        "legend_unrecognized": "Framework non reconnu",
        "legend_missing": "Framework inconnu",
        "footer": lambda packages, refs: f"{packages} bibliothèques · {refs} références directes · "
                                         "Versions triées par ordre sémantique · "
                                         "Déclarations de versions centralisées exclues",
        "status_date": lambda d, months: f"Statut de prise en charge au {d} ; orange quand la prise en charge "
                                         f"s'arrête dans moins de {months} mois.",
        "unresolved": lambda n: f"{n} référence{'' if n == 1 else 's'} sans version littérale non affichée{'' if n == 1 else 's'}.",
        "used": lambda n: f"{n} utilisée{'' if n == 1 else 's'}",
        "not_used": "non utilisée",
        "undetermined": lambda n: f"{n} indéterminée{'' if n == 1 else 's'}",
        "usage_line": lambda used, total: f"{used} référence{'' if used <= 1 else 's'} "
                                          f"considérée{'' if used <= 1 else 's'} comme utilisée{'' if used <= 1 else 's'} "
                                          f"sur {total}",
        "legend_used": "Utilisée",
        "legend_used_text": "partie pleine de la barre : le code utilise les types du package, ou le package n'agit que "
                            "sur la compilation (analyseur, fichiers de build)",
        "legend_unused": "Déclarée mais inutilisée",
        "legend_unused_text": "partie pâle : le code n'utilise aucun de ses types, la référence peut sans doute être retirée",
        "legend_undetermined": "Usage indéterminé",
        "legend_undetermined_text": "partie grise hachurée : package introuvable, projet VB ou F#, code sous #if…",
        "usage_note": "Une référence déclarée dans un fichier partagé (Directory.Build.props…) compte comme utilisée dès "
                      "qu'un des projets qu'elle couvre utilise le package.",
        "usageColumns": ["Utilisées", "Inutilisées", "Indéterminées"],
        "breakdown": lambda used, unused, undetermined:
            f"{used} utilisée{'' if used <= 1 else 's'} · {unused} inutilisée{'' if unused <= 1 else 's'} · "
            f"{undetermined} indéterminée{'' if undetermined <= 1 else 's'}",
    },
}

# Month names are spelled out: strftime("%B") follows the machine's locale, whatever the chart's language.
MONTHS_EN = ["January", "February", "March", "April", "May", "June", "July", "August", "September",
             "October", "November", "December"]
MONTHS_FR = ["janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre",
             "octobre", "novembre", "décembre"]


def format_date(value: dt.date, lang: str) -> str:
    if lang == "fr":
        return f"{value.day} {MONTHS_FR[value.month - 1]} {value.year}"
    return f"{MONTHS_EN[value.month - 1]} {value.day}, {value.year}"


def read_csv(path: Path) -> list[dict]:
    """depinv CSV: UTF-8 with BOM, comma-separated by default (semicolons before depinv 1.0.0, or a semicolon or a tab
    with --csv-delimiter), quoted values may hold the separator and line breaks."""
    return read_csv_with_columns(path)[1]


def csv_delimiter(text: str) -> str:
    """The separator of the header line: column names hold none, so it is the most frequent one."""
    header = text.splitlines()[0] if text else ""
    return max(",;\t", key=header.count)


def read_csv_with_columns(path: Path) -> tuple[list[str], list[dict]]:
    """The header and the rows of a depinv CSV; the header is known even without any row."""
    text = path.read_text(encoding="utf-8-sig")
    reader = csv.DictReader(io.StringIO(text), delimiter=csv_delimiter(text))
    rows = list(reader)
    return list(reader.fieldnames or []), rows


def version_key(version: str):
    """NuGet/SemVer order: numeric parts, then a release after its prereleases, prerelease labels
    compared part by part (numbers numerically, below text)."""
    core, _, prerelease = version.split("+")[0].partition("-")
    numbers = [int(p) if p.isdigit() else 0 for p in core.split(".")]
    numbers += [0] * (4 - len(numbers))
    labels = [(0, int(p), "") if p.isdigit() else (1, 0, p.lower()) for p in prerelease.split(".")] if prerelease else []
    return (numbers, 0 if prerelease else 1, labels)


def normalize_version(version: str) -> str:
    """NuGet normalized form, used to join the two reports: 2.0 is 2.0.0, 1.0.0.0 is 1.0.0, no metadata."""
    core, dash, prerelease = version.strip().split("+")[0].partition("-")
    parts = core.split(".")
    while len(parts) < 3:
        parts.append("0")
    if len(parts) == 4 and parts[3] == "0":
        parts = parts[:3]
    return (".".join(str(int(p)) if p.isdigit() else p for p in parts) + dash + prerelease).lower()


def fetch_dotnet_eol() -> dict[str, dt.date | None]:
    """End-of-support date of each .NET (Core) channel, e.g. "8.0", from Microsoft's release metadata."""
    errors = []
    for url in RELEASES_INDEX_URLS:
        try:
            with urllib.request.urlopen(url, timeout=30) as response:
                index = json.load(response)
            return {r["channel-version"]: dt.date.fromisoformat(r["eol-date"]) if r.get("eol-date") else None
                    for r in index["releases-index"]}
        except Exception as error:  # noqa: BLE001 - the next source is tried, and all errors are reported
            errors.append(f"{url}: {error}")
    sys.exit("Cannot read the .NET release metadata:\n  " + "\n  ".join(errors) +
             "\nPass --eol-file with a JSON object of channel -> end-of-support date to work offline.")


def framework_status(framework: str, eol: dict, today: dt.date, warn_until: dt.date, text: dict):
    """(status, note, end of support) of a target framework moniker; status is unsupported, ending,
    supported or unknown."""
    moniker = framework.lower().split("-")[0]
    if moniker.startswith("netstandard"):
        return "supported", text["netstandard"], None
    match = re.fullmatch(r"net(?:coreapp)?(\d+)\.(\d+)", moniker)
    if match:
        channel = f"{match.group(1)}.{match.group(2)}"
        if channel not in eol:
            return "unknown", text["unrecognized"], None
        end = eol[channel]
    elif moniker in NET_FRAMEWORK_EOL:
        end = dt.date.fromisoformat(NET_FRAMEWORK_EOL[moniker]) if NET_FRAMEWORK_EOL[moniker] else None
        if end is None:
            return "supported", text["follows_windows"], None
    else:
        return "unknown", text["unrecognized"], None
    if end is None:
        return "supported", "", None
    when = format_date(end, text["lang"])
    if end <= today:
        return "unsupported", text["unsupported_since"](when), end
    if end <= warn_until:
        return "ending", text["ends_on"](when), end
    return "supported", text["supported_until"](when), end


SEVERITY = {"unsupported": 0, "ending": 1, "unknown": 2, "supported": 3}


def usage_kind(value: str | None) -> str:
    """The part of the bar a reference falls in. BuildOnly is used: such a package acts on the build without being
    referenced by the code, it cannot be removed. UsedTransitively is used too: the code uses a package it brings."""
    return {"Used": "used", "BuildOnly": "used", "UsedTransitively": "used", "Unused": "unused"}.get((value or "").strip(), "undetermined")


def usage_fields(counts: dict[str, int], total: int, text: dict) -> dict:
    """The usage of one version's references: its parts, and the text after the bar, e.g. "21 · 5 used"."""
    parts = [text["used"](counts["used"]) if counts["used"] or counts["undetermined"] else text["not_used"]]
    if counts["undetermined"]:
        parts.append(text["undetermined"](counts["undetermined"]))
    return {
        "used": counts["used"],
        "unused": counts["unused"],
        "undetermined": counts["undetermined"],
        "valueLabel": " · ".join([str(total), *parts]),
        "usageLabel": " · ".join(parts),
        "usageLine": text["usage_line"](counts["used"], total),
        "breakdown": text["breakdown"](counts["used"], counts["unused"], counts["undetermined"]),
    }


def add_months(day: dt.date, months: int) -> dt.date:
    """The same day, months later; the last day of the month when that month is shorter."""
    month = day.month - 1 + months
    year, month = day.year + month // 12, month % 12 + 1
    return day.replace(year=year, month=month, day=min(day.day, calendar.monthrange(year, month)[1]))


def scope(row: dict) -> str:
    """The Azure DevOps project or GitLab group of a row: the Scope column, Project before depinv 1.0.0."""
    return row.get("Scope") or row.get("Project") or ""


def select_branches(rows: list[dict], branches: list[str], source: Path) -> list[dict]:
    """The rows of the branches matching one of the globs; all rows without any, with a warning when a repository
    has several branches, since its references would then be counted once per branch."""
    if branches:
        return [r for r in rows
                if any(fnmatch.fnmatch((r.get("Branch") or "").lower(), b.lower()) for b in branches)]
    by_repository: dict[str, set[str]] = defaultdict(set)
    for row in rows:
        by_repository[(scope(row), row.get("Repository") or "")].add(row.get("Branch") or "")
    several = sorted(repository for (_, repository), found in by_repository.items() if len(found) > 1)
    if several:
        shown = ", ".join(several[:3]) + (", ..." if len(several) > 3 else "")
        subject = "1 repository has" if len(several) == 1 else f"{len(several)} repositories have"
        print(f"warning: {subject} several branches in {source} ({shown}): their "
              "references are counted once per branch. Pass --branch to keep one.", file=sys.stderr)
    return rows


def select_project_types(rows: list[dict], columns: list[str], types: list[str], source: Path) -> list[dict]:
    """The rows of projects of one of the types; all rows without any. A reference declared in a shared file has no
    type and is left out by the filter. A report without the ProjectType column is refused, rows or not."""
    if not types:
        return rows
    if "ProjectType" not in columns:
        sys.exit(f"{source} has no ProjectType column: --project-type needs a report of depinv 1.0.0-preview.8 "
                 "or later.")
    wanted = {t.lower() for t in types}
    return [r for r in rows if (r.get("ProjectType") or "").lower() in wanted]


def select_active(rows: list[dict], repositories: Path | None, since: dt.date | None) -> list[dict]:
    """The rows of the repositories and branches whose scanned commit dates from `since` or later, as
    repositories.csv gives it; all rows without `since`."""
    if since is None:
        return rows
    if repositories is None:
        sys.exit("--active-since needs --repositories, the repositories.csv of the same scan.")
    columns, coverage = read_csv_with_columns(repositories)
    if "CommitDate" not in columns:
        sys.exit(f"{repositories} has no CommitDate column: --active-since needs a report of depinv 1.0.0-preview.8 "
                 "or later.")
    active = {(scope(r), r.get("Repository") or "", r.get("Branch") or "") for r in coverage
              if r.get("CommitDate") and dt.date.fromisoformat(r["CommitDate"][:10]) >= since}
    return [r for r in rows
            if (scope(r), r.get("Repository") or "", r.get("Branch") or "") in active]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dependencies", type=Path, required=True, help="dependencies.csv of depinv packages")
    parser.add_argument("--frameworks", type=Path, required=True, help="package-frameworks.csv of depinv feed")
    parser.add_argument("--title", required=True, help="chart title")
    parser.add_argument("--eyebrow", default="", help="small line above the title")
    parser.add_argument("--include", action="append", default=[],
                        help="glob on package ids to keep (repeatable, case-insensitive); all by default")
    parser.add_argument("--branch", action="append", default=[],
                        help="only the references of the branches matching this glob (repeatable): a report of "
                             "depinv packages --branch-mode all lists every branch")
    parser.add_argument("--project-type", action="append", default=[],
                        help="only the references of projects of this type (repeatable): Application, Library, "
                             "Test, Sample or Unknown, from the ProjectType column of depinv 1.0.0-preview.8 and later")
    parser.add_argument("--repositories", type=Path,
                        help="repositories.csv of depinv packages, which gives the date of every scanned commit")
    parser.add_argument("--active-since", type=dt.date.fromisoformat,
                        help="only the repositories whose scanned branch tip has a committer timestamp from this "
                             "day, YYYY-MM-DD; needs --repositories")
    parser.add_argument("--lang", choices=sorted(TEXT), default="en", help="language of the chart texts")
    parser.add_argument("--as-of", type=dt.date.fromisoformat, default=dt.date.today(),
                        help="date of the support status, YYYY-MM-DD; today by default")
    parser.add_argument("--warn-months", type=int, default=6,
                        help="support ending within this many months is shown in orange")
    parser.add_argument("--eol-file", type=Path, help="JSON object channel -> end-of-support date, offline")
    parser.add_argument("--output", type=Path, required=True, help="HTML file to write")
    args = parser.parse_args()

    text = dict(TEXT[args.lang], lang=args.lang)
    eol = ({k: dt.date.fromisoformat(v) if v else None for k, v in json.loads(args.eol_file.read_text()).items()}
           if args.eol_file else fetch_dotnet_eol())
    warn_until = add_months(args.as_of, args.warn_months)

    def included(package: str) -> bool:
        return not args.include or any(fnmatch.fnmatch(package.lower(), p.lower()) for p in args.include)

    # One line of dependencies.csv is one reference declaration: that is what the chart counts.
    counts: dict[tuple[str, str], int] = defaultdict(int)
    usage: dict[tuple[str, str], dict[str, int]] = defaultdict(lambda: {"used": 0, "unused": 0, "undetermined": 0})
    names: dict[str, str] = {}
    unresolved = 0
    dependency_columns, dependency_rows = read_csv_with_columns(args.dependencies)
    dependency_rows = select_project_types(dependency_rows, dependency_columns, args.project_type, args.dependencies)
    dependency_rows = select_active(dependency_rows, args.repositories, args.active_since)
    dependency_rows = select_branches(dependency_rows, args.branch, args.dependencies)
    # Reports of depinv before 1.0.0-preview.5 have no Usage column: the chart then shows references only.
    has_usage = "Usage" in dependency_columns
    for row in dependency_rows:
        if row.get("Kind", "Reference") != "Reference" or not included(row["Package"]):
            continue
        version = (row.get("Version") or "").strip()
        if not re.fullmatch(r"\d+(\.\d+){0,3}(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?", version):
            unresolved += 1
            continue
        names.setdefault(row["Package"].lower(), row["Package"])
        # 2.0 and 2.0.0 are the same version: counted together, shown in normalized form.
        counts[(row["Package"].lower(), normalize_version(version))] += 1
        usage[(row["Package"].lower(), normalize_version(version))][usage_kind(row.get("Usage"))] += 1

    frameworks: dict[tuple[str, str], list[str]] = defaultdict(list)
    for row in read_csv(args.frameworks):
        key = (row["Package"].lower(), normalize_version(row["Version"]))
        if row["Framework"] and row["Framework"] not in frameworks[key]:
            frameworks[key].append(row["Framework"])

    by_package: dict[str, list] = defaultdict(list)
    for (package, version), count in counts.items():
        by_package[package].append((version, count))

    packages = []
    legend_counts: dict[tuple[str, str], int] = defaultdict(int)
    ends: dict[str, dt.date | None] = {}
    for package, versions in sorted(by_package.items(), key=lambda p: (-sum(c for _, c in p[1]), p[0])):
        rows = []
        for version, count in sorted(versions, key=lambda v: version_key(v[0])):
            shipped = frameworks.get((package, version)) or ["?"]
            statuses = [framework_status(f, eol, args.as_of, warn_until, text) if f != "?"
                        else ("unknown", text["missing"], None) for f in shipped]
            # A package version is as healthy as its best-supported framework: a consumer can use that one.
            status = max((s for s, _, _ in statuses), key=SEVERITY.get)
            for f, (s, _, end) in zip(shipped, statuses):
                legend_counts[(s, f)] += count
                ends[f] = end
            rows.append({
                "version": version,
                "count": count,
                "countLabel": text["references"](count),
                **(usage_fields(usage[(package, version)], count, text) if has_usage else {}),
                "frameworks": [{"name": f, "status": s} for f, (s, _, _) in zip(shipped, statuses)],
                "status": status if status in ("unsupported", "ending") else "",
                "notes": sorted({n for _, n, _ in statuses if n}),
            })
        total = sum(c for _, c in versions)
        label = text["references"](total)
        if has_usage:
            label += " · " + text["used"](sum(usage[(package, v)]["used"] for v, _ in versions))
        packages.append({"name": names[package], "count": total, "countLabel": label, "versions": rows})
    for index, package in enumerate(packages):
        package["color"] = SERIES_COLORS[index] if index < len(SERIES_COLORS) else "--s-other"

    legend = []
    for status in ("unsupported", "ending"):
        flagged = sorted((ends[f], f, refs) for (s, f), refs in legend_counts.items() if s == status)
        for _, framework, refs in flagged:
            note = framework_status(framework, eol, args.as_of, warn_until, text)[1]
            legend.append({"status": status, "title": framework, "text": f"{note} — {text['references'](refs)}"})
    supported = sorted(f for (s, f) in legend_counts if s == "supported")
    if supported:
        refs = sum(n for (s, _), n in legend_counts.items() if s == "supported")
        legend.append({"status": "", "title": text["legend_supported"],
                       "text": f"{', '.join(supported)} — {text['references'](refs)}"})
    # A framework read from the feed but not classified, and a version missing from the feed report, are
    # different problems: the first needs the classification above, the second another feed read.
    unrecognized = sorted(f for (s, f) in legend_counts if s == "unknown" and f != "?")
    if unrecognized:
        refs = sum(n for (s, f), n in legend_counts.items() if s == "unknown" and f != "?")
        legend.append({"status": "", "title": text["legend_unrecognized"],
                       "text": f"{', '.join(unrecognized)} — {text['references'](refs)}"})
    missing_refs = legend_counts.get(("unknown", "?"), 0)
    if missing_refs:
        legend.append({"status": "", "title": text["legend_missing"],
                       "text": f"{text['missing']} — {text['references'](missing_refs)}"})

    usage_legend = []
    if has_usage:
        usage_legend = [{"usage": "used", "title": text["legend_used"], "text": text["legend_used_text"]},
                        {"usage": "unused", "title": text["legend_unused"], "text": text["legend_unused_text"]}]
        if any(u["undetermined"] for u in usage.values()):
            usage_legend.append({"usage": "undetermined", "title": text["legend_undetermined"],
                                 "text": text["legend_undetermined_text"]})

    references = sum(counts.values())
    footer = [text["footer"](len(packages), references),
              text["status_date"](format_date(args.as_of, args.lang), args.warn_months)]
    if has_usage:
        footer.append(text["usage_note"])
    if unresolved:
        footer.append(text["unresolved"](unresolved))

    data = {
        "eyebrow": args.eyebrow,
        "title": args.title,
        "text": {k: v for k, v in text.items() if isinstance(v, (str, list))},
        "packages": packages,
        "legend": legend,
        "usage": has_usage,
        "usageLegend": usage_legend,
        "footer": footer,
    }
    template = (Path(__file__).resolve().parent.parent / "assets" / "chart.html").read_text(encoding="utf-8")
    page = (template
            .replace("__PAGE_TITLE__", args.title.replace("&", "&amp;").replace("<", "&lt;"))
            .replace("__CHART_DATA__", json.dumps(data, ensure_ascii=False).replace("</", "<\\/")))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(page, encoding="utf-8")

    print(f"{args.output}: {len(packages)} packages, {references} references, support status on {args.as_of}")
    for item in legend:
        print(f"  {item['status'] or 'other':11} {item['title']}: {item['text']}")
    if has_usage:
        totals = {kind: sum(u[kind] for u in usage.values()) for kind in ("used", "unused", "undetermined")}
        print(f"  usage       {totals['used']} used, {totals['unused']} not used, {totals['undetermined']} undetermined")
    else:
        print("  usage       no Usage column in dependencies.csv: run depinv 1.0.0-preview.5 or later to show it")
    if unresolved:
        print("  " + TEXT["en"]["unresolved"](unresolved))


if __name__ == "__main__":
    main()
