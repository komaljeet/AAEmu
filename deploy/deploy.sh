#!/usr/bin/env bash
# One-shot VPS deploy for the AAEmu stack.
#
#   bash deploy/deploy.sh
#
# Does, in order:
#   1. Generates .env (strong DB_PASSWORD + SECRET_KEY, best-effort PUBLIC_HOST)
#      by calling setup.sh — idempotent, only fills placeholders.
#   2. Sanity-checks the two gitignored client assets the game server needs at
#      boot (game_pak, compact.sqlite3). These can't be fetched from here — you
#      must upload them to the VPS first (see deploy/README.md). Warns + waits if
#      missing so you don't waste a multi-minute build on a boot that will fail.
#   3. Builds + starts the whole stack: db -> sidecar-init -> sidecar -> login
#      -> game.   (docker compose up -d --build)
#   4. Prints `docker compose ps` and how to watch the game server load the pak.
#
# On a small VPS (8 GB / 2 vCPU) the parallel Rust + .NET builds can OOM. If the
# build step dies, re-run with serial builds:
#   COMPOSE_BUILD_PARALLELISM=1 bash deploy/deploy.sh
set -euo pipefail

# Run from the repo root regardless of where the user invoked us.
cd "$(dirname "$0")/.."

echo "==> 1/4  Generating .env (secrets + PUBLIC_HOST)..."
bash deploy/setup.sh

echo
echo "==> 2/4  Checking staged client assets..."
missing=()
[ -f AAEmu.Game/ClientData/game_pak ] || missing+=("AAEmu.Game/ClientData/game_pak  (~24 GB)")
[ -f AAEmu.Game/Data/compact.sqlite3 ] || missing+=("AAEmu.Game/Data/compact.sqlite3")
if [ ${#missing[@]} -gt 0 ]; then
    echo "  WARNING — missing (the game server will fail to boot without these):"
    for f in "${missing[@]}"; do
        echo "    - $f"
    done
    echo "  Upload them, then re-run. See deploy/README.md."
    echo "  Proceeding anyway in 10s (Ctrl-C to abort)..."
    sleep 10
else
    echo "  game_pak + compact.sqlite3 present."
fi

echo
echo "==> 3/4  Building + starting the stack (docker compose up -d --build)..."
echo "       (first build takes several minutes: Rust release + two .NET publishes)"
docker compose up -d --build

echo
echo "==> 4/4  Stack status:"
docker compose ps

cat <<EOF

=== Next ===
Watch the game server load the ~24 GB pak (takes a few min before port 1239 opens):
    docker compose logs -f game

Point the AAEmu client launcher at \$(grep ^PUBLIC_HOST= .env | cut -d= -f2) and connect.

Stop:        docker compose down
Wipe data:   docker compose down -v
DB admin UI: docker compose --profile admin up adminer   (then ssh -L 8080:127.0.0.1:8080 vps)
EOF