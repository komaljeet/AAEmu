# Deploy on a Linux VPS (one command)

The whole stack — MySQL, the `aaemu-custom` Rust sidecar, the AAEmu Login server, and the
AAEmu Game server — is defined in `docker-compose.yaml` and brought up with a single
`docker compose up -d --build`. Config is injected via environment variables (no `.server_files`,
no `sed` install script). This is the Linux deploy path; `Start-AAEmu.ps1` remains the Windows
dev path.

## Requirements

- A Linux VPS with Docker + the `docker compose` plugin. 8 GB RAM / 2 vCPU is the minimum
  (the Game server loads a ~24 GB client pak into memory at boot); 16 GB / 4 vCPU is comfortable.
- ~30 GB free disk for the client assets + DB + build artifacts.
- The two client-extracted assets, **not in the repo** (gitignored + dockerignored, so they're
  never baked into an image): `game_pak` (~24 GB) and `compact.sqlite3`.

## Steps

```sh
git clone <your-repo> ArcheAge && cd ArcheAge

# 1. Stage the two client assets first (they mount as volumes, not in the image):
scp game_pak         vps:ArcheAge/AAEmu.Game/ClientData/game_pak
scp compact.sqlite3  vps:ArcheAge/AAEmu.Game/Data/compact.sqlite3

# 2. Open the firewall for client-facing ports only: 1237, 1239, 1250.
#    (3306 MySQL is NOT published by the base compose — only the Windows dev
#    override docker-compose.dev.yaml publishes it, and the VPS deploy doesn't
#    load that override. 1281 sidecar + 1234 login-internal also stay internal.)

# 3. One-shot deploy: generate .env, sanity-check the assets, build + start the stack.
bash deploy/deploy.sh
```

`deploy.sh` calls `setup.sh` (which generates `.env` — strong `DB_PASSWORD` + `SECRET_KEY`,
best-effort `PUBLIC_HOST`; idempotent, only fills placeholders), checks `game_pak` and
`compact.sqlite3` are present, then runs `docker compose up -d --build` and prints the status.
You can also run the pieces manually: `bash deploy/setup.sh` then `docker compose up -d --build`.

## What `up` does, in order

1. **`db`** — MySQL 8.0.36, applies `SQL/aaemu_login.sql` + `SQL/aaemu_game.sql` on first boot
   (both `CREATE DATABASE IF NOT EXISTS`). Data lives in the bind mount
   `.server_files/AAEmu.Database/mysql` (created empty on a fresh VPS). `3306` is **not**
   published.
2. **`sidecar-init`** (one-shot) — runs `aaemu-custom --init-db`: applies the sidecar schema,
   bootstraps the world bank, and seeds vehicle/mount defaults. Idempotent, exits 0.
3. **`sidecar`** — the Rust HTTP API on `:1281` + the scheduler loops (hourly integrity, boss
   tick, labor regen, daily tax). Reuses the image built by `sidecar-init`. Not published — the
   Game container calls it as `http://sidecar:1281` over the compose network.
4. **`login`** — AAEmu Login on `:1237` (clients). Hands clients `${PUBLIC_HOST}:1239` for the
   game server.
5. **`game`** — AAEmu Game on `:1239` / `:1250` (clients). Loads `game_pak` at boot (give it a
   few minutes; the healthcheck has a 10-minute start period). The `AaemuCustom` integration is
   enabled via env (`AaemuCustom__Enabled=true`, `AaemuCustom__BaseUrl=http://sidecar:1281`).

## Config injection (how it works without mounted config files)

`AAEmu.Game/Program.cs` (`BuildConfiguration`) loads the baked-in `Config.json` (which still has
`%db_host%` placeholders) then `AddEnvironmentVariables()` last, and binds the whole root into
`AppConfiguration`. So compose env vars override the placeholders:

- `Connections__MySQLProvider__Host=db` (etc.) → DB connection.
- `LoginNetwork__Host=login` / `LoginNetwork__Port=1234` → Game→Login internal link.
- `SecretKey=${SECRET_KEY}` → shared Login/Game inter-server secret.
- `GameServers__0__Host=${PUBLIC_HOST}` → the address Login advertises to clients.
- `AaemuCustom__Enabled=true` / `AaemuCustom__BaseUrl=http://sidecar:1281` → sidecar integration.

The sidecar has no env-config support (its `config.toml` carries the full economy tuning), so its
`docker-entrypoint.sh` generates `/tmp/config.toml` from the in-image `config.example.toml` +
the `SIDECAR_DB_URL` / `SIDECAR_LISTEN` env vars, then execs the binary. No secrets file on the
host.

## Operations

```sh
docker compose ps                         # db healthy, sidecar-init exited 0, rest up
docker compose logs -f game               # tail game server (watch for the pak load)
docker compose down                       # stop (data preserved)
docker compose down -v                    # stop + wipe the MySQL data dir
docker compose --profile admin up adminer # opt-in DB UI on 127.0.0.1:8080 (SSH tunnel)
```

## Verification

- `docker compose exec db mysql -uroot -p"$DB_PASSWORD" -e "USE aaemu_game; SHOW TABLES LIKE 'world_bank';"`
  → sidecar tables exist (proves `--init-db` ran).
- `docker compose exec game wget -qO- http://sidecar:1281/health` → `ok` (sidecar reachable from
  the game container; integration enabled).
- `docker compose logs game | grep -i mysql` → no connection failure (env DB override worked).
- Point the client launcher at `${PUBLIC_HOST}`; Login (1237) hands the client `${PUBLIC_HOST}:1239`,
  and the client connects to Game (1239/1250).

## Notes / troubleshooting

- **Small VPS OOM during build:** the Rust release build + two .NET Release builds in parallel
  can exceed 8 GB. Build serially: `COMPOSE_BUILD_PARALLELISM=1 docker compose build` then
  `docker compose up -d`. Builds only happen once; later `up` reuses the images.
- **`sidecar-init` re-runs on every `up`** — safe by design (`schema.sql` is `CREATE TABLE IF NOT
  EXISTS` and `world_bank` init is idempotent; the HTTP `POST /init-db` path is documented as
  re-runnable).
- **`PUBLIC_HOST` must be the public address**, not `127.0.0.1` — otherwise remote clients are
  told to connect to themselves. `setup.sh` auto-detects it via `api.ipify.org`; edit `.env` to
  change it (e.g. to a domain).
- **`Scripts/docker-install-local.sh` is superseded** by this compose + `deploy/setup.sh` for
  VPS deploys. It remains for the upstream `.server_files`/`sed` workflow if anyone still uses it.
- **Only 1237/1239/1250 are public.** Never expose 3306 (MySQL) or 1281 (sidecar) to the internet.
- **3306 publish is Windows-dev-only.** The base `docker-compose.yaml` does NOT publish 3306
  (VPS-safe: compose services use `db:3306` internally). `docker-compose.dev.yaml` publishes it
  for the Windows dev path (`Start-AAEmu.ps1` loads it via `-f` so bare-dotnet Login/Game and the
  standalone sidecar can reach MySQL on the host). The VPS deploy (`deploy/deploy.sh`) runs the
  base file only, so MySQL is never internet-exposed — no firewalling of 3306 needed on the VPS.