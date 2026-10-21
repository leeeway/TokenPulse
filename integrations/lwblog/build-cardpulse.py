#!/usr/bin/env python3
"""Build a pinned, clean CardPulse release for the website without editing its checkout."""
import argparse
import io
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--repo', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--tag', default='v0.2.0')
args = parser.parse_args()
repo = args.repo.resolve()
commit = subprocess.check_output(['git', '-C', str(repo), 'rev-parse', args.tag + '^{commit}'], text=True).strip()
with tempfile.TemporaryDirectory(prefix='cardpulse-lwblog-') as directory:
    work = Path(directory)
    archive = subprocess.check_output(['git', '-C', str(repo), 'archive', commit])
    with tarfile.open(fileobj=io.BytesIO(archive)) as source:
        source.extractall(work, filter='data')
    lock = repo / 'package-lock.json'
    if (repo / 'node_modules').is_dir() and lock.read_bytes() == (work / lock.name).read_bytes():
        (work / 'node_modules').symlink_to(repo / 'node_modules', target_is_directory=True)
    else:
        subprocess.run(['npm', 'ci', '--ignore-scripts'], cwd=work, check=True)
    subprocess.run(['npm', 'run', 'build', '--', '--base=/apps/cardpulse/', '--configLoader', 'native'], cwd=work, check=True)
    index = work / 'dist/index.html'
    text = index.read_text().replace('</head>', '<meta name="robots" content="noindex" /></head>')
    bridge = '<nav aria-label="个人站导航" style="display:flex;justify-content:space-between;gap:20px;padding:12px 24px;background:#f7f9fc;border-bottom:1px solid #e2e8ef;font:12px -apple-system,BlinkMacSystemFont,sans-serif;color:#536c88"><a style="color:inherit;text-decoration:none" href="/projects/cardpulse/">← leeway · CardPulse 项目介绍</a><a style="color:inherit;text-decoration:none" href="https://github.com/leeeway/CardPulse" target="_blank" rel="noopener noreferrer">GitHub ↗</a></nav>'
    index.write_text(text.replace('<body>', '<body>' + bridge))
    (work / 'dist/BUILD-INFO.txt').write_text(f'CardPulse {args.tag}\nCommit: {commit}\nBase: /apps/cardpulse/\n')
    args.output.mkdir(parents=True, exist_ok=True)
    shutil.copytree(work / 'dist', args.output, dirs_exist_ok=True)
print('Built release snapshot:', args.tag, commit)
