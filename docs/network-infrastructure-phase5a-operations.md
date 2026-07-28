# Phase 5A network operations

## Purpose and current boundary

This is the provider-neutral dashboard, alert, incident-response, and rollback
reference for the Phase 5A local baseline.

The application controls marked implemented in the responsibility matrix have
local automated coverage, but Phase 5A is not every production control. No
upstream DDoS provider, public metrics endpoint, or production origin has
been selected.
Ordinary autoscaling, a host firewall, IP bans, SYN cookies, or an HTTP CDN
must not be described as protection against an arbitrary volumetric TCP/UDP
attack.

The management plane must eventually be private: VPN, zero-trust access, or a
loopback/private collector. Metrics, health, profiling, and administration
must never be exposed on the public login, game, or UDP ports.

Do not change a developer workstation's firewall, antivirus, network adapter,
DNS, routes, or internet configuration while running these local gates.

## Meter collection contract

Consume these standard .NET meters:

| Meter | Purpose |
| --- | --- |
| `Godswar.Server.Networking` | TCP lifecycle, bounded reliable queues, bytes, timeouts, drains, disconnects |
| `Godswar.Server.Networking.Secure` | TLS, secure framing/queues, UDP packets, datagram outcomes, runtime outcomes |
| `Godswar.Server.Security.Authentication` | authentication outcomes and duration |
| `Godswar.Server.Simulation` | loop state, tick cost/drift, missed boundaries, heartbeat age |
| `Godswar.Server.OperationalState` | authoritative admission, ticket, UDP session, and limiter gauges |

The application publishes instruments but deliberately does not start an
exporter. A deployment adapter may attach a `MeterListener` or OpenTelemetry
SDK and export over a private management path. Before activation, verify the
exporter's exact metric-name/unit normalization and dashboard queries against
a staging scrape.

Privacy and cardinality rules:

- never label by account, character, session, connection, ticket, IP, map,
  packet text, or exception message;
- retain only the finite label sets already enforced by protocol checks;
- do not log secrets, tickets, cookies, keys, passwords, raw packets, or
  arbitrary player input;
- sample repetitive diagnostics and cap message length and output rate;
- metric callbacks must not perform I/O or access storage. The grouped
  observers take one bounded in-memory snapshot per family and collection;
  snapshots may briefly take locks and perform their existing expiry or
  limiter-window maintenance. Collect them at a controlled interval through
  the private management path, never the gameplay listener.

## Minimum dashboard

The deployment dashboard should have these panels:

1. Active and unauthenticated TCP connections, with configured admission
   capacity shown as a constant.
2. TLS handshakes by finite outcome plus p50/p95/p99 duration.
3. Authentication attempts by outcome plus p95/p99 duration.
4. Reliable, secure-ingress, and secure-control queue items/bytes and overflow
   rate.
5. UDP packets/bytes in and out, datagram outcomes, and rate-limited,
   malformed, replay-rejected, endpoint-mismatch, movement, and snapshot
   rates.
6. UDP ready/faulted state, pending/bound sessions, session capacity, and
   limiter-table occupancy.
7. Per-loop active state, heartbeat age, tick rate, p50/p95/p99/max duration,
   schedule drift, and missed boundaries.
8. Process CPU, working set, allocation/GC rate, thread-pool pressure, handles
   or file descriptors, and socket counts from standard runtime/host
   telemetry.
9. Upstream mitigation state, clean bandwidth, packet rate, dropped attack
   traffic, and origin health once a provider is selected.

Illustrative PromQL after exporter normalization:

```text
histogram_quantile(0.99,
  sum by (le, simulation_loop)
    (rate(godswar_server_simulation_tick_duration_bucket[5m])))

sum by (simulation_loop)
  (rate(godswar_server_simulation_tick_missed_deadlines_total[5m]))

godswar_server_operational_udp{operational_state="sessions_bound"}
  / godswar_server_operational_udp{operational_state="sessions_capacity"}

rate(godswar_server_network_secure_udp_datagrams_total{
  network_secure_udp_outcome=~"rate_limited|malformed|replay_rejected"
}[5m])
```

These are examples, not checked-in exporter output. Confirm suffixes, unit
conversion, and label normalization in staging.

