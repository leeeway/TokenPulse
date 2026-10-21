#!/usr/bin/env python3
"""Validate catalog versions locally and optionally public GitHub release assets."""
import argparse
import json
from pathlib import Path
import re
import urllib.request

CATALOG = Path(__file__).parent / 'site/src/data/projects.json'


def downloads(projects):
    ids = set()
    for project in projects:
        slug = project['id']
        if not re.fullmatch(r'[a-z0-9-]+', slug) or slug in ids:
            raise ValueError(f'Invalid or duplicate project id: {slug}')
        ids.add(slug)
        repo = project['repo']
        if not re.fullmatch(r'https://github\.com/leeeway/[A-Za-z0-9_.-]+', repo):
            raise ValueError(f'Unexpected repository: {repo}')
        for item in project.get('downloads', []):
            prefix = repo + '/releases/download/'
            if not item['href'].startswith(prefix):
                raise ValueError(f'Download belongs to another repository: {item["href"]}')
            parts = item['href'][len(prefix):].split('/')
            if len(parts) != 2 or not all(re.fullmatch(r'[A-Za-z0-9_.-]+', p) for p in parts):
                raise ValueError('Invalid release asset URL')
            tag, asset = parts
            version = item['version']
            if not re.fullmatch(r'\d+\.\d+\.\d+', version) or not re.fullmatch(r'(?:mac-|windows-)?v' + re.escape(version), tag):
                raise ValueError(f'Displayed version {version} disagrees with tag {tag}')
            yield repo, tag, asset


def fetch(url):
    request = urllib.request.Request(url, headers={'User-Agent': 'TokenPulse-website-version-check'})
    with urllib.request.urlopen(request, timeout=30) as response:
        return response.read()


def verify_asset(release, name, checksum_text):
    if release.get('draft'):
        raise ValueError('Release is still a draft')
    asset = next((a for a in release['assets'] if a['name'] == name), None)
    if not asset or asset.get('state') != 'uploaded' or asset.get('size', 0) <= 0:
        raise ValueError(f'Missing or incomplete release asset: {name}')
    sums = {}
    for line in checksum_text.splitlines():
        match = re.fullmatch(r'([a-fA-F0-9]{64})\s+\*?(.+)', line.strip())
        if match:
            filename = match[2].removeprefix('./')
            if filename in sums:
                raise ValueError(f'Duplicate checksum: {filename}')
            sums[filename] = match[1].lower()
    if name not in sums:
        raise ValueError(f'Asset absent from checksum file: {name}')
    digest = asset.get('digest')
    if digest and digest != 'sha256:' + sums[name]:
        raise ValueError(f'Release digest disagrees with checksum file: {name}')


def validate(projects, online=False):
    releases = {}
    count = 0
    for repo, tag, name in downloads(projects):
        if online:
            key = (repo, tag)
            if key not in releases:
                release = json.loads(fetch('https://api.github.com/repos/' + repo.split('github.com/')[1] + '/releases/tags/' + tag))
                if release['tag_name'] != tag:
                    raise ValueError('Unexpected release tag')
                checksum_asset = next((a for a in release['assets'] if a['name'] in ('SHA256SUMS.txt', 'SHA256SUMS')), None)
                expected = repo + '/releases/download/' + tag + '/'
                if not checksum_asset or not checksum_asset['browser_download_url'].startswith(expected):
                    raise ValueError(f'Missing checksum file: {repo} {tag}')
                sums = fetch(checksum_asset['browser_download_url']).decode('utf-8-sig')
                releases[key] = release, sums
            verify_asset(releases[key][0], name, releases[key][1])
        count += 1
    return count


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--online', action='store_true')
    args = parser.parse_args()
    count = validate(json.loads(CATALOG.read_text()), args.online)
    print(f'PASS: {count} download versions' + (' and public release assets/checksums' if args.online else ' (offline)'))
