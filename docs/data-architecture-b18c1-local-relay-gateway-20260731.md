# B18C1 local relay gateway

Status: completed and verified on 2026-07-31

## Outcome

B18C1 adds a separate, opt-in process mode:

```text
Godswar.Server --relay-gateway <configPath>
```

It proves that the unchanged original client's raw login and game TCP
connections can cross a process boundary to one private, existing combined
authoritative worker:

```text
original client
   | raw login/game TCP
   v
B18C1 local relay process
   | opaque TCP, private/loopback upstream
   v
existing combined Godswar.Server worker
   - login/authentication and game sessions
   - packet handlers and all map/instance owners
   - B18B mailboxes
   - PostgreSQL or local JSON provider
```

Normal server startup remains the default. The relay mode is selected only
when `--relay-gateway` is the first argument and exactly one configuration
path follows it. The relay and worker are separate operating-system
processes, but B18C1 is deliberately a local/raw-development topology proof,
not a production gateway.

## Implementation inventory

The relay implementation is isolated under
`src/Godswar.Server/Networking/RelayGateway`:

| Repository path | Principal symbols and responsibility |
| --- | --- |
| `src/Godswar.Server/Networking/RelayGateway/RelayGatewayCommand.cs` | `RelayGatewayCommand`, `Mode`, and `TryRunAsync`; selects `--relay-gateway <configPath>` before normal worker composition |
| `src/Godswar.Server/Networking/RelayGateway/RelayGatewayOptions.cs` | `RelayGatewayOptions`, `RelayGatewayEndpointOptions`, and `RelayGatewayRuntimeLimitOptions`; bounded JSON loading, validation, DNS resolution, and loopback/private-upstream enforcement |
| `src/Godswar.Server/Networking/RelayGateway/RelayGatewayModels.cs` | Finite endpoint, state, readiness, worker-availability, outcome, configuration, limits, started-endpoint, and snapshot models |
| `src/Godswar.Server/Networking/RelayGateway/RelayGatewayServer.cs` | `RelayGatewayServer`; two listeners, one global connection cap, tracked connection lifetime, listener failure handling, and bounded drain |
| `src/Godswar.Server/Networking/RelayGateway/RelayGatewayConnection.cs` | `RelayGatewayTrackedConnection` and `RelayGatewayConnection`; private-worker connect, two opaque byte pumps, pooled fixed buffers, deadlines, half-close, and socket cleanup |
| `src/Godswar.Server/Networking/RelayGateway/RelayGatewayMetrics.cs` | `RelayGatewayMetrics`, meter `Godswar.Server.RelayGateway`, finite counters/gauges, and `RelayGatewaySnapshot` accounting |

`src/Godswar.Server/Program.cs` calls
`RelayGatewayCommand.TryRunAsync(args)` before loading normal
`ServerOptions`. `appsettings.relay-gateway.json` is the checked-in local
example for distinct relay bind and private worker upstream ports.

The existing combined worker gains only the configuration needed to make
the local split unambiguous:

- `src/Godswar.Server/WorldInstanceRuntimeOptions.cs` exposes validated
  `ServerNodeId` and typed `ProcessServerNodeId`;
- `src/Godswar.Server/ServerOptions.WorldInstances.cs` applies
  `GODSWAR_WORLD_INSTANCE_SERVER_NODE_ID`;
- `src/Godswar.Server/ServerOptions.cs` exposes the raw advertised game
  `GameEndpointOptions.PublicPort`, its
  `ResolvePublicPort()` compatibility fallback, and
  `GODSWAR_GAME_PUBLIC_PORT`; and
- `src/Godswar.Server/Game/LoginClientHandler.cs` puts that public port in
  the original client's game-server redirect instead of assuming that the
  worker's private listener port is public.

This configuration does not make the relay a placement authority. The
configured worker `ServerNodeId` names the existing local B18A/B runtime
owner, while `PublicPort` lets a private worker advertise the relay's raw
game port.

The Docker-free real-process acceptance harness is:

| Repository path | Principal symbols and responsibility |
| --- | --- |
| `tools/Godswar.Server.B18CSmoke/Godswar.Server.B18CSmoke.csproj` | Executable smoke project referencing the server and protocol |
| `tools/Godswar.Server.B18CSmoke/Program.cs` | `Program.Main` and the bounded child-process lifecycle |
| `tools/Godswar.Server.B18CSmoke/TwoProcessSmokeProtocol.cs` | `TwoProcessSmokeProtocol`; encrypted login/game rounds and active-connection failure proof |
| `tools/Godswar.Server.B18CSmoke/SmokeWorkspace.cs` | `SmokeWorkspace` and `SmokeEndpoints`; validated temporary workspace, ephemeral ports, relay/worker JSON, and clean child environment |
| `tools/Godswar.Server.B18CSmoke/ManagedChildProcess.cs` | `ManagedChildProcess`; real `dotnet` child launch, bounded log tail, deadline-aware exit, and process-tree cleanup |
| `tools/Godswar.Server.B18CSmoke/LegacySmokePeer.cs` | `LegacySmokePeer`; stateful legacy `PacketCipher` TCP peer |
| `tools/Godswar.Server.B18CSmoke/SmokePackets.cs` | `SmokePackets`; bounded login, game-login, and opcode packet construction |
| `tools/InvokeB18CTwoProcessSmoke.ps1` | Repository wrapper that builds Release by default and passes exact `--server-dll` and `--dotnet-host` paths |

