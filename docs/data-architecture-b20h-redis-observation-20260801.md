# B20H Redis-coordinated local observation (historical)

Status: invalidated local-alpha rehearsal; not retirement authorization

## Historical run

The game server now uses the `Redis` operational-coordination provider in the
main `reborn` Compose project. Redis is a private, disposable coordination
container; PostgreSQL remains the authoritative durable player-data store.

| Field | Value |
| --- | --- |
| Source commit | `db6565382693f84b25b94c8790afbb91c9c39697` |
| Topology | `redis-coordinated-single-worker` |
| T0 | `2026-08-01T07:08:36.9520131Z` |
| Target end | `2026-08-08T07:08:36.9520131Z` |
| Evidence directory | `artifacts/b20h-observation/20260801T070756Z-db6565382693` |
| PostgreSQL volume | `reborn_godswar-postgres-data` |
| Required coordination routes | `23` |
| Invalidated | `2026-08-01T08:34:55.7875487Z` |
| Reason | `local-gameplay-compatibility-hotfix` |

The initial export is `in_progress` and confirms observer readiness `1`, Redis
coordination readiness `1`, exact route minimum/maximum `23`, zero legacy
invocations, zero missing required samples, matching observation/input hashes,
matching Redis identity, and no active alerts. This partial window was later
invalidated and cannot be resumed or combined with another run. The export
also deliberately records `eligibleForRetirementAuthorization=false` because
this was a local single-worker alpha rehearsal.

The authoritative current-state indicator is the ignored runtime pointer
`artifacts/b20h-observation/active-observation.json`. Use
`GetB20HDockerObservation.ps1` to inspect it; this historical document must
not be used to decide whether an observation is active.

## Retired rehearsals

Two earlier runs are also retained as non-authorizing evidence and must never
be combined with a later window:

- `20260801T044109Z-d0f3f73c8339` used Local coordination and was invalidated
  for `coordination_topology_correction`.
- `20260801T065946Z-6dc6101f4f14` used Redis coordination but exposed an
  exporter parameter-binding bug for an intentionally absent lazy legacy
  counter. It was invalidated for `telemetry-exporter-empty-lazy-counter-fix`.

The exporter fix is covered by a regression proving that an absent lazy
counter produces zero maximum, resets, and missing-confirmation evidence.
Both retired runs retain their start records, invalidation receipts, and
Prometheus TSDB directories.

## Operating boundary

During any active replacement window, do not rebuild, recreate, or restart the
game server, Redis container, or Prometheus observer. Source work may continue,
but it must not be deployed into those containers. Keep Docker and the host
awake and run the required gameplay workload, including scheduled world-boss
cycles.

Use these read-only checks:

```powershell
.\tools\GetB20HDockerObservation.ps1
.\tools\ExportB20HDockerObservationTelemetry.ps1
```

This local run validates the mechanics and alpha workload. Final compatibility
deletion still requires a real all-replica deployed observation, approved
external evidence, workload coverage, and all recovery gates described in the
B20H runbook.