## Initial alert policy

Thresholds are starting points and must be tuned from authorized staging
baselines:

| Signal | Initial condition | Action |
| --- | --- | --- |
| UDP runtime fault | `faulted == 1` for one collection | page; preserve TLS fallback |
| UDP session pressure | pending + bound above 80% capacity for 5 min | investigate admission/expiry; shed new handshakes before established play |
| Admission pressure | active or unauthenticated above 80% capacity for 5 min | inspect outcome rates and upstream telemetry |
| Queue pressure | any bounded queue above 80% or overflow counter increases | page if sustained; protect simulation and established sessions |
| Realtime tick | p99 above 50 ms for 5 min or missed-boundary rate above zero | page gameplay owner |
| Monster/recovery heartbeat | age above 1 second while active | page gameplay owner |
| EXP/Zodiac heartbeat | age above three configured intervals while active | investigate background-loop stall |
| TLS/auth abuse | rejection/busy/timeout rate exceeds learned baseline | restrict new unauthenticated work; do not punish established sessions |
| Replay/proof failures | sudden sustained increase | investigate abuse, clock/key rollout, or client-version mismatch |
| Process pressure | CPU, memory, handles/FD, or thread pool above host budget | drain instance; do not call autoscaling DDoS mitigation |

Every page should include current capacity, five-minute and one-hour rates,
last deployment/protocol version, upstream status, and whether established
sessions remain healthy. Do not include player identifiers in alert labels.

## DDoS responsibility matrix

| Control | Application | Host / network | Upstream provider |
| --- | --- | --- | --- |
| Bounded parsing, queues, maps, workers | implemented and tested | enforce process limits | not applicable |
| UDP spoof/replay/amplification defense | cookies, AEAD, replay window, response budget | socket/firewall policy | source validation and scrubbing |
| UDP per-session and prefix admission | implemented and finite | conntrack/socket capacity | edge connection and PPS policy |
| Per-account abuse limiting | open; authentication work is bounded but no per-account rate limiter exists | account recovery and private administration | credential-abuse controls where supported |
| Established UDP priority / general load shedding | authenticated-session budget implemented; broader shedding is not exercised by Phase 5A | scheduler/resource isolation | preserve clean traffic |
| TCP SYN flood | handshake/application deadlines only | backlog, SYN cookies, conntrack | mandatory volumetric absorption |
| UDP/TCP bandwidth or PPS flood | cannot absorb link saturation | cannot exceed origin link | mandatory arbitrary-port L3/L4 scrubbing |
| Origin concealment | no forwarded-IP trust by default | ingress allowlist/tunnel | protected edge addresses and authenticated client-IP delivery |
| Regional failover | reconnect/resume semantics | instance draining | Anycast/routing capacity and health failover |
| Attack testing | bounded loopback tools only | authorized staging controls | written provider authorization |

Production activation requires an upstream service that explicitly supports
the game's arbitrary TCP and UDP ports, IPv4 and IPv6, automatic L3/L4
scrubbing, sufficient clean bandwidth/PPS, regional failover, authenticated
client-IP preservation, telemetry/API access, alerts, and an appropriate SLA.

## Runbook: volumetric UDP flood

Detection:

- upstream attack/mitigation signal;
- origin or edge PPS/bandwidth saturation;
- sharp increase in UDP rate-limited/malformed outcomes;
- rising socket drops while established-session traffic degrades.

Response:

1. Confirm the event from upstream telemetry; application counters alone
   cannot measure traffic discarded before the origin.
2. Preserve established authenticated sessions and reduce or pause new
   unauthenticated UDP validation.
3. Engage the upstream provider's documented mitigation path.
4. Confirm the origin is reachable only through the protected edge/tunnel.
5. If UDP clean traffic cannot be preserved, use the already-tested one-way
   TLS fallback; do not switch an authority epoch back automatically.
6. Avoid verbose packet logging and attacker-controlled responses.

Recovery:

- wait for stable clean PPS, queue occupancy, tick latency, and heartbeat;
- restore admission gradually;
- verify replay/proof rejection rates return to baseline;
- record provider detection and mitigation times, peak clean/attack traffic,
  user impact, and any origin leak.

