# Network infrastructure Phase 5A

## Status

Phase 5A is complete as a local, bounded verification baseline. It adds
deterministic movement replay, a targetless load/soak runner, expanded
realtime decoder fuzzing, live simulation-loop metrics, authoritative
operational gauges, and an operator gate.

This is not a production capacity claim. It does not select a hosting
provider, expose a public metrics endpoint, run an authorized DDoS exercise,
or complete the deferred map/AOI optimization.

## Scope and invariants

The slice preserves the Phase 4 transport and authority decisions:

- TLS remains the reliable control and fallback channel.
- Authenticated encrypted UDP remains the preferred movement channel.
- Clients submit movement intent; the server owns accepted position,
  revision, tick, and snapshot state.
- TCP and UDP ordering remain independent and are reconciled by transport
  epoch, input ID, simulation tick, snapshot sequence, and keyframes.
- Every new workload and state container has a hard upper bound.
- Test traffic cannot be aimed at another machine.

## Deterministic replay

The replay implementation is under
`src/Godswar.Server/Game/Simulation/Replay`.

Version 1 records semantic movement after authentication and transport
validation. It deliberately excludes credentials, tickets, cookies, keys,
addresses, and raw packet payloads. A trace contains:

- the authoritative movement baseline;
- immutable server-owned world facts;
- an ordered frame sequence at the exact 50 ms fixed step;
- either one eligible input or an explicit empty/loss frame per tick.

The hard limits are 24,000 frames and 12,000 inputs. Unsupported versions,
timeline overflow, excessive counts, and unsafe checkpoint boundaries fail
closed.

Replay results use a domain-separated, canonical little-endian SHA-256 hash
chain. Float values are hashed by their IEEE-754 bit representation. A
separate trace-identity hash binds the full ordered input trace, preventing a
checkpoint from being resumed against a different suffix. The comparison
helper reports the first divergent frame and field instead of only saying
that final hashes differ.

A checkpoint can be created only when the public movement baseline fully
represents the authority's hidden cadence state. A rejected input advances
observed-input state, so that boundary is refused until a later accepted input
restores a representable checkpoint. Tests prove that a safe checkpoint plus
suffix produces the same final hash and state as an uninterrupted run.

## Bounded load and soak runner

`tools/Godswar.Server.Phase5A` is a synthetic, in-process runner. It opens no
sockets and has no host, address, or target option. This makes the default
command safe to run without generating external traffic.

The runner creates one production `AuthoritativePlayerMovementSystem` per
bot. Each bot performs the production movement-input encode/decode,
authoritative decision, position-snapshot encode/decode, and ordered digest
append. Nine of every ten inputs use UDP semantics and one uses TLS fallback
semantics.

Safety limits:

| Limit | Value |
| --- | ---: |
| Default workload | 64 bots for 10 logical seconds |
| Fixed tick rate | 20 Hz |
| Maximum bots | 512 |
| Maximum requested duration | 300 seconds |
| Maximum operations in any run | 5,000,000 |
| Default operations | 76,800 |
| Retained percentile samples | 2,048 |
| External targets | none |

The operation cap is authoritative: combinations within the individual bot
and duration maxima are still rejected if their product exceeds 5,000,000.
Runtime consumption is checked again on every bot tick.

Modes:

- `load` executes the validated logical ticks as quickly as possible.
- `paced-soak` schedules them against the real monotonic clock at 20 Hz.
- `--self-check` proves option rejection, hard budgets, deterministic
  digests, packet accounting, and bounded percentile storage.

The JSON report includes OS/runtime/architecture, processor count, GC mode,
elapsed and CPU time, normalized CPU, allocations, working-set and handle
observations, GC collections, packet/byte totals, tick percentiles, pacing
misses, budget consumption, and the deterministic digest.

Elapsed time, CPU, allocation, and working-set observations begin before bot,
sampler, and digest construction, so they include runner setup. Tick
processing percentiles measure only each tick's bot work; they exclude setup
and paced-soak waiting.

The runner intentionally does not exercise TLS handshakes, AEAD, kernel
sockets, admission queues, database work, AOI fan-out, or load shedding.
Those boundaries remain covered by the secure protocol, UDP admission,
network-emulation, handler-integration, and controlled-host suites. A future
live multi-client runner must retain literal-loopback/allowlist controls and
hard traffic caps.

## Runtime observability

The new `Godswar.Server.Simulation` meter instruments five finite loop kinds:

- realtime movement;
- monster world;
- player recovery;
- EXP-boost reconciliation;
- Zodiac accrual.

It reports active loops, starts/stops, finite stop outcomes, tick count,
processing duration, schedule drift, missed boundaries, and heartbeat age.
Timing uses `Stopwatch`; it does not alter gameplay clocks or fixed-step
decisions. The realtime movement file gained only the small observation seam;
measurement logic lives in dedicated files.

Tick, duration, drift, missed-boundary, and heartbeat measurements are
committed only after the full awaited loop iteration succeeds. Cancellation
or a fault records its finite loop-stop outcome without reporting an aborted
iteration as a completed tick. Duration is wall time for that awaited
iteration, not process CPU time.

The instance-owned `Godswar.Server.OperationalState` meter reports current,
authoritative values for:

- active and unauthenticated admitted connections;
- occupied unauthenticated IP and prefix tables;
- outstanding ticket count and ticket capacity when secure TLS is enabled;
- UDP ready/faulted state;
- pending, bound, and maximum UDP sessions;
- global, unvalidated, binding-proof, and protected-candidate limiter
  occupancy;
- general, proof, and protected-candidate prefix-table occupancy;
- authenticated-session limiter occupancy.

