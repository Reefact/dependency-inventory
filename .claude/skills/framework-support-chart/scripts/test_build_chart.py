"""Unit tests of build_chart.py: python3 -m unittest discover -s .claude/skills/framework-support-chart/scripts"""

import contextlib
import datetime as dt
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import build_chart  # noqa: E402

AS_OF = dt.date(2026, 10, 27)
EOL = {"6.0": dt.date(2024, 11, 12), "8.0": dt.date(2026, 11, 10), "10.0": dt.date(2028, 11, 14)}


def status(framework: str, lang: str = "en"):
    text = dict(build_chart.TEXT[lang], lang=lang)
    return build_chart.framework_status(framework, EOL, AS_OF, build_chart.add_months(AS_OF, 6), text)


class FrameworkStatusTests(unittest.TestCase):
    def test_dotnet_channels_follow_their_end_of_support(self):
        self.assertEqual(status("net6.0")[0], "unsupported")
        self.assertEqual(status("net8.0")[0], "ending")
        self.assertEqual(status("net8.0-windows")[0], "ending")
        self.assertEqual(status("net10.0")[0], "supported")

    def test_net_framework_4_7_and_later_follow_windows(self):
        for framework in ("net47", "net471", "net472", "net48", "net481"):
            with self.subTest(framework):
                self.assertEqual(status(framework)[:2], ("supported", build_chart.TEXT["en"]["follows_windows"]))

    def test_net_framework_4_6_2_ends_on_january_12_2027(self):
        self.assertEqual(status("net462"), ("ending", "end of support on January 12, 2027", dt.date(2027, 1, 12)))
        self.assertEqual(status("net461")[0], "unsupported")

    def test_netstandard_has_no_end_of_support_but_does_not_claim_to_run_everywhere(self):
        # .NET Framework implements .NET Standard 2.0 only: netstandard2.1 does not run on it.
        for framework in ("netstandard2.0", "netstandard2.1"):
            with self.subTest(framework):
                state, note, end = status(framework)
                self.assertEqual((state, end), ("supported", None))
                self.assertIn("support this .NET Standard version", note)
                self.assertNotIn("every", note)

    def test_an_unrecognized_framework_is_not_reported_as_missing_from_the_feed(self):
        for framework in ("uap10.0", "monoandroid", "net99.0"):
            with self.subTest(framework):
                self.assertEqual(status(framework)[:2], ("unknown", build_chart.TEXT["en"]["unrecognized"]))
        self.assertNotEqual(build_chart.TEXT["en"]["unrecognized"], build_chart.TEXT["en"]["missing"])


class DateTests(unittest.TestCase):
    def test_add_months_keeps_the_day_or_takes_the_last_day_of_a_shorter_month(self):
        self.assertEqual(build_chart.add_months(dt.date(2026, 1, 31), 6), dt.date(2026, 7, 31))
        self.assertEqual(build_chart.add_months(dt.date(2026, 8, 31), 6), dt.date(2027, 2, 28))
        self.assertEqual(build_chart.add_months(dt.date(2027, 8, 31), 6), dt.date(2028, 2, 29))
        self.assertEqual(build_chart.add_months(dt.date(2026, 10, 27), 6), dt.date(2027, 4, 27))

    def test_dates_are_spelled_in_the_chart_language_whatever_the_locale(self):
        self.assertEqual(build_chart.format_date(dt.date(2026, 11, 10), "en"), "November 10, 2026")
        self.assertEqual(build_chart.format_date(dt.date(2026, 11, 10), "fr"), "10 novembre 2026")


class VersionTests(unittest.TestCase):
    def test_versions_are_normalized_as_nuget_does(self):
        self.assertEqual(build_chart.normalize_version("2.0"), "2.0.0")
        self.assertEqual(build_chart.normalize_version("1.0.0.0"), "1.0.0")
        self.assertEqual(build_chart.normalize_version("2.1.0-Beta.6+sha.1"), "2.1.0-beta.6")

    def test_versions_are_ordered_as_nuget_does(self):
        versions = ["2.0.0", "2.0.0-beta.12", "1.0.3-rc.1", "2.0.0-beta.6", "2.0.0-rc.2", "1.0.2"]
        self.assertEqual(sorted(versions, key=build_chart.version_key),
                         ["1.0.2", "1.0.3-rc.1", "2.0.0-beta.6", "2.0.0-beta.12", "2.0.0-rc.2", "2.0.0"])


