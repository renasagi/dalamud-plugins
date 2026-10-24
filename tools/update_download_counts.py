#!/usr/bin/env python3
"""Copy published GitHub ZIP download totals into the Dalamud feed."""

import argparse
import copy
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import tempfile


SOURCES = {
    "CritLuck": ("critluck-v", "CritLuck.zip"),
    "PartyInitials": ("partyinitials-v", "PartyInitials.zip"),
    "ClientLanguageOverride": ("clientlanguageoverride-v", "ClientLanguageOverride.zip"),
    "InfoBar": ("infobar-v", "InfoBar.zip"),
}
MAX_COUNT = (1 << 63) - 1


def fetch_releases(repository):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Expected an owner/repository name.")
    result = subprocess.run(
        [
            "gh", "api", "--hostname", "github.com", "--paginate",
            f"repos/{repository}/releases?per_page=100",
            "--jq", ".[] | @json",
        ],
        capture_output=True, text=True, timeout=60,
    )
    if result.returncode:
        raise ValueError("GitHub release lookup failed; feed unchanged.")
    return [json.loads(line) for line in result.stdout.splitlines() if line.strip()]


def count_downloads(releases):
    totals = dict.fromkeys(SOURCES, 0)
    seen_assets = set()
    if not isinstance(releases, list):
        raise ValueError("Expected a list of releases.")
    for release in releases:
        if not isinstance(release, dict) or type(release.get("draft")) is not bool:
            raise ValueError("Invalid release metadata.")
        if release["draft"]:
            continue
        tag = release.get("tag_name")
        if not isinstance(tag, str):
            raise ValueError("Missing release tag.")
        for plugin, (prefix, filename) in SOURCES.items():
            if not tag.startswith(prefix):
                continue
            assets = release.get("assets")
            if not isinstance(assets, list):
                raise ValueError("Missing release asset list.")
            for asset in assets:
                if not isinstance(asset, dict):
                    raise ValueError("Invalid release asset.")
                if asset.get("name") != filename or asset.get("state") != "uploaded":
                    continue
                asset_id = asset.get("id")
                count = asset.get("download_count")
                if type(asset_id) is not int or asset_id <= 0:
                    raise ValueError("Invalid asset ID.")
                if asset_id in seen_assets:
                    raise ValueError("Duplicate asset in API results; feed unchanged.")
                if type(count) is not int or not 0 <= count <= MAX_COUNT:
                    raise ValueError("Invalid download count.")
                seen_assets.add(asset_id)
                totals[plugin] += count
                if totals[plugin] > MAX_COUNT:
                    raise ValueError("Download total exceeds the manifest limit.")
    return totals


def update_feed(feed, totals):
    if not isinstance(feed, list):
        raise ValueError("Expected a list of feed entries.")
    updated = copy.deepcopy(feed)
    seen_plugins = set()
    for plugin in updated:
        if not isinstance(plugin, dict) or not isinstance(plugin.get("InternalName"), str):
            raise ValueError("Invalid feed entry.")
        name = plugin["InternalName"]
        if name in seen_plugins:
            raise ValueError("Duplicate plugin in feed.")
        seen_plugins.add(name)
        if name in totals:
            count = totals[name]
            if type(count) is not int or not 0 <= count <= MAX_COUNT:
                raise ValueError("Invalid download total.")
            plugin["DownloadCount"] = count
    return updated


def refresh(path, repository, dry_run=False):
    path = Path(path)
    if path.is_symlink():
        raise ValueError("Feed must be a regular file, not a symlink.")
    original = json.loads(path.read_text(encoding="utf-8"))
    totals = count_downloads(fetch_releases(repository))
    updated = update_feed(original, totals)
    for plugin in updated:
        if plugin["InternalName"] in totals:
            print(f"{plugin['InternalName']}: {plugin['DownloadCount']}")
    if updated == original or dry_run:
        return False

    # Replace only after the entire API response and feed have been validated.
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w", encoding="utf-8", dir=path.parent, delete=False,
        ) as output:
            temporary = Path(output.name)
            output.write(json.dumps(updated, indent=2, ensure_ascii=False) + "\n")
        temporary.chmod(stat.S_IMODE(path.stat().st_mode))
        os.replace(temporary, path)
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default="renasagi/dalamud-plugins")
    parser.add_argument("--feed", type=Path, default=Path("pluginmaster.json"))
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    try:
        changed = refresh(args.feed, args.repo, args.dry_run)
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        parser.exit(1, f"Download count refresh failed: {error}\n")
    print("Feed updated." if changed else "Feed unchanged.")


if __name__ == "__main__":
    main()
