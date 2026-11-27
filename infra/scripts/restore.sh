#!/usr/bin/env bash
# MECHWARE restore for the single-server stack (infra/docker/docker-compose.prod.yml). Backups come from backup.sh.
#
#   sudo bash infra/scripts/restore.sh list                 # show the local backups
#   sudo bash infra/scripts/restore.sh drill [BACKUP]       # TEST: restore into a scratch database, check it, drop it.
#                                                      # The shop keeps running and nothing live is touched. Monthly.
#   sudo bash infra/scripts/restore.sh full BACKUP          # DISASTER ONLY: replaces the live database and documents.
#
# BACKUP is a folder made by backup.sh, e.g. /var/backups/mechware/daily/2026-12-01 (drill: newest daily by default).
# A backup from the off-site remote is first copied back with rclone (docs/DEPLOY_RUNBOOK.md step 17).
# The restored data is only readable with the SAME ENCRYPTION_FIELD_KEY that was in prod.env when it was made.

set -Eeuo pipefail
umask 077

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MECHWARE_DIR="${MECHWARE_DIR:-$(cd "$SCRIPT_DIR/../.." && pwd)}"
COMPOSE_FILE="${COMPOSE_FILE:-$MECHWARE_DIR/infra/docker/docker-compose.prod.yml}"
ENV_FILE="${ENV_FILE:-$MECHWARE_DIR/infra/docker/prod.env}"
PROJECT="${COMPOSE_PROJECT_NAME:-mechware}"
DRILL_DB="mechware_restore_drill"

