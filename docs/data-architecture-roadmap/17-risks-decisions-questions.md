# 17. Risks, decisions, and unresolved questions

## Confirmed decisions

These are repository-supported facts, not assumed approval of every target recommendation:

1. The Docker runtime selects PostgreSQL; the normal checked-in application configuration still defaults to the incomplete JSON store.
2. Current PostgreSQL access uses Npgsql, explicit SQL/transactions, row locks, and the custom migration runner.
3. The custom ECS has no automatic component serialization or durable whole-world snapshot.
4. Runtime ECS IDs, sessions, AOI, monster state, tickets, replay windows, and keys currently exist only in process memory.
5. TLS plus authenticated UDP movement is implemented/tested as an opt-in profile; raw TCP remains the checked-in default.
6. The runtime is one modular-monolith server process plus PostgreSQL in Docker; no Redis/Mongo/distributed placement process exists.
7. Applied migration IDs/checksums are immutable under the documented policy, and the current runner enforces exact-prefix compatibility.
8. Several valuable PG operations already use transactions/row locks, but there is no general durable inbox/outbox or stable client operation ID.
9. Content reads are split across generated package catalogs, PG catalogs, and captured fallbacks.
10. ADR 0003 records B16's evidence-based defer for the current process.
    ADR 0004 confirms Tempest as the first realm, future multiple realms,
    cross-realm Pindus, and ephemeral battlefield/dungeon instances; B17 is
    reopened and approved for future coordination but is not implemented or
    deployed.
11. B18A/B provide process-local realm/instance placement, a live runtime
    directory keyed by `WorldInstanceId`, instance-aware sessions and
    transfers, and one bounded map-owner mailbox per local runtime. The
    legacy byte-map bridge resolves only Tempest's default open world unless
    a routed session supplies an exact instance.

## Roadmap recommendations pending approval

1. Use PostgreSQL only for the initial migration and as the sole durable player-value authority.
2. Continue direct Npgsql for transactional paths; do not introduce EF Core now.
3. Persist selected durable facets, never the entire ECS world.
4. Commit valuable commands before success acknowledgement and use PG inbox/audit/outbox instead of cross-store dual writes.
5. Do not introduce MongoDB during the initial migration.
6. Make raw account-creating/username-only authentication impossible outside an explicit local-development profile, then retire it when client compatibility permits.
7. Keep modular-monolith code boundaries while introducing local
   realm/node/world-instance placement first; split gateway/workers only at
   those explicit composition boundaries.

## Assumptions

1. The immediate deployment remains one server process plus one PostgreSQL service.
2. Position/vitals may lose a small, documented checkpoint tail on crash; inventory/currency/progression value may not.
3. PostgreSQL 17 remains the target database version.
4. The original client can use the in-process networking shim for the secure profile.
5. Stable numeric template IDs remain compatible with the original client.
6. Captured packet data is research evidence, not indefinite runtime authority.
7. The dirty pet working tree is intentional work in progress and will be reconciled into a coherent release, not discarded.
8. Ordinary monster state resets after process crash; only explicitly permanent world outcomes persist.
9. One region is sufficient until product/hosting inputs say otherwise.

## Unresolved questions

Priority 0:

1. Which exact uncommitted migrations/code have already reached the connected local development database, and what commit/tag should own schema version `20260729_022`?
2. Will raw TCP remain accessible outside controlled development, and when must secure TLS become mandatory?
3. What are the required no-loss semantics when a monster death commits in ECS but its reward transaction is unavailable?
4. What restore window and authorization apply to character deletion?
5. How will the unchanged legacy client/shim supply a stable operation identity for cross-reconnect retries of valuable commands?
6. How many characters/slots may one account own, given the database currently permits multiple rows while the client preview selects the first?

Priority 1:

7. What are target concurrent players per process/realm/map/instance, peak
   login and instance-admission rates, regions, tick/snapshot rates, and
   acceptable loss/latency?
8. What release introduces the first runnable gateway/worker split and
   activates B17's Redis implementation?
9. Which gateway/placement component owns node selection, sticky routing,
   draining, transfer, and recovery?
10. What reconnect/resume guarantee and maximum window are required?
11. What Redis latency, outage, staleness, eviction, provider, region, and
    cost budgets must be approved before B17 is enabled?
12. Which open-world maps are statically assigned, which are dynamically
    placed, and how many instances may one worker own?
13. What RPO, RTO, backup retention, privacy, security-audit, and economy-audit retention are required?
14. Which hosting/upstream L3/L4 provider will protect arbitrary TCP and UDP ports?

Priority 2:

15. Should content be authored in source/generated resources, in PostgreSQL, or via a future tool, and how are revisions approved?
16. Should packet captures remain in the gameplay database, move to a separate research database, or archive to object storage?
17. Is durable chat/moderation history required?
18. What exact schedules, admission rules, party sizes, reconnect windows,
    and reward-settlement rules apply to Pindus, Ni Mini Valley, Lelantine,
    Medusa Island, Atlantis, Wonderland, and Bay Under Attack?
19. Which other future features are actually scheduled: guilds, trade,
    auction, mail, quests, housing, achievements, player-generated content?
20. What data residency, payment, child-safety, and account-deletion obligations apply?