class ChartTests(unittest.TestCase):
    def test_the_chart_counts_reference_declarations(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            (folder / "dependencies.csv").write_text(
                "Project;Repository;File;Package;Version;Kind\n"
                # Two conditional declarations in one project file are two references, not two projects.
                "p;r;/src/A.csproj;Contoso.Core;2.0;Reference\n"
                "p;r;/src/A.csproj;Contoso.Core;2.0.0;Reference\n"
                "p;r;/Directory.Packages.props;Contoso.Core;2.0.0;CentralVersion\n"
                "p;r;/src/B.csproj;Contoso.Core;$(CoreVersion);Reference\n", encoding="utf-8-sig")
            (folder / "package-frameworks.csv").write_text(
                "Package;Version;Framework\nContoso.Core;2.0.0;net6.0\n", encoding="utf-8-sig")
            (folder / "eol.json").write_text(json.dumps({"6.0": "2024-11-12"}))
            sys.argv = ["build_chart.py", "--dependencies", str(folder / "dependencies.csv"),
                        "--frameworks", str(folder / "package-frameworks.csv"), "--title", "Chart",
                        "--as-of", "2026-10-27", "--eol-file", str(folder / "eol.json"),
                        "--output", str(folder / "chart.html")]
            with contextlib.redirect_stdout(io.StringIO()):
                build_chart.main()
            page = (folder / "chart.html").read_text(encoding="utf-8")
            data = json.loads(page.split("const DATA = ", 1)[1].split(";\n", 1)[0])

        version = data["packages"][0]["versions"]
        self.assertEqual([(v["version"], v["count"], v["countLabel"]) for v in version], [("2.0.0", 2, "2 references")])
        self.assertEqual(version[0]["status"], "unsupported")
        self.assertEqual(data["text"]["axis"], "Number of direct references to this version")
        self.assertIn("1 reference without a literal version not shown.", data["footer"])


class UsageTests(unittest.TestCase):
    def test_the_tooltip_says_how_many_references_are_classified_as_used(self):
        text = dict(build_chart.TEXT["en"], lang="en")
        self.assertEqual(build_chart.usage_fields({"used": 2, "unused": 2, "undetermined": 0}, 4, text)["usageLine"],
                         "2 of 4 references are classified as used")
        self.assertEqual(build_chart.usage_fields({"used": 1, "unused": 0, "undetermined": 0}, 1, text)["usageLine"],
                         "1 of 1 reference is classified as used")

    def build(self, dependencies: str) -> dict:
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            (folder / "dependencies.csv").write_text(dependencies, encoding="utf-8-sig")
            (folder / "package-frameworks.csv").write_text(
                "Package;Version;Framework\nContoso.Core;1.0.0;netstandard2.0\n", encoding="utf-8-sig")
            (folder / "eol.json").write_text("{}")
            sys.argv = ["build_chart.py", "--dependencies", str(folder / "dependencies.csv"),
                        "--frameworks", str(folder / "package-frameworks.csv"), "--title", "Chart",
                        "--as-of", "2026-10-27", "--eol-file", str(folder / "eol.json"), "--lang", "fr",
                        "--output", str(folder / "chart.html")]
            with contextlib.redirect_stdout(io.StringIO()):
                build_chart.main()
            page = (folder / "chart.html").read_text(encoding="utf-8")
            return json.loads(page.split("const DATA = ", 1)[1].split(";\n", 1)[0])

    def test_each_version_shows_how_many_of_its_references_the_code_uses(self):
        data = self.build(
            "File;Package;Version;Kind;Usage\n"
            "/A.csproj;Contoso.Core;1.0.0;Reference;Used\n"
            "/B.csproj;Contoso.Core;1.0.0;Reference;BuildOnly\n"
            "/C.csproj;Contoso.Core;1.0.0;Reference;Unused\n"
            "/D.csproj;Contoso.Core;1.0.0;Reference;Undetermined\n"
            "/E.csproj;Contoso.Core;2.0.0;Reference;Unused\n")

        versions = {v["version"]: v for v in data["packages"][0]["versions"]}
        # A package acting on the build only cannot be removed: it counts as used.
        self.assertEqual((versions["1.0.0"]["used"], versions["1.0.0"]["unused"], versions["1.0.0"]["undetermined"]),
                         (2, 1, 1))
        self.assertEqual(versions["1.0.0"]["valueLabel"], "4 · 2 utilisées · 1 indéterminée")
        self.assertEqual(versions["2.0.0"]["valueLabel"], "1 · non utilisée")
        self.assertEqual(versions["1.0.0"]["usageLabel"], "2 utilisées · 1 indéterminée")
        self.assertEqual(versions["1.0.0"]["usageLine"], "2 références considérées comme utilisées sur 4")
        self.assertEqual(versions["2.0.0"]["usageLine"], "0 référence considérée comme utilisée sur 1")
        # Undetermined references are told apart from unused ones without the hatching.
        self.assertEqual(versions["1.0.0"]["breakdown"], "2 utilisées · 1 inutilisée · 1 indéterminée")
        self.assertEqual(data["text"]["usageColumns"], ["Utilisées", "Inutilisées", "Indéterminées"])
        self.assertEqual(data["packages"][0]["countLabel"], "5 références · 2 utilisées")
        self.assertEqual([item["usage"] for item in data["usageLegend"]], ["used", "unused", "undetermined"])
        self.assertTrue(data["usage"])

    def test_a_reference_used_transitively_counts_as_used(self):
        data = self.build(
            "File;Package;Version;Kind;Usage\n"
            "/A.csproj;Contoso.Core;1.0.0;Reference;UsedTransitively\n"
            "/B.csproj;Contoso.Core;1.0.0;Reference;Unused\n")

        version = data["packages"][0]["versions"][0]
        self.assertEqual((version["used"], version["unused"], version["undetermined"]), (1, 1, 0))

    def test_a_report_without_usage_column_shows_references_only(self):
        data = self.build("File;Package;Version;Kind\n/A.csproj;Contoso.Core;1.0.0;Reference\n")

        version = data["packages"][0]["versions"][0]
        self.assertFalse(data["usage"])
        self.assertNotIn("valueLabel", version)
        self.assertEqual(data["packages"][0]["countLabel"], "1 référence")
        self.assertEqual(data["usageLegend"], [])


class BranchTests(unittest.TestCase):
    ROWS = [
        {"Scope": "p", "Repository": "api", "Branch": "release", "Package": "A"},
        {"Scope": "p", "Repository": "api", "Branch": "develop", "Package": "A"},
        {"Scope": "p", "Repository": "lib", "Branch": "release", "Package": "A"},
    ]

    def select(self, branches):
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            rows = build_chart.select_branches(self.ROWS, branches, Path("dependencies.csv"))
        return [f"{r['Repository']} {r['Branch']}" for r in rows], err.getvalue()

    def test_the_branch_filter_keeps_the_matching_branches(self):
        self.assertEqual(self.select(["Release"]), (["api release", "lib release"], ""))
        self.assertEqual(self.select(["rel*", "develop"])[0], ["api release", "api develop", "lib release"])

    def test_several_branches_of_a_repository_are_reported_without_a_filter(self):
        rows, warning = self.select([])
        self.assertEqual(rows, ["api release", "api develop", "lib release"])
        self.assertIn("1 repository has several branches in dependencies.csv (api)", warning)
        self.assertIn("Pass --branch to keep one.", warning)

    def test_a_single_branch_per_repository_needs_no_warning(self):
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            build_chart.select_branches(self.ROWS[:1] + self.ROWS[2:], [], Path("dependencies.csv"))
        self.assertEqual(err.getvalue(), "")


class CsvTests(unittest.TestCase):
    def test_the_separator_is_the_one_of_the_header_line(self):
        for delimiter in (",", ";", "\t"):
            with self.subTest(delimiter=delimiter), tempfile.TemporaryDirectory() as folder:
                report = Path(folder) / "dependencies.csv"
                report.write_text(f"Scope{delimiter}Package{delimiter}Version\n"
                                  f'"a, b; c"{delimiter}A{delimiter}1.0.0\n', encoding="utf-8-sig")
                columns, rows = build_chart.read_csv_with_columns(report)
                self.assertEqual(columns, ["Scope", "Package", "Version"])
                self.assertEqual(rows, [{"Scope": "a, b; c", "Package": "A", "Version": "1.0.0"}])


class ProjectTypeTests(unittest.TestCase):
    ROWS = [
        {"Repository": "api", "File": "/src/Api.csproj", "ProjectType": "Application"},
        {"Repository": "api", "File": "/tests/Api.Tests.csproj", "ProjectType": "Test"},
        {"Repository": "api", "File": "/Directory.Build.props", "ProjectType": ""},
    ]

    def test_the_filter_keeps_the_projects_of_the_types(self):
        columns = ["Repository", "File", "ProjectType"]
        rows = build_chart.select_project_types(self.ROWS, columns, ["application"], Path("dependencies.csv"))
        self.assertEqual([r["File"] for r in rows], ["/src/Api.csproj"])
        self.assertEqual(build_chart.select_project_types(self.ROWS, columns, [], Path("dependencies.csv")), self.ROWS)

    def test_a_report_without_project_types_cannot_be_filtered_even_without_rows(self):
        with tempfile.TemporaryDirectory() as folder:
            report = Path(folder) / "dependencies.csv"
            report.write_text("Project;Repository;File;Package;Version;Kind\n", encoding="utf-8-sig")
            columns, rows = build_chart.read_csv_with_columns(report)
        self.assertEqual((columns[-1], rows), ("Kind", []))
        with self.assertRaises(SystemExit) as raised:
            build_chart.select_project_types(rows, columns, ["Application"], report)
        self.assertIn("has no ProjectType column", str(raised.exception))


class ActivityTests(unittest.TestCase):
    ROWS = [
        {"Scope": "p", "Repository": "api", "Branch": "release", "Package": "A"},
        {"Scope": "p", "Repository": "api", "Branch": "develop", "Package": "A"},
        {"Scope": "p", "Repository": "old", "Branch": "release", "Package": "A"},
    ]

    def repositories(self, folder: str, text: str) -> Path:
        path = Path(folder) / "repositories.csv"
        path.write_text(text, encoding="utf-8-sig")
        return path

    def test_only_the_recently_changed_repositories_and_branches_are_kept(self):
        with tempfile.TemporaryDirectory() as folder:
            path = self.repositories(folder,
                "Scope;Repository;Branch;Status;FilesExamined;References;Details;CommitDate\n"
                "p;api;release;OK;1;1;;2026-10-28T09:42:56Z\n"
                "p;api;develop;OK;1;1;;2025-01-10T08:00:00Z\n"
                "p;old;release;OK;1;1;;2024-03-01T08:00:00Z\n")
            rows = build_chart.select_active(self.ROWS, path, dt.date(2026, 1, 1))
        self.assertEqual([f"{r['Repository']} {r['Branch']}" for r in rows], ["api release"])
        self.assertEqual(build_chart.select_active(self.ROWS, None, None), self.ROWS)

    def test_reports_older_than_1_0_name_the_scope_project(self):
        rows = [{"Project": "p", "Repository": "api", "Branch": "release", "Package": "A"}]
        with tempfile.TemporaryDirectory() as folder:
            path = self.repositories(folder,
                "Project;Repository;Branch;Status;FilesExamined;References;Details;CommitDate\n"
                "p;api;release;OK;1;1;;2026-10-28T09:42:56Z\n")
            self.assertEqual(build_chart.select_active(rows, path, dt.date(2026, 1, 1)), rows)

    def test_the_date_filter_needs_a_report_with_dates(self):
        with self.assertRaises(SystemExit) as raised:
            build_chart.select_active(self.ROWS, None, dt.date(2026, 1, 1))
        self.assertIn("needs --repositories", str(raised.exception))
        with tempfile.TemporaryDirectory() as folder:
            path = self.repositories(folder, "Project;Repository;Branch;Status;FilesExamined;References;Details\n")
            with self.assertRaises(SystemExit) as raised:
                build_chart.select_active(self.ROWS, path, dt.date(2026, 1, 1))
        self.assertIn("has no CommitDate column", str(raised.exception))


if __name__ == "__main__":
    unittest.main()