log() { printf '%s restore: %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*"; }
die() { printf '%s restore: FAILED: %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" >&2; exit 1; }
usage() { sed -n '2,13p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 2; }

[[ -f "$ENV_FILE" ]] || die "settings file not found: $ENV_FILE"
env_value() {
  local line value
  line="$(grep -E "^$1=" "$ENV_FILE" | tail -n 1 || true)"
  value="${line#*=}"
  if [[ ${#value} -ge 2 && ( ( "${value:0:1}" == "'" && "${value: -1}" == "'" ) || ( "${value:0:1}" == '"' && "${value: -1}" == '"' ) ) ]]; then
    value="${value:1:${#value}-2}"
  fi
  printf '%s' "$value"
}
BACKUP_DIR="${BACKUP_DIR:-$(env_value BACKUP_DIR)}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/mechware}"

compose() { docker compose -p "$PROJECT" -f "$COMPOSE_FILE" --env-file "$ENV_FILE" "$@"; }
psql_in() { local db="$1"; shift; compose exec -T postgres psql -U mechware -d "$db" -At -v ON_ERROR_STOP=1 -c "$1"; }
manifest() { grep -E "^$2=" "$1/manifest.txt" | head -n 1 | cut -d= -f2- || true; }

# Checks the folder and its checksums; prints nothing on success.
check_backup() {
  local dir="$1"
  [[ -f "$dir/db.dump" && -f "$dir/documents.tar.gz" && -f "$dir/SHA256SUMS" && -f "$dir/manifest.txt" ]] \
    || die "$dir is not a complete backup (needs db.dump, documents.tar.gz, SHA256SUMS, manifest.txt)"
  (cd "$dir" && sha256sum -c --quiet SHA256SUMS) || die "checksums do not match: the backup in $dir is damaged"
  tar -tzf - < "$dir/documents.tar.gz" > /dev/null || die "the documents archive in $dir is damaged"
}

require_postgres() {
  [[ -n "$(compose ps -q postgres)" ]] || die "the postgres container is not running: docker compose ... up -d postgres"
}

newest_daily() { find "$BACKUP_DIR/daily" -mindepth 1 -maxdepth 1 -type d -name '20*' 2>/dev/null | sort | tail -n 1; }

cmd_list() {
  local kind dir
  for kind in daily weekly; do
    echo "== $kind ($BACKUP_DIR/$kind)"
    find "$BACKUP_DIR/$kind" -mindepth 1 -maxdepth 1 -type d -name '20*' 2>/dev/null | sort | while read -r dir; do
      printf '  %s  %s  migration %s\n' "$dir" "$(du -sh "$dir" | cut -f1)" "$(manifest "$dir" last_migration)"
    done
  done
}

cmd_drill() {
  local dir="${1:-$(newest_daily)}"
  [[ -n "$dir" ]] || die "no backup found in $BACKUP_DIR/daily"
  dir="$(cd "$dir" && pwd)"
  log "drill with $dir"
  check_backup "$dir"
  require_postgres
  trap 'compose exec -T postgres dropdb -U mechware --if-exists --force "$DRILL_DB" >/dev/null 2>&1 || true' EXIT

  compose exec -T postgres dropdb -U mechware --if-exists --force "$DRILL_DB"
  compose exec -T postgres createdb -U mechware "$DRILL_DB"
  local started=$SECONDS
  compose exec -T postgres pg_restore -U mechware -d "$DRILL_DB" --exit-on-error --no-owner < "$dir/db.dump" \
    || die "pg_restore into the scratch database failed"
  local seconds=$((SECONDS - started))

  local tables migration want_tables want_migration users docs
  tables="$(psql_in "$DRILL_DB" "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'")"
  migration="$(psql_in "$DRILL_DB" 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1')"
  users="$(psql_in "$DRILL_DB" 'SELECT count(*) FROM users' 2>/dev/null || echo '?')"
  want_tables="$(manifest "$dir" table_count)"
  want_migration="$(manifest "$dir" last_migration)"
  docs="$(tar -tzf - < "$dir/documents.tar.gz" | grep -vc '/$' || true)"

  [[ "$tables" == "$want_tables" ]] || die "restored $tables tables, the backup says $want_tables"
  [[ "$migration" == "$want_migration" ]] || die "restored migration $migration, the backup says $want_migration"
  [[ "$docs" == "$(manifest "$dir" document_files)" ]] || die "documents archive has $docs files, the backup says $(manifest "$dir" document_files)"

  compose exec -T postgres dropdb -U mechware --force "$DRILL_DB"
  trap - EXIT
  log "DRILL PASSED: $tables tables, last migration $migration, $users user account(s), $docs document file(s);"
  log "database restore took ${seconds}s. Scratch database $DRILL_DB dropped; the live shop was not touched."
}

cmd_full() {
  local dir="${1:-}"
  [[ -n "$dir" ]] || usage
  dir="$(cd "$dir" && pwd)"
  check_backup "$dir"
  require_postgres
  local pg_image ts
  pg_image="$(docker inspect --format '{{.Config.Image}}' "$(compose ps -q postgres)")"

  cat <<EOF

  FULL RESTORE from $dir
  (made $(manifest "$dir" created), app commit $(manifest "$dir" app_commit), migration $(manifest "$dir" last_migration))

  This REPLACES the live database and all uploaded documents with that backup.
  Everything written after the backup was made (orders, invoices, uploads) is LOST.
  The shop is offline while it runs. A safety copy of the current database is saved first.

EOF
  local answer
  read -r -p "  Type RESTORE to continue: " answer
  [[ "$answer" == "RESTORE" ]] || die "cancelled, nothing was changed"

  ts="$(date +%Y%m%d-%H%M%S)"
  mkdir -p "$BACKUP_DIR/pre-restore"
  log "stopping caddy, web, console, api and worker"
  compose stop caddy web console worker api

  log "safety copy of the current database -> $BACKUP_DIR/pre-restore/db-$ts.dump"
  if ! compose exec -T postgres pg_dump -U mechware -d mechware -Fc > "$BACKUP_DIR/pre-restore/db-$ts.dump"; then
    log "no safety copy (the current database is missing or broken); continuing"
    rm -f "$BACKUP_DIR/pre-restore/db-$ts.dump"
  fi

  log "replacing the database"
  compose exec -T postgres dropdb -U mechware --if-exists --force mechware
  compose exec -T postgres createdb -U mechware -O mechware mechware
  compose exec -T postgres pg_restore -U mechware -d mechware --exit-on-error < "$dir/db.dump" \
    || die "pg_restore failed; the safety copy is in $BACKUP_DIR/pre-restore (restore it the same way)"

  log "replacing the documents volume"
  docker run --rm -i --network none -v "${PROJECT}_documents:/data" "$pg_image" \
    sh -c 'find /data -mindepth 1 -delete && tar -C /data --numeric-owner -xzpf -' < "$dir/documents.tar.gz" \
    || die "restoring the documents failed"

  log "starting the stack (migrate runs first and applies any newer migrations)"
  compose up -d
  log "FULL RESTORE DONE. Next: check the health URLs (runbook step 13), then rebuild the search index:"
  log "  docker compose -p $PROJECT -f '$COMPOSE_FILE' --env-file '$ENV_FILE' exec worker dotnet Mechware.Worker.dll rebuild-search"
  log "Old safety copies in $BACKUP_DIR/pre-restore are not deleted automatically: remove them when no longer needed."
}

case "${1:-}" in
  list) cmd_list ;;
  drill) cmd_drill "${2:-}" ;;
  full) cmd_full "${2:-}" ;;
  *) usage ;;
esac
