#!/usr/bin/env bash
# One-time VPS bootstrap for the AAEmu stack.
#
# Generates .env from .env.example with strong secrets (DB_PASSWORD, SECRET_KEY)
# and a best-effort PUBLIC_HOST (the VPS public IP), then prints the remaining
# manual steps (stage the two client assets, open firewall ports, bring the
# stack up). Safe to re-run: it only fills placeholder values and leaves already-
# set real values alone.
#
# Usage:  bash deploy/setup.sh
set -euo pipefail

# Run from the repo root regardless of where the user invoked us.
cd "$(dirname "$0")/.."

ENV_FILE=".env"
EXAMPLE=".env.example"

if [ ! -f "$EXAMPLE" ]; then
    echo "error: $EXAMPLE not found — run this from the repo root." >&2
    exit 1
fi

# 1. Create .env from the example if it doesn't exist (don't clobber an existing one).
if [ ! -f "$ENV_FILE" ]; then
    cp "$EXAMPLE" "$ENV_FILE"
    echo "created .env from .env.example"
fi

# Helper: set KEY=value in .env (replace the line if present, append if absent).
set_env() {
    local key="$1" val="$2"
    # Escape forward slashes in the value for the sed replacement.
    local escaped
    escaped=$(printf '%s' "$val" | sed 's/[\/&]/\\&/g')
    if grep -qE "^${key}=" "$ENV_FILE"; then
        sed -i "s|^${key}=.*|${key}=${escaped}|" "$ENV_FILE"
    else
        printf '%s=%s\n' "$key" "$val" >> "$ENV_FILE"
    fi
}

# Helper: read the current value of KEY from .env (empty if absent).
get_env() {
    grep -E "^${1}=" "$ENV_FILE" 2>/dev/null | head -1 | cut -d= -f2- || true
}

# 2. DB_PASSWORD — generate if it's still the placeholder.
DB_PASSWORD=$(get_env DB_PASSWORD)
if [ -z "$DB_PASSWORD" ] || [ "$DB_PASSWORD" = "password" ]; then
    DB_PASSWORD=$(openssl rand -base64 32 | tr -d '/+=' | head -c 32)
    set_env DB_PASSWORD "$DB_PASSWORD"
    echo "generated DB_PASSWORD"
else
    echo "DB_PASSWORD already set — keeping it"
fi

# 3. SECRET_KEY — generate if it's still the placeholder.
SECRET_KEY=$(get_env SECRET_KEY)
if [ -z "$SECRET_KEY" ] || [ "$SECRET_KEY" = "change-me" ]; then
    SECRET_KEY=$(openssl rand -base64 32 | tr -d '/+=' | head -c 32)
    set_env SECRET_KEY "$SECRET_KEY"
    echo "generated SECRET_KEY"
else
    echo "SECRET_KEY already set — keeping it"
fi

# 4. PUBLIC_HOST — the VPS public IP/domain clients use to reach the game server.
PUBLIC_HOST=$(get_env PUBLIC_HOST)
if [ -z "$PUBLIC_HOST" ] || [ "$PUBLIC_HOST" = "127.0.0.1" ]; then
    detected=""
    if command -v curl >/dev/null 2>&1; then
        detected=$(curl -s --max-time 5 https://api.ipify.org 2>/dev/null || true)
    fi
    echo
    echo "PUBLIC_HOST is the public address clients use to connect (Login hands it"
    echo "to clients, so it must be externally reachable — NOT 127.0.0.1)."
    if [ -n "$detected" ]; then
        read -r -p "Detected public IP: $detected. Use this? [Y]/n " ans
        ans=${ans:-Y}
        if [ "${ans#y}" != "$ans" ] || [ "${ans#Y}" != "$ans" ]; then
            PUBLIC_HOST="$detected"
        else
            read -r -p "Enter PUBLIC_HOST (IP or domain): " PUBLIC_HOST
        fi
    else
        read -r -p "Could not auto-detect public IP. Enter PUBLIC_HOST (IP or domain): " PUBLIC_HOST
    fi
    if [ -z "$PUBLIC_HOST" ]; then
        echo "warning: empty PUBLIC_HOST — set it in .env before bringing the stack up."
    else
        set_env PUBLIC_HOST "$PUBLIC_HOST"
        echo "set PUBLIC_HOST=$PUBLIC_HOST"
    fi
else
    echo "PUBLIC_HOST already set — keeping it"
fi

# 5. Reminders for the remaining manual steps.
cat <<EOF

=== .env ready ===
DB_PASSWORD:  $(get_env DB_PASSWORD | sed 's/./*/g')
SECRET_KEY:   $(get_env SECRET_KEY | sed 's/./*/g')
PUBLIC_HOST:  $(get_env PUBLIC_HOST)

=== Remaining steps ===

1. Stage the two gitignored client-extracted assets (not in the repo, not in any
   image — they mount as volumes):
     game_pak  (~24 GB)  -> AAEmu.Game/ClientData/game_pak
     compact.sqlite3      -> AAEmu.Game/Data/compact.sqlite3
   e.g. from your machine:
     scp game_pak         vps:ArcheAge/AAEmu.Game/ClientData/game_pak
     scp compact.sqlite3  vps:ArcheAge/AAEmu.Game/Data/compact.sqlite3

2. Open the firewall for the client-facing ports only:
     1237/tcp  (login)
     1239/tcp  (game)
     1250/tcp  (game stream)
   (3306, 1281, 1234 stay internal to the compose network — do NOT expose them.)

3. Build + start the whole stack:
     docker compose up -d --build
   On a small VPS (8 GB / 2 vCPU) the parallel Rust + .NET builds can OOM; if so:
     COMPOSE_BUILD_PARALLELISM=1 docker compose build
     docker compose up -d

4. Check it came up:
     docker compose ps
     docker compose logs -f game

5. Point the AAEmu client launcher at ${PUBLIC_HOST:-<PUBLIC_HOST>} and connect.

To stop:      docker compose down
To wipe data: docker compose down -v   (also removes the MySQL data dir bind mount)
EOF