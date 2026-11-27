#!/usr/bin/env bash
# MECHWARE nightly backup for the single-server stack (infra/docker/docker-compose.prod.yml).
#
#   sudo bash /opt/mechware/infra/scripts/backup.sh          # run by hand; the systemd timer runs it every night
#
# What it saves, into BACKUP_DIR/daily/<YYYY-MM-DD>/:
#   db.dump              pg_dump -Fc of the database (checked with pg_restore --list)
#   documents.tar.gz     the documents volume (uploads, invoice PDFs)
#   manifest.txt         time, app commit, last migration, table count, sizes
#   SHA256SUMS           checksums of the two files
# The first backup of each ISO week is also kept as weekly/<YYYY-Www> (hard links, no extra space).
# Keeps BACKUP_KEEP_DAILY daily (14) and BACKUP_KEEP_WEEKLY weekly (8) copies.
# With BACKUP_RCLONE_REMOTE set, the new copy is uploaded off-site with rclone and the remote is pruned by age.
# NOT saved: prod.env (keep it in a password manager), the search index (rebuilt from the DB), carts in Redis,
# HTTPS certificates (re-issued), virus signatures (re-downloaded).
#
# Settings come from infra/docker/prod.env (BACKUP_*); environment variables of the same name win.
# Exit code: 0 = backup made (a warning is printed when there is no off-site copy), non-zero = no usable backup today.

set -Eeuo pipefail
umask 077

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MECHWARE_DIR="${MECHWARE_DIR:-$(cd "$SCRIPT_DIR/../.." && pwd)}"
COMPOSE_FILE="${COMPOSE_FILE:-$MECHWARE_DIR/infra/docker/docker-compose.prod.yml}"
ENV_FILE="${ENV_FILE:-$MECHWARE_DIR/infra/docker/prod.env}"
PROJECT="${COMPOSE_PROJECT_NAME:-mechware}"

log() { printf '%s backup: %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*"; }
warn() { printf '%s backup: WARNING: %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" >&2; }
die() { printf '%s backup: FAILED: %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" >&2; exit 1; }

[[ -f "$ENV_FILE" ]] || die "settings file not found: $ENV_FILE"
[[ -f "$COMPOSE_FILE" ]] || die "compose file not found: $COMPOSE_FILE"

# Reads KEY=value from prod.env without executing it (the file is not shell syntax). Strips one pair of quotes.
env_value() {
  local line value
  line="$(grep -E "^$1=" "$ENV_FILE" | tail -n 1 || true)"
  value="${line#*=}"
  if [[ ${#value} -ge 2 && ( ( "${value:0:1}" == "'" && "${value: -1}" == "'" ) || ( "${value:0:1}" == '"' && "${value: -1}" == '"' ) ) ]]; then
    value="${value:1:${#value}-2}"
  fi
  printf '%s' "$value"
}
setting() { local v="${!1:-}"; [[ -n "$v" ]] || v="$(env_value "$1")"; [[ -n "$v" ]] || v="$2"; printf '%s' "$v"; }

BACKUP_DIR="$(setting BACKUP_DIR /var/backups/mechware)"
KEEP_DAILY="$(setting BACKUP_KEEP_DAILY 14)"
KEEP_WEEKLY="$(setting BACKUP_KEEP_WEEKLY 8)"
RCLONE_REMOTE="$(setting BACKUP_RCLONE_REMOTE '')"
PING_URL="$(setting BACKUP_PING_URL '')"
[[ "$KEEP_DAILY" =~ ^[1-9][0-9]*$ && "$KEEP_WEEKLY" =~ ^[1-9][0-9]*$ ]] || die "BACKUP_KEEP_DAILY/WEEKLY must be whole numbers above 0"

compose() { docker compose -p "$PROJECT" -f "$COMPOSE_FILE" --env-file "$ENV_FILE" "$@"; }
psql_value() { compose exec -T postgres psql -U mechware -d mechware -At -v ON_ERROR_STOP=1 -c "$1"; }

mkdir -p "$BACKUP_DIR/daily" "$BACKUP_DIR/weekly"
chmod 700 "$BACKUP_DIR"

# One backup at a time (the timer and a manual run must not overlap).
if command -v flock >/dev/null 2>&1; then
  exec 9>"$BACKUP_DIR/.lock"
  flock -n 9 || die "another backup is still running"
fi

# Space check: need room for at least the size of the previous backup plus 1 GB.
last_size_kb=0
last_dir="$(find "$BACKUP_DIR/daily" -mindepth 1 -maxdepth 1 -type d -name '20*' | sort | tail -n 1)"
if [[ -n "$last_dir" ]]; then last_size_kb="$(du -sk "$last_dir" | cut -f1)"; fi
free_kb="$(df -Pk "$BACKUP_DIR" | awk 'NR==2 {print $4}')"
(( free_kb > last_size_kb + 1048576 )) || die "not enough free disk space in $BACKUP_DIR (${free_kb} KB free)"

pg_container="$(compose ps -q postgres)"
[[ -n "$pg_container" ]] || die "the postgres container is not running (start the stack first)"
pg_image="$(docker inspect --format '{{.Config.Image}}' "$pg_container")"

today="$(date +%F)"
week="$(date +%G-W%V)"
work="$BACKUP_DIR/daily/.tmp-$today-$$"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work"

log "dumping the database"
compose exec -T postgres pg_dump -U mechware -d mechware -Fc -Z 6 > "$work/db.dump"
[[ -s "$work/db.dump" ]] || die "pg_dump produced an empty file"
compose exec -T postgres pg_restore --list < "$work/db.dump" > "$work/db.toc" || die "the dump cannot be read back (pg_restore --list)"
toc_entries="$(grep -cv '^;' "$work/db.toc" || true)"
rm -f "$work/db.toc"

log "archiving the documents volume"
docker run --rm --network none -v "${PROJECT}_documents:/data:ro" "$pg_image" \
  tar -C /data --numeric-owner -czf - . > "$work/documents.tar.gz"
tar -tzf - < "$work/documents.tar.gz" > /dev/null || die "the documents archive is damaged"
doc_files="$(tar -tzf - < "$work/documents.tar.gz" | grep -vc '/$' || true)"

last_migration="$(psql_value 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1' || echo unknown)"
table_count="$(psql_value "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'")"
app_commit="$(git -c safe.directory="$MECHWARE_DIR" -C "$MECHWARE_DIR" rev-parse --short HEAD 2>/dev/null || echo unknown)"

(
  cd "$work"
  sha256sum db.dump documents.tar.gz > SHA256SUMS
  {
    echo "created=$(date -Iseconds)"
    echo "host=$(hostname)"
    echo "project=$PROJECT"
    echo "app_commit=$app_commit"
    echo "postgres_image=$pg_image"
    echo "last_migration=$last_migration"
    echo "table_count=$table_count"
    echo "dump_toc_entries=$toc_entries"
    echo "document_files=$doc_files"
    echo "db_dump_bytes=$(wc -c < db.dump | tr -d ' ')"
    echo "documents_bytes=$(wc -c < documents.tar.gz | tr -d ' ')"
  } > manifest.txt
)

# Publish today's copy (a second run on the same day replaces it).
rm -rf "${BACKUP_DIR:?}/daily/$today"
mv "$work" "$BACKUP_DIR/daily/$today"
trap - EXIT
log "saved $BACKUP_DIR/daily/$today ($(du -sh "$BACKUP_DIR/daily/$today" | cut -f1), $table_count tables, $doc_files documents)"

if [[ ! -d "$BACKUP_DIR/weekly/$week" ]]; then
  cp -al "$BACKUP_DIR/daily/$today" "$BACKUP_DIR/weekly/$week" 2>/dev/null || cp -a "$BACKUP_DIR/daily/$today" "$BACKUP_DIR/weekly/$week"
  log "kept as weekly copy $week"
fi

# Retention: newest N folders of each kind.
prune() {
  local dir="$1" keep="$2"
  find "$dir" -mindepth 1 -maxdepth 1 -type d -name '20*' | sort -r | tail -n "+$((keep + 1))" | while read -r old; do
    rm -rf "$old"
    log "removed old copy $old"
  done
}
prune "$BACKUP_DIR/daily" "$KEEP_DAILY"
prune "$BACKUP_DIR/weekly" "$KEEP_WEEKLY"
find "$BACKUP_DIR/daily" -mindepth 1 -maxdepth 1 -type d -name '.tmp-*' -mtime +1 -exec rm -rf {} + 2>/dev/null || true

# Off-site copy.
if [[ -n "$RCLONE_REMOTE" ]]; then
  command -v rclone >/dev/null 2>&1 || die "BACKUP_RCLONE_REMOTE is set but rclone is not installed (runbook step 14)"
  log "uploading to $RCLONE_REMOTE"
  rclone copy "$BACKUP_DIR/daily/$today" "$RCLONE_REMOTE/daily/$today" || die "off-site upload failed (the local copy is fine)"
  rclone copy "$BACKUP_DIR/weekly/$week" "$RCLONE_REMOTE/weekly/$week" || die "off-site upload failed (the local copy is fine)"
  # Age-based pruning on the remote. Never `rclone sync`: a wiped server would then wipe the off-site copies too.
  rclone delete "$RCLONE_REMOTE/daily" --min-age "$((KEEP_DAILY + 1))d" || warn "could not prune old daily copies off-site"
  rclone delete "$RCLONE_REMOTE/weekly" --min-age "$((KEEP_WEEKLY * 7 + 7))d" || warn "could not prune old weekly copies off-site"
  rclone rmdirs "$RCLONE_REMOTE" --leave-root 2>/dev/null || true
  log "off-site copy done"
else
  warn "NO OFF-SITE COPY. Backups are only on this server's disk: a disk failure, a deleted server or a break-in"
  warn "loses the shop AND every backup. Set BACKUP_RCLONE_REMOTE in prod.env (docs/DEPLOY_RUNBOOK.md step 14)."
fi

if [[ -n "$PING_URL" ]]; then
  curl -fsS -m 10 --retry 3 "$PING_URL" > /dev/null || warn "could not ping BACKUP_PING_URL"
fi
log "done"
