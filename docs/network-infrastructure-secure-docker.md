# Secure hybrid server in Docker Desktop

## Purpose and boundary

`docker-compose.secure.yml` replaces the base Compose `server` service with
the secure TLS plus authenticated-UDP profile. It uses the same Compose
project, service name, and `godswar-server` container identity, so the raw and
secure game-server profiles are never started as two competing containers.
PostgreSQL remains the existing durable Compose service and volume.

The secure server is attached only to the `secure-runtime` bridge. PostgreSQL
joins both that bridge and the base project bridge, so the server's fixed
`172.31.250.10` address is its sole container interface and the `postgres`
service remains reachable by name. This is required for Docker's published
loopback ports to target the same interface that owns the TLS and UDP
listeners; attaching the server to the implicit default network as well would
make Docker forward through a different primary address.

Docker Desktop supplies useful process, port, health, and log visibility. The
container healthcheck proves that both TLS listeners and the UDP listener are
bound. It is not a substitute for the client TLS, ticket-binding, UDP
authentication, or gameplay acceptance tests.

The secure host exposure is exactly:

| Host endpoint | Container endpoint | Transport |
| --- | --- | --- |
| `127.0.0.1:6599` | `172.31.250.10:6599` | TLS/TCP login |
| `127.0.0.1:7443` | `172.31.250.10:7443` | TLS/TCP game |
| `127.0.0.1:7444` | `172.31.250.10:7444` | Authenticated UDP |

Raw `5999` and `7000` are not published by this profile. The synthetic game
route still carries port `7000` inside the authenticated protocol, but the
native shim resolves it to the signed TLS endpoint; it is not a raw external
listener.

## One-time local preparation

Copy the tracked template:

```powershell
Copy-Item .env.secure.example .env.secure.local
```

Edit the untracked copy and set:

- `GODSWAR_SECURE_CERTIFICATE_HOST_PATH` to the current server PFX;
- `GODSWAR_SECURE_CERTIFICATE_PASSWORD_HOST_PATH` to a host file containing
  only the PFX password and, optionally, one final newline;
- `GODSWAR_SECURE_POSTGRES_DB` to a durable existing database;
- the Docker subnet and fixed address if `172.31.250.0/24` overlaps a VPN or
  another Docker network.

The default database is `godswar` in the existing named PostgreSQL volume.
Routine development must not use a disposable
`godswar_secure_acceptance_*` database.

The password host file is a local Docker Compose secret source. Keep it
outside the repository, restrict its Windows ACL to the current operator and
administrators required by Docker Desktop, and never commit or print it. The
Linux container receives it read-only at
`/run/secrets/reborn-secure-certificate-password`; the password is not placed
in the container environment or Docker inspection output. The PFX is also
mounted read-only.

Docker Desktop for Windows may warn that Compose secret `uid`, `gid`, and
`mode` are ignored. Its 9P mount can also report synthetic `0777` mode bits.
The required property is a `ro` mount in `/proc/mounts`; the Windows source
files must still have a protected ACL because their host permissions are the
actual confidentiality boundary.

The client-side development root remains in the Windows trust store, and the
hosts file must retain:

```text
127.0.0.1 login.reborn.test game.reborn.test
```

The signed endpoint manifest remains unchanged because the published host
ports and DNS identities remain `6599`, `7443`, and `7444`.

## Start and inspect

Stop the standalone secure server before starting Docker because both use the
same three host ports. Do not stop PostgreSQL.

```powershell
docker compose `
  --env-file .env.secure.local `
  -f docker-compose.yml `
  -f docker-compose.secure.yml `
  --profile secure `
  up --build -d server

docker compose `
  --env-file .env.secure.local `
  -f docker-compose.yml `
  -f docker-compose.secure.yml `
  --profile secure `
  ps

docker logs --tail 100 godswar-server
docker inspect godswar-server --format '{{.State.Health.Status}}'
docker exec godswar-server sh -c `
  "grep '/run/secrets/reborn-secure' /proc/mounts"
```

Expected server output states that raw compatibility is disabled, reports TLS
login/game listeners, and reports secure UDP startup. Docker health should
become `healthy`.

The password-file setting fails closed:

- a directly supplied `GODSWAR_SECURE_CERTIFICATE_PASSWORD` takes precedence;
- otherwise `GODSWAR_SECURE_CERTIFICATE_PASSWORD_FILE` must be absolute,
  readable, strict UTF-8, nonempty, NUL-free, and at most 4,096 UTF-8 bytes;
- the reader removes at most one terminal `CRLF`, `CR`, or `LF`;
- invalid, missing, or oversized files stop startup.

## Stop or return to the raw development profile

Stop only the game-server service:

```powershell
docker compose `
  --env-file .env.secure.local `
  -f docker-compose.yml `
  -f docker-compose.secure.yml `
  --profile secure `
  stop server
```

To intentionally return to raw development, use the base file alone. Compose
recreates the same `godswar-server` identity with the legacy configuration:

```powershell
docker compose `
  -f docker-compose.yml `
  --profile legacy-raw `
  up --build -d server
```

Never run base and secure server commands concurrently.

## Verification

The static Compose contract test renders both profiles and verifies the exact
ports, sole fixed private server network, shared PostgreSQL network, secret
paths, durable database default, healthcheck, and absence of raw host mappings:

```powershell
powershell -NoProfile -File tools/TestSecureDockerProfile.ps1
```

Managed certificate-password and TLS checks:

```powershell
dotnet run --project tests/Godswar.Server.ProtocolChecks `
  --configuration Release -- "TLS mux"
```

Image build:

```powershell
docker compose `
  --env-file .env.secure.local `
  -f docker-compose.yml `
  -f docker-compose.secure.yml `
  --profile secure `
  build server
```

The bounded live smoke creates a random versioned account and character only
in `godswar_secure_dev`, validates the complete TLS/ticket/UDP flow, enters
the world, sends one authoritative movement input, requires its acknowledged
snapshot, waits for server teardown, and deletes the fixture:

```powershell
.\tools\InvokeSecureDockerSmoke.ps1 `
  -RootCertificatePath 'C:\private\reborn-development-root.cer'
```

The probe refuses non-loopback endpoints and any database other than
`godswar_secure_dev`; it has a 20-second default deadline and never prints its
random credential. Final Phase 4 acceptance first keeps this profile healthy
while the receipt-bound client campaign applies, then temporarily stops only
Docker `server`. The current Release runs Baseline, Fallback, and ten-minute
Soak profiles in the foreground against the same Docker PFX/password file and
`godswar_secure_dev`; PostgreSQL remains healthy. Fallback uses the one-shot
logical snapshot-ACK-drop campaign. Do not disable Norton, change Windows
Firewall, alter adapters/routes, or interrupt the machine's internet
connection. Restart and revalidate this exact secure-Docker profile before
the receipt-bound client/hosts/root campaign Restore.
