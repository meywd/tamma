#!/usr/bin/env bash
# Weekly Docker cleanup cron job for Tamma VPS
# Install: crontab -e → 0 3 * * 0 /opt/tamma/docker/cleanup-cron.sh >> /var/log/tamma-cleanup.log 2>&1
set -e

echo "=== Tamma cleanup $(date -Is) ==="

# Remove images not used in the last 7 days
docker image prune -a --filter 'until=168h' -f 2>/dev/null || true

# Remove dangling build cache
docker builder prune -a --filter 'until=168h' -f 2>/dev/null || true

# Remove unused volumes (not attached to any container)
docker volume prune -f 2>/dev/null || true

# Remove stopped containers
docker container prune -f 2>/dev/null || true

# Trim systemd journal to 500MB
journalctl --vacuum-size=500M 2>/dev/null || true

# Report
USAGE=$(df / --output=pcent | tail -1 | tr -d ' %')
echo "Disk usage after cleanup: ${USAGE}%"

if [ "$USAGE" -gt 80 ]; then
  echo "WARNING: Disk usage still above 80%"
  df -h /
  du -sh /var/lib/docker/ /var/lib/containerd/ /var/log/ 2>/dev/null
fi

echo "=== Done ==="
