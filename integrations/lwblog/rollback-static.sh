#!/usr/bin/env bash
# Explicit rollback; only the website's pointer and website-specific config change.
set -euo pipefail
backup_root=$(cd "${1:?Specify the deployment backup directory}" && pwd)
case "$backup_root" in /var/backups/lwblog.cn-before-projects-*) ;; *) echo 'Unexpected backup path' >&2; exit 1 ;; esac
previous=$(cat "$backup_root/previous-target.txt")
case "$previous" in /var/www/lwblog.cn|/var/www/lwblog-releases/*) ;; *) echo 'Unexpected previous release' >&2; exit 1 ;; esac
python3 "$backup_root/release-pointer.py" /var/www/lwblog-current "$previous"
install -m 644 "$backup_root/site.conf" /etc/nginx/sites-available/lwblog.cn
for name in lwblog-cardpulse-security.conf lwblog-cardpulse-locations.conf; do
    if test -f "$backup_root/$name"; then
        install -m 644 "$backup_root/$name" "/etc/nginx/snippets/$name"
    else rm -f "/etc/nginx/snippets/$name"; fi
done
nginx -t
systemctl reload nginx
printf 'Restored website release: %s\n' "$previous"
