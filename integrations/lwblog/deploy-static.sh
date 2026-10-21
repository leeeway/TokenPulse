#!/usr/bin/env bash
# Run on the existing lwblog.cn host with the reviewed archive and app snippets.
# No credentials, package installs, proxy configuration, or proxy restarts.
set -euo pipefail
payload_dir=$(cd "${1:?Specify the uploaded payload directory}" && pwd)
site_root=/var/www/lwblog.cn
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
tar -xzf "$payload_dir/site.tar.gz" -C "$release_root" --no-same-owner
for route in index.html en/index.html projects/index.html en/projects/index.html apps/cardpulse/index.html; do
  test -s "$release_root/$route"
done
for slug in tokenpulse cardpulse daily-trend-radar logmind; do
  test -s "$release_root/projects/$slug/index.html"
  test -s "$release_root/en/projects/$slug/index.html"
done
cp -a "$site_root" "$backup_root/site"
cp -p "$site_conf" "$backup_root/site.conf"
cp -p /etc/nginx/snippets/lwblog-security.conf "$backup_root/main-security.conf"
for conf in "$app_security" "$app_locations"; do
  if test -e "$conf"; then cp -p "$conf" "$backup_root/$(basename "$conf")"; fi
done
rollback() {
  set +e
  for route in projects en/projects apps/cardpulse; do
    rm -rf "${site_root:?}/$route"
    if test -e "$backup_root/site/$route"; then
      mkdir -p "$(dirname "$site_root/$route")"
      cp -a "$backup_root/site/$route" "$site_root/$route"
    fi
  done
  for route in index.html en/index.html; do
    cp -p "$backup_root/site/$route" "$site_root/$route.rollback"
    mv -f "$site_root/$route.rollback" "$site_root/$route"
  done
  cp -p "$backup_root/site.conf" "$site_conf"
  for conf in "$app_security" "$app_locations"; do
    if test -f "$backup_root/$(basename "$conf")"; then
      cp -p "$backup_root/$(basename "$conf")" "$conf"
    else rm -f "$conf"; fi
  done
  nginx -t && systemctl reload nginx
  printf 'Deployment rolled back; backup=%s\n' "$backup_root" >&2
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
    p.write_text(s.replace(anchor, include + '\n' + anchor, 1))
PY
nginx -t
# Retain old immutable assets so already-open pages continue working.
mkdir -p "$site_root/_astro" "$site_root/apps" "$site_root/en"
cp -a "$release_root/_astro/." "$site_root/_astro/"
for route in projects en/projects apps/cardpulse; do
  mkdir -p "$site_root/$route"
  cp -a "$release_root/$route/." "$site_root/$route/"
done
chmod -R a+rX "$site_root/projects" "$site_root/en/projects" "$site_root/apps/cardpulse" "$site_root/_astro"
systemctl reload nginx
for route in en/index.html index.html; do
  install -m 644 "$release_root/$route" "$site_root/$route.projects-new"
  mv -f "$site_root/$route.projects-new" "$site_root/$route"
done
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
