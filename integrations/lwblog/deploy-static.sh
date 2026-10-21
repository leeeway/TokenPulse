#!/usr/bin/env bash
# Run on the existing lwblog.cn host with the reviewed archive and app snippets.
# No credentials, package installs, proxy configuration, or proxy restarts.
set -euo pipefail
payload_dir=$(cd "${1:?Specify the uploaded payload directory}" && pwd)
current=/var/www/lwblog-current
site_root=/var/www/lwblog.cn
if test -L "$current"; then site_root=$(readlink -f "$current");
elif test -e "$current"; then echo 'Current pointer must be a symlink' >&2; exit 1; fi
site_conf=/etc/nginx/sites-available/lwblog.cn
app_security=/etc/nginx/snippets/lwblog-cardpulse-security.conf
app_locations=/etc/nginx/snippets/lwblog-cardpulse-locations.conf
stamp=$(date -u +%Y%m%dT%H%M%SZ)
release_root="/var/www/lwblog-releases/$stamp-projects"
backup_root="/var/backups/lwblog.cn-before-projects-$stamp"
test -s "$site_root/index.html"
test -s "$site_root/en/index.html"
test -f "$site_conf"
test ! -e "$release_root"
test ! -e "$backup_root"
(cd "$payload_dir" && sha256sum -c SHA256SUMS.txt)
proxy_pid_before=$(systemctl show sing-box -p MainPID --value)
nginx_pid_before=$(systemctl show nginx -p MainPID --value)
test "$proxy_pid_before" != 0
test "$nginx_pid_before" != 0
systemctl is-active --quiet sing-box nginx
mkdir -p "$release_root" "$backup_root"
# Stage the current full tree first to preserve unmanaged files and older assets.
cp -a "$site_root/." "$release_root/"
# Replace managed pages in staging, never in the live tree.
rm -rf "$release_root/projects" "$release_root/en/projects" "$release_root/apps/cardpulse"
tar -xzf "$payload_dir/site.tar.gz" -C "$release_root" --no-same-owner
# Already-open browser tabs may still request previous hashed app assets.
if test -d "$site_root/apps/cardpulse/assets"; then
    cp -an "$site_root/apps/cardpulse/assets/." "$release_root/apps/cardpulse/assets/"
fi
if test -d "$site_root/projects/media"; then
    cp -an "$site_root/projects/media/." "$release_root/projects/media/"
fi
chmod -R a+rX "$release_root"
for route in index.html en/index.html projects/index.html en/projects/index.html apps/cardpulse/index.html; do
  test -s "$release_root/$route"
done
for slug in tokenpulse cardpulse daily-trend-radar logmind; do
  test -s "$release_root/projects/$slug/index.html"
  test -s "$release_root/en/projects/$slug/index.html"
done
mkdir -p "$backup_root/site"
cp -a "$site_root/." "$backup_root/site/"
printf '%s\n' "$site_root" > "$backup_root/previous-target.txt"
cp "$payload_dir/release-pointer.py" "$payload_dir/rollback-static.sh" "$backup_root/"
cp -p "$site_conf" "$backup_root/site.conf"
cp -p /etc/nginx/snippets/lwblog-security.conf "$backup_root/main-security.conf"
for conf in "$app_security" "$app_locations"; do
  if test -e "$conf"; then cp -p "$conf" "$backup_root/$(basename "$conf")"; fi
done
rollback() {
  trap - ERR
  bash "$backup_root/rollback-static.sh" "$backup_root" || true
  printf 'Deployment failed; rollback attempted; backup=%s\n' "$backup_root" >&2
}
trap rollback ERR
install -m 644 "$payload_dir/nginx-cardpulse-security.conf" "$app_security"
install -m 644 "$payload_dir/nginx-cardpulse.conf.example" "$app_locations"
python3 - "$site_conf" <<'PY'
from pathlib import Path
import sys
p = Path(sys.argv[1])
s = p.read_text()
include = '    include /etc/nginx/snippets/lwblog-cardpulse-locations.conf;'
if include not in s:
    anchor = '    location / { try_files $uri $uri/ =404; }'
    if s.count(anchor) != 1 or 'location /apps/cardpulse/' in s:
        raise SystemExit('Website locations changed; review before deploying')
    s = s.replace(anchor, include + '\n' + anchor, 1)
old = 'root /var/www/lwblog.cn;'
new = 'root /var/www/lwblog-current;'
if old in s and s.count(old) == 1:
    s = s.replace(old, new, 1)
elif s.count(new) != 1:
    raise SystemExit('Website root changed; review before deploying')
p.write_text(s)
PY
# Bootstrap the new root pointer to the old release before reloading Nginx.
if ! test -L "$current"; then python3 "$payload_dir/release-pointer.py" "$current" "$site_root"; fi
nginx -t
systemctl reload nginx
# A single rename switches every route together. The old release remains intact.
python3 "$payload_dir/release-pointer.py" "$current" "$release_root"
for route in / /en/ /projects/ /en/projects/ /apps/cardpulse/; do
  curl --fail --silent --show-error --resolve lwblog.cn:443:127.0.0.1 "https://lwblog.cn$route" -o "$backup_root/verify.html"
  cmp "$release_root/${route#/}index.html" "$backup_root/verify.html"
done
cmp "$backup_root/main-security.conf" /etc/nginx/snippets/lwblog-security.conf
proxy_pid_after=$(systemctl show sing-box -p MainPID --value)
nginx_pid_after=$(systemctl show nginx -p MainPID --value)
test "$proxy_pid_before" = "$proxy_pid_after"
test "$nginx_pid_before" = "$nginx_pid_after"
systemctl is-active --quiet sing-box nginx
trap - ERR
printf 'deployment=success\nbackup=%s\nrelease=%s\nproxy_pid_before=%s\nproxy_pid_after=%s\nnginx_pid_before=%s\nnginx_pid_after=%s\n' "$backup_root" "$release_root" "$proxy_pid_before" "$proxy_pid_after" "$nginx_pid_before" "$nginx_pid_after"
