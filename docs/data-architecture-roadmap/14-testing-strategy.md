# 14. Testing strategy

## 14.1 Test layers

| Layer | Required coverage | Repository action |
| --- | --- | --- |
| Unit/domain | ECS systems, validation, arithmetic, versions/fences, key builders, TTL policies, request canonicalization | Keep deterministic custom checks initially; consider xUnit only if it improves isolation/reporting without a flag-day rewrite |
| Codec/property/fuzz | Every externally reachable TCP/UDP decoder: truncation, oversize, malformed counts/strings, random bytes, wraparound | Extend current secure decoder fuzzing to all opcodes/codecs; fixed seeds plus coverage-guided fuzz job |
| Repository | SQL mapping, not-found/conflict/duplicate, constraints, query shape | Run against disposable PG 17; never mock SQL semantics for acceptance |
| Migration | Empty DB, each supported old version, restored representative backup, interrupted connection, checksum/ahead/reorder, rolling compatibility | Make mandatory in CI; a skipped integration is a failed gate |
| ECS save/load | Durable snapshot -> hydrate -> derive -> checkpoint -> reload; runtime-only fields absent | Golden snapshots for current and previous contract version |
| PostgreSQL integration | Transaction isolation, row locks, timeouts, inbox/outbox, ledger/reconciliation, concurrent operations | Use Testcontainers for .NET or an equivalent CI-managed disposable PG container |
| Redis integration, conditional | TTL, atomic Lua, restart/eviction, lease/fence, shared NAT, cache reconstruction | Add Testcontainers Redis only after Redis ADR approval |
| MongoDB integration | None initially | Add only if MongoDB is approved; absence should be enforced |
| Handler/end-to-end | TLS auth, ticket consume, character load, UDP bind/movement, reliable control, reconnect/shutdown | Extend `Godswar.Server.SecureSmoke` with disposable PG and reference shim where safe |
| Network impairment | latency, jitter, burst loss, duplication, reorder, MTU, UDP block/fallback | Preserve current deterministic emulator and Phase 5A gates |
| Load/soak | realistic command mix, AOI, PG, queues, reconnect churn, bounded bot traffic | Extend targetless/loopback tools; hard allowlist/duration/traffic caps |
| Backup/restore | base backup + WAL/PITR where available, schema/history verification, reconciler | Scheduled staging drill with recorded RPO/RTO |

## 14.2 Required failure scenarios

1. **Server crashes after PG commits but before Redis update:** PG state/inbox/outbox survive; reconnect reads correct state; outbox later rebuilds Redis.
2. **Redis updates but request fails before acknowledgement:** this ordering is forbidden for authoritative writes. If a cache update occurred after PG commit, retry reads PG inbox and returns the committed result.
3. **Client retries a completed transaction:** same operation ID/request hash returns the original result without another mutation; a changed request is rejected/audited.
4. **Two servers load one player:** only the highest valid ownership fence may commit; loser drains; alert emitted.
5. **Reconnect reaches another server:** new TLS ticket/connection generation, ownership acquisition, PG snapshot load, new UDP keys/epoch; old packets fail.
6. **Stale packet follows newer state:** replay/sequence/world generation/aggregate version rejects it.
7. **Migration interrupted:** current migration transaction rolls back; history remains exact prefix; next runner resumes.
8. **Old/new server versions coexist:** expand schema and compatibility manifest permit both; contract migration waits until old version drains.
9. **Monster dies and reward persistence fails:** death event has deterministic ID; reward remains pending/retriable and never double-grants.
10. **Character delete commits but response is lost:** retry returns tombstone state; it never deletes a different/recreated character.
11. **Outbox consumer crashes after side effect:** duplicate event is rejected by event ID/version.
12. **Database is slow/unavailable:** map tick continues or sheds according to policy; value commands fail without success; readiness and metrics change.
13. **Redis is slow/unavailable after adoption:** cache falls back; cross-instance ownership operations fail safe; established sessions follow lease-grace policy.
14. **Queue/limiter map exhausted:** work and memory stay bounded; authenticated established sessions get documented priority.
15. **Restore contains older schema/content:** migrator upgrades it; reconciler proves invariants before readiness.
16. **Retry token outlives inbox retention:** configuration and tests forbid this state; an expired or purged token is rejected and cannot re-execute value.
17. **Ownership transfer races a child-row mutation:** both contend on the authoritative PG ownership row; either the value transaction commits under the old fence before transfer or transfer wins and the old operation conflicts.

## 14.3 Concurrency and determinism

- Run concurrent duplicate inventory/forge/pet/reward/currency commands with the same and different operation IDs.
- Randomize transaction interleavings and assert serializable invariants.
- Run deterministic ECS replay from the same durable snapshot/content revision and compare canonical hashes.
- Exercise sequence wraparound rules and explicitly disconnect before cryptographic sequence reuse.
- Use runtime concurrency diagnostics supported by .NET and stress the custom locks/mailboxes; add watchdog deadlines so deadlocks fail tests.

## 14.4 CI gates

1. Format/analyzers and Release build on supported Windows/Linux server targets.
2. Full fast managed suite with machine-readable results.
3. Mandatory PostgreSQL 17 integration and migration job; no SKIP-as-PASS.
4. Raw-development and secure Docker build/smoke contract.
5. Fuzz/property smoke and Phase 5A bounded workload.
6. Dependency, secret, SAST, container, and SBOM checks.
7. Scheduled longer replay/soak/restore rehearsal.

Pin SDK/package/image versions (`global.json`, lock strategy, image digests according to release policy). The current `NuGetAudit=false` settings and mutable action/image tags are not sufficient production gates.