The in-process acceptance entry is the partial `RelayGatewayChecks` in
`tests/Godswar.Server.ProtocolChecks/RelayGatewayChecks.cs` and
`tests/Godswar.Server.ProtocolChecks/RelayGatewayChecks.Runtime.cs`, registered
by `DataArchitectureCheckCatalog`.

## Bounded transport behavior

The relay treats both protocols as bytes. It does not parse frames, retain
messages, or create an application queue. Each admitted connection:

1. connects to the one configured private worker endpoint within the
   configured connect timeout;
2. rents exactly one fixed-size buffer for each direction;
3. copies bytes with a write deadline and a shared idle deadline;
4. propagates end-of-stream with a best-effort TCP send half-close;
5. clears and returns both buffers; and
6. remains registered until its task and sockets have settled.

Login and game share one `MaximumConnections` admission cap. Configuration
also bounds backlog, buffer size, connect/idle/write/drain timeouts, and the
aggregate application-buffer reservation. Upstream DNS is resolved during
validation and every answer must be loopback, RFC1918, or IPv6 unique-local.
Listener/upstream collisions that could form a local relay loop are rejected.

Ctrl+C and POSIX SIGTERM feed the existing
`ServerProcessSignalRegistration` into the same bounded shutdown path.
Shutdown stops both listeners, marks the gateway draining, waits a finite
time for tracked connections, then cancels and disconnects remaining
connections. Readiness, active/capacity, accepted/rejected, connection
outcomes, worker-connect failures, and bytes in both directions use finite
state or endpoint-role labels; player, address, and connection identifiers
are not metric labels.

These signals currently exist as an in-memory `RelayGatewaySnapshot` plus
`.NET Meter` instruments. Relay mode returns before
`ServerObservabilityRuntime` composition, so B18C1 does not expose the
worker's management endpoint or compose a metrics exporter. External relay
health/metrics integration is a later operational/semantic-gateway gap.

## Verification and acceptance

Automated in-process checks must cover at least:

- strict command/config parsing, bound validation, private-upstream
  validation, DNS results, endpoint collisions, and aggregate buffer limits;
- opaque bidirectional byte preservation, large segmented payloads,
  half-close in each direction, abrupt peer failure, and worker-unavailable
  behavior;
- one global login/game cap, overload rejection, deadline outcomes,
  readiness transitions, finite metrics, and bounded drain; and
- worker `ServerNodeId`, raw advertised `PublicPort`, fallback behavior, and
  the legacy redirect packet.

The Docker-free smoke must start two real processes: one normal combined
worker on private loopback login/game ports and one
`--relay-gateway <configPath>` process on distinct client-facing loopback
ports. It must drive real login and game traffic through the relay, prove the
worker-advertised game endpoint points back to the relay, verify byte/session
continuity, and shut both children down within finite deadlines.

The carrying tree passed:

- `dotnet build GodswarServer.sln --configuration Release --nologo` with
  zero warnings and zero errors;
- the focused B18C1 relay check (`1/1`) plus the worker configuration,
  advertised-port, and legacy transport checks (`3/3`);
- the complete managed protocol catalog (`275/275`);
- `tools/InvokeB18CTwoProcessSmoke.ps1`, with eight pass records covering
  unavailable-worker behavior, worker readiness, encrypted login/select,
  public relay redirect, game bootstrap, active-connection teardown, worker
  restart, and stable relay PID; and
- the mandatory disposable PostgreSQL gate: 43 required checks and five
  migration scenarios, with successful cleanup and no leftover
  `godswar_b03_*` databases.

The PostgreSQL gate's machine-readable local result is written under the
ignored `artifacts/b03` directory. B18C1 adds no migration; that gate proves
the process split did not regress the existing durable authority.

Secure UDP is not part of B18C1 acceptance. If it is experimented with, it
remains direct-to-worker; this relay neither forwards nor terminates it.

## Explicit non-claims

B18C1 does **not**:

- terminate TLS, authenticate a client, consume a ticket, or interpret any
  login/game packet;
- route by `RealmId`, `WorldInstanceId`, character, account, capacity, or
  admission result;
- share tickets, sessions, presence, placement, ownership, or reconnect
  state;
- relay secure UDP or preserve the original client source IP at the worker;
- coordinate multiple workers or provide worker selection, health-based
  failover, cross-worker transfer, or cross-process reconnect;
- schedule battlefield/dungeon instances, perform cross-realm admission, or
  settle results/rewards;
- move networking sessions, game handlers, maps, or B18B owner mailboxes out
  of the existing combined worker;
- change PostgreSQL/JSON authority or introduce a database migration; or
- add Redis packages, configuration, deployment, leases, routes, or
  presence.

Two local processes therefore do not constitute production failure
isolation, a secure edge, distributed simulation, or proof that the original
client can move between workers.

## Rollback and next milestone

Rollback is to omit `--relay-gateway`, restore the worker's directly
advertised raw game port, and run the existing combined server exactly as
before. No durable data conversion is involved.

B18C2 is next. It must introduce the semantic gateway/session-authority
backhaul, a stable gateway connection identity, routing by
`WorldInstanceId`, and explicit admission/source identity. Only after that
real shared coordination boundary exists, and its latency, outage,
staleness, capacity, provider/region, and cost budgets are recorded, should
B17 add Redis-backed tickets, routes, presence, and PostgreSQL-fenced leases.
