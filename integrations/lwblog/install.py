#!/usr/bin/env python3
"""Apply the reviewed overlay to the existing Astro site; retain timestamped backups."""
import argparse
from datetime import datetime, timezone
from pathlib import Path
import shutil
import re

parser = argparse.ArgumentParser()
parser.add_argument('--site-dir', type=Path, required=True)
args = parser.parse_args()
site = args.site_dir.resolve()
source = Path(__file__).resolve().parent / 'site'
home = site / 'src/components/ParallaxPortfolio.astro'
if not home.is_file() or not (site / 'astro.config.mjs').is_file():
    raise SystemExit('Expected the existing lwblog Astro source directory')
text = home.read_text()
start_marker = '<!-- lwblog-projects:start -->'
end_marker = '<!-- lwblog-projects:end -->'
if start_marker in text:
    start = text.index(start_marker)
    end = text.index(end_marker, start) + len(end_marker)
else:
    start = text.index('<section id="work"')
    end = text.index('</section>', start) + len('</section>')
replacement = '''<!-- lwblog-projects:start -->
      <section id="work" class="work-section wrap" aria-labelledby="work-title">
        <div class="work-heading reveal"><div><p class="eyebrow">{t.workEyebrow}</p><h2 id="work-title">{t.workTitle.split('\\n').map(line => <span>{line}</span>)}</h2></div><p>{zh ? '为真实的日常，做持续打磨的项目。' : 'Considered projects for real, everyday work.'}</p></div>
        <ProjectCards lang={lang} featuredOnly />
        <a class="project-all-link" href={zh ? '/projects/' : '/en/projects/'}>{zh ? '查看全部项目' : 'Explore all projects'} <span aria-hidden="true">↗</span></a>
      </section>
      <!-- lwblog-projects:end -->'''
text = text[:start] + replacement + text[end:]
if "import ProjectCards from './ProjectCards.astro';" not in text:
    anchor = "import '../styles/parallax.css';"
    if anchor not in text: raise SystemExit('Home import anchor changed; review before applying')
    text = text.replace(anchor, anchor + "\nimport ProjectCards from './ProjectCards.astro';", 1)
text = re.sub(r"  projects: \[\n.*?\n  \],\n", "", text, flags=re.S)
backup = site / '.lwblog-backups' / datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
targets = [home] + [site / p.relative_to(source) for p in source.rglob('*') if p.is_file()]
for target in targets:
    if target.exists():
        dest = backup / target.relative_to(site)
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(target, dest)
for path in source.rglob('*'):
    if path.is_file():
        dest = site / path.relative_to(source)
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, dest)
home.write_text(text)
print(f'Applied project integration; source backup: {backup}')
