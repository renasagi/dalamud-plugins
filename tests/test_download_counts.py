import contextlib
import copy
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from tools import update_download_counts as counts


def asset(asset_id=1, name="CritLuck.zip", downloads=19, state="uploaded"):
    return {"id": asset_id, "name": name, "download_count": downloads, "state": state}


def release(tag="critluck-v1.0.0", assets=None, draft=False, prerelease=False):
    return {
        "tag_name": tag, "assets": [asset()] if assets is None else assets,
        "draft": draft, "prerelease": prerelease,
    }


class DownloadCountsTests(unittest.TestCase):
    def test_sums_versions_and_published_prereleases(self):
        releases = [
            release(),
            release("partyinitials-v1.1.0", [asset(2, "PartyInitials.zip", 18)]),
            release("partyinitials-v1.1.1", [asset(3, "PartyInitials.zip", 9)]),
            release("clientlanguageoverride-v0.2.0", [asset(4, "ClientLanguageOverride.zip", 7)], prerelease=True),
            release("clientlanguageoverride-v0.2.1", [asset(5, "ClientLanguageOverride.zip", 4)], prerelease=True),
        ]
        self.assertEqual(counts.count_downloads(releases), {
            "CritLuck": 19, "PartyInitials": 27, "ClientLanguageOverride": 11,
        })

    def test_ignores_drafts_unrelated_tags_and_non_plugin_assets(self):
        releases = [
            release(draft=True),
            release("unrelated-v1", [asset(2)]),
            release("partyinitials-v1", [asset(3)]),
            release(assets=[asset(4, "source.zip"), asset(5, "CritLuck.zip.sha256"), asset(6, state="open")]),
        ]
        self.assertEqual(counts.count_downloads(releases), dict.fromkeys(counts.SOURCES, 0))

    def test_empty_releases_have_zero_counts(self):
        self.assertEqual(counts.count_downloads([]), dict.fromkeys(counts.SOURCES, 0))

    def test_missing_counts_and_invalid_numbers_fail(self):
        for value in (None, True, False, -1, "19", 1.5, counts.MAX_COUNT + 1):
            with self.subTest(value=value), self.assertRaises(ValueError):
                counts.count_downloads([release(assets=[asset(downloads=value)])])

    def test_duplicate_asset_ids_fail_instead_of_double_counting(self):
        with self.assertRaises(ValueError):
            counts.count_downloads([release(), release()])

    def test_overflow_fails(self):
        with self.assertRaises(ValueError):
            counts.count_downloads([release(assets=[asset(downloads=counts.MAX_COUNT), asset(2, downloads=1)])])

    def test_malformed_api_data_fails(self):
        for data in ({}, [None], [{"draft": "false"}], [{"draft": False}],
                     [{"draft": False, "tag_name": "critluck-v1"}],
                     [release(assets=[None])], [release(assets=[asset(asset_id=None)])]):
            with self.subTest(data=data), self.assertRaises(ValueError):
                counts.count_downloads(data)

    def test_only_download_count_changes_and_unknown_plugins_are_preserved(self):
        feed = json.loads(Path("pluginmaster.json").read_text(encoding="utf-8"))
        feed.append({"InternalName": "AnotherPlugin", "DownloadCount": 123})
        before = copy.deepcopy(feed)
        updated = counts.update_feed(feed, dict.fromkeys(counts.SOURCES, 42))
        self.assertEqual(feed, before)
        for old, new in zip(before, updated):
            if old["InternalName"] in counts.SOURCES:
                self.assertEqual(new["DownloadCount"], 42)
                self.assertEqual({k: v for k, v in new.items() if k != "DownloadCount"},
                                 {k: v for k, v in old.items() if k != "DownloadCount"})
            else:
                self.assertEqual(new, old)

    def test_duplicate_or_invalid_feed_entries_fail(self):
        for feed in ({}, [None], [{}], [{"InternalName": "CritLuck"}] * 2):
            with self.subTest(feed=feed), self.assertRaises(ValueError):
                counts.update_feed(feed, dict.fromkeys(counts.SOURCES, 1))

    def test_fetch_uses_pagination_and_reads_each_release(self):
        response = subprocess.CompletedProcess([], 0, json.dumps(release()) + "\n" + json.dumps(release("other")) + "\n")
        with patch.object(counts.subprocess, "run", return_value=response) as run:
            self.assertEqual(len(counts.fetch_releases("renasagi/dalamud-plugins")), 2)
        command = run.call_args.args[0]
        self.assertIn("--paginate", command)
        self.assertIn("repos/renasagi/dalamud-plugins/releases?per_page=100", command)
        self.assertIn(".[] | @json", command)
        self.assertNotIn("shell", run.call_args.kwargs)

    def test_failed_lookup_does_not_report_response_contents(self):
        response = subprocess.CompletedProcess([], 1, "", "server-response-marker")
        with patch.object(counts.subprocess, "run", return_value=response):
            with self.assertRaisesRegex(ValueError, "lookup failed") as error:
                counts.fetch_releases("renasagi/dalamud-plugins")
        self.assertNotIn("server-response-marker", str(error.exception))

    def test_invalid_repository_does_not_make_a_request(self):
        for repository in ("--invalid", "a/b/c", "https://example.com/a/b", "a b/c"):
            with self.subTest(repository=repository), patch.object(counts.subprocess, "run") as run:
                with self.assertRaises(ValueError):
                    counts.fetch_releases(repository)
                run.assert_not_called()

    def test_refresh_is_idempotent_and_preserves_permissions(self):
        with tempfile.TemporaryDirectory() as directory:
            feed = Path(directory) / "feed.json"
            feed.write_text('[{"InternalName":"CritLuck","LastUpdate":123,"AssemblyVersion":"1.0.0.0"}]\n')
            feed.chmod(0o644)
            with patch.object(counts, "fetch_releases", return_value=[release()]), contextlib.redirect_stdout(io.StringIO()):
                self.assertTrue(counts.refresh(feed, "renasagi/dalamud-plugins"))
                after = feed.read_bytes()
                self.assertFalse(counts.refresh(feed, "renasagi/dalamud-plugins"))
            self.assertEqual(feed.read_bytes(), after)
            self.assertEqual(feed.stat().st_mode & 0o777, 0o644)
            self.assertEqual(json.loads(after), [{"InternalName": "CritLuck", "LastUpdate": 123,
                                                "AssemblyVersion": "1.0.0.0", "DownloadCount": 19}])
            self.assertEqual(list(Path(directory).iterdir()), [feed])

    def test_dry_run_does_not_write(self):
        with tempfile.TemporaryDirectory() as directory:
            feed = Path(directory) / "feed.json"
            feed.write_text('[{"InternalName":"CritLuck"}]\n')
            before = feed.read_bytes()
            with patch.object(counts, "fetch_releases", return_value=[release()]), contextlib.redirect_stdout(io.StringIO()):
                self.assertFalse(counts.refresh(feed, "renasagi/dalamud-plugins", dry_run=True))
            self.assertEqual(feed.read_bytes(), before)

    def test_bad_response_does_not_change_feed(self):
        with tempfile.TemporaryDirectory() as directory:
            feed = Path(directory) / "feed.json"
            feed.write_text('[{"InternalName":"CritLuck","DownloadCount":19}]\n')
            before = feed.read_bytes()
            with patch.object(counts, "fetch_releases", return_value=[release(assets=[asset(downloads=-1)])]):
                with self.assertRaises(ValueError):
                    counts.refresh(feed, "renasagi/dalamud-plugins")
            self.assertEqual(feed.read_bytes(), before)

    def test_counts_can_fall_after_release_deletion(self):
        feed = [{"InternalName": "CritLuck", "DownloadCount": 99}]
        self.assertEqual(counts.update_feed(feed, counts.count_downloads([]))[0]["DownloadCount"], 0)


if __name__ == "__main__":
    unittest.main()