## Runbook: TCP SYN or connection exhaustion

1. Distinguish SYN backlog pressure from completed TLS connections and
   Slowloris-style reads.
2. Verify upstream SYN protection and host backlog/conntrack state.
3. Protect the TLS handshake gate and unauthenticated admission capacity.
4. Shorten nothing below tested safe client deadlines during the incident;
   shed new work with cheap rejection instead.
5. Preserve bounded write/read queues and established authenticated sessions.
6. Drain a damaged instance only after healthy placement is available.

Autoscaling may add clean-session capacity but is not a substitute for
upstream volumetric filtering.

## Runbook: authentication abuse

1. Compare accepted, rejected, invalid, busy, and timeout outcomes against the
   normal regional baseline.
2. Verify the bounded KDF scheduler, queue capacity, credential-byte budget,
   and operation deadline.
3. Apply account and prefix controls without using account/IP values as metric
   labels.
4. Do not weaken password hashing or expose detailed failure reasons.
5. Review sampled, privacy-scrubbed audit records for credential stuffing.
6. Escalate compromised accounts through the account-recovery process, not
   through network bans alone.

## Runbook: origin exposure

1. Treat a discovered origin address as compromised, even if no traffic is
   visible yet.
2. Restrict ingress to authenticated provider tunnels or exact edge ranges.
3. Rotate the origin address using the hosting/provider procedure.
4. Search DNS history, certificates, logs, crash reports, diagnostics, and
   administrative endpoints for the leak.
5. Publish only protected edge addresses and verify direct-origin probes fail
   from an authorized external test point.
6. Reissue any manifest/configuration that embedded the old address.

## Runbook: upstream-provider failure

1. Confirm whether failure affects TCP, UDP, one region, or the provider
   control plane.
2. Keep the origin closed; do not bypass protection by publishing it.
3. Route to a pre-authorized secondary protected edge if one exists.
4. Prefer controlled reconnect/resume and instance draining over split
   ownership of live match state.
5. If no protected path exists, fail closed and communicate status.
6. Record routing convergence, clean capacity, reconnect success, and session
   loss.

## Runbook: key or certificate compromise

1. Identify scope: TLS leaf/private key, ticket authority, cookie secret, or
   UDP traffic key.
2. Stop issuing affected credentials and revoke/expire related sessions.
3. Rotate through the documented key ring with only the bounded overlap needed
   for healthy clients; a confirmed compromise may require zero overlap.
4. Redeploy secrets through the secret store, never source control or command
   output.
5. Reissue the client trust/manifest only through its signed rollout and
   rollback process.
6. Verify old tickets, cookies, keys, replays, and endpoints are rejected.
7. Preserve sanitized evidence and obtain independent security review.

## Runbook: false positive and overload recovery

1. Confirm the signal across application, host, and upstream layers.
2. Identify which finite limiter or queue rejected work and whether
   established sessions were affected.
3. Change thresholds only from measured evidence; never disable validation,
   replay protection, authentication, or bounds to clear an alert.
4. Restore new-session admission gradually while watching tick p99, missed
   boundaries, queue occupancy, and authentication latency.
5. Run the focused Phase 5A regression gate after remediation.
6. Record the old/new threshold, evidence window, approver, and rollback
   condition.

## Rollback

Phase 5A adds no database migration, packet opcode, client patch, listener, or
default-on feature. Replay and the load tool are offline. Metrics add no new
gameplay authority decision. Operational collection invokes existing bounded
snapshot paths, which may perform their normal expiry and limiter-window
maintenance.

Code rollback:

1. Stop or drain the server normally.
2. Revert the Phase 5A commits with ordinary Git history; do not reset the
   repository or delete data.
3. Build Release and run the Phase 4 movement, impairment, and handler suites.
4. Restart with the prior checked-in settings.
5. Confirm TLS fallback and UDP authority remain at their previous accepted
   behavior.

Secure client activation/rollback remains governed by
[`network-infrastructure-controlled-host-rollback-commands.md`](network-infrastructure-controlled-host-rollback-commands.md).
The local Phase 5A gate does not install a client shim, trust root, hosts-file
entry, firewall rule, route, or network-adapter change.