Three grouped operational instruments emit up to 19 finite state series:
four admission states, two ticket states, and 13 UDP states. Each carries the
single compile-time-bounded `operational.state` dimension, and each family
takes one authoritative snapshot per collection. Simulation instruments use
only `simulation.loop` and `simulation.loop.stop_outcome`, whose value sets
are also compile-time finite. No account, character, session, connection,
ticket, IP address, map, or attacker-controlled string can become a metric
label. Optional ticket and UDP instrument families are absent when their
runtime is disabled rather than publishing misleading zero series.

The existing networking, secure transport, UDP, and authentication meters
remain unchanged. Phase 5A adds state gauges and simulation timing instead of
duplicating their event counters.

The repository still has no production exporter. Operators can consume the
standard .NET meters through a `MeterListener`, diagnostic tooling, or a
deployment-owned OpenTelemetry adapter. Selecting and exposing an exporter
requires a private management boundary and is a later activation task; it
must never share the public game listener.

## Decoder and overload gates

The Phase 5A decoder fuzz check uses:

- every truncated and extended length from 0 through 128 bytes;
- every single-bit mutation of valid 52-byte movement input and 64-byte
  snapshot packets;
- 20,000 seeded arbitrary byte strings across both decoders.

Successful random decodes are revalidated, and malformed inputs must return a
bounded rejection without throwing.

The regression gate also runs the existing deterministic latency, jitter,
loss, duplication, reordering, UDP-blocking, keyframe-recovery, MTU, and
capacity-one mailbox overload checks. The existing 16,000-attempt UDP
admission baseline proves fixed limiter capacity; the impairment suite proves
bounded overload recovery. These are separate from the synthetic throughput
number.

## Commands

Build and focused checks:

```powershell
dotnet restore GodswarServer.sln
dotnet build GodswarServer.sln --configuration Release --no-restore
dotnet tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/Godswar.Server.ProtocolChecks.dll
dotnet tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/Godswar.Server.ProtocolChecks.dll `
  "Secure Phase 5A" `
  "deterministic network emulation and overload" `
  "UDP bounded loopback baseline"
```

Direct runner:

```powershell
dotnet tools/Godswar.Server.Phase5A/bin/Release/net10.0/Godswar.Server.Phase5A.dll --self-check
dotnet tools/Godswar.Server.Phase5A/bin/Release/net10.0/Godswar.Server.Phase5A.dll `
  --mode load --bots 64 --duration-seconds 10 --seed 20260728
dotnet tools/Godswar.Server.Phase5A/bin/Release/net10.0/Godswar.Server.Phase5A.dll `
  --mode paced-soak --bots 64 --duration-seconds 10 --seed 20260728
```

Combined local gate:

```powershell
.\tools\TestPhase5ABaseline.ps1 -Bots 64 -SoakSeconds 10 -Seed 20260728
```

The combined gate builds Release, runs the Phase 5A plus overload/admission
checks, runs the tool self-check, runs both workload modes, and writes an
ignored receipt under `artifacts/phase5a`. It records Git HEAD, dirty state,
and a SHA-256 manifest of tracked plus untracked non-ignored source. The gate
hashes that state before and after execution and fails if it changes.

`.github/workflows/phase5a-network-gate.yml` runs the focused check families,
the tool self-check, and a 16-bot two-second targetless paced smoke test for
pull requests and mainline pushes. CI does not reproduce the accepted 64-bot
ten-second runs or write the local provenance receipt.

## Accepted local baseline

On 2026-07-28, Windows `10.0.26200`, x64, 32 logical processors, and
`.NET 10.0.0-rc.2.25502.107`, using implementation commit
`2986466cfbdb641fe849ce62c7cfd951f2715de8`. The final clean-tree gate
was recorded at documentation commit
`19247006df2b95b52f344dafe2bbf48ab5fb9f36`:

- Release solution build: 0 warnings and 0 errors.
- Full managed protocol suite: 149 passed, 0 failed.
- Focused Phase 5A/overload/admission gate: 6 passed, 0 failed.
- Load self-check: 13 passed.
- Load mode: 12,800 bot-ticks, 25,600 codec packets, 1,484,800 protocol
  bytes, 76,800 operations, 0 rejected movements, 32.8124 ms elapsed, and
  53,840 allocated bytes including setup.
- Paced soak: the same exact workload over 10,007.6551 ms, 0 rejected
  movements, 0 pacing misses, p99 processing time 0.356264 ms, maximum
  3.0569 ms, and 129,712 allocated bytes including setup.

Selected machine-readable values and limitations are in
[`baselines/network-phase5a-local-20260728.json`](baselines/network-phase5a-local-20260728.json).
That record also preserves the clean receipt's HEAD, dirty flag, and source
manifest digest. These values describe one synthetic local run only.

## Exit decision and remaining work

Phase 5A's local gates pass:

- deterministic repeat and checkpoint recovery;
- bounded workload configuration and runtime accounting;
- malformed decoder safety;
- existing overload and recovery behavior;
- low-cardinality simulation and operational metrics;
- reproducible local benchmark;
- documented alert policy, incident response, and rollback procedures in
  [`network-infrastructure-phase5a-operations.md`](network-infrastructure-phase5a-operations.md).

Still required before a production claim:

- private metrics/health export and deployment-specific dashboards;
- bounded live multi-client and longer soak runs in an authorized staging
  environment;
- structured, sampled, privacy-aware logging at remaining legacy call sites;
- a selected upstream arbitrary-TCP/UDP DDoS provider and protected origin;
- independent review of the UDP cryptographic construction;
- capacity inputs and multi-region/failure requirements.
