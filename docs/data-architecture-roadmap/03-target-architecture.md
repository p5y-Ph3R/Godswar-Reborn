# 3. Target architecture

## 3.1 Recommended boundaries

Keep a modular monolith initially, but impose dependency direction:

```text
Untrusted client
    |
    v
[Raw compatibility edge] or [TLS control] + [authenticated UDP ingress]
    |
    v
Transport session + packet decoder
    |  produces versioned CommandEnvelope
    v
Authentication / authorization / ownership / replay validation
    |
    v
Application command or query handler
    |                         |
    | runtime command         | durable transaction request
    v                         v
single-owner map/player ECS   feature-specific persistence contract
    |                         |
    | immutable events        v
    |                  PostgreSQL Npgsql adapter
    |                         |
    |                  inbox + state + audit + outbox (one transaction)
    |                         |
    |                  committed result
    |                         |
    +------------> owning player/map mailbox
                              |
                       revalidate session/entity/
                       aggregate generation
                              |
                              v
                    ECS projection + immediate result
                              |
                              v
                    state-replication adapter
    |
    +--> TLS reliable result/control
    `--> UDP sequenced snapshot

PostgreSQL outbox ---> eventual cache/external projections only
                      (at least once; versioned and rebuildable)

Optional only after measured scale-out decision:
session coordinator ---> Redis tickets/leases/routing/presence directly
                         (atomic TTL coordination carrying a PG-issued fence)
PostgreSQL outbox -----> Redis read caches/projections only

MongoDB: absent unless a future ADR proves a document workload.
```

The transactional outbox never drives the immediate live-player result. The PG commit result returns directly to the owning mailbox with its committed aggregate version. The owner either serializes conflicting player-value commands or applies only the exact next version; a stale version is ignored and a version gap triggers a PG reload before updating ECS/replying. It also revalidates the current session/entity/ownership generation. Outbox consumers handle eventual caches, indexes, notifications, and external projections.

The dependency rule is:

```text
Networking -> Application contracts -> Domain/ECS
                                  ^
                                  |
                    Infrastructure implementations
                    (PostgreSQL; later optional Redis)
```

Domain/ECS code must not reference Npgsql, a Redis client, sockets, packet buffers, or database rows. Infrastructure may reference application contracts and domain value types. Packet definitions remain in the protocol adapter and must not become persistence DTOs.

## 3.2 Module responsibilities

| Boundary | Responsibility | Must not do |
| --- | --- | --- |
| UDP/TCP transport | Socket lifecycle, TLS, datagram protection, bounded queues, deadlines, framing | Interpret inventory/economy meaning or call stores |
| Session/authentication | Credential verification, principal, secure ticket, connection ownership, permissions | Trust username/account fields after binding; mutate gameplay value |
| Packet decoder | Bounds-check and translate bytes to versioned command DTOs | Hydrate ECS or issue SQL |
| Command validation | Authentication, authorization, packet sequence, world generation, input shape, rate/cost limits | Accept client-computed outcomes |
| Application command handler | Define one use case and its transaction/ack semantics | Hide consistency behind a universal CRUD repository |
| ECS/map owner | Fixed-step runtime authority, deterministic updates, runtime events | Block on database/network/external API |
| Persistence contracts | Feature-specific reads and transactions | Expose Npgsql types to gameplay; model all stores as identical key/value APIs |
| PostgreSQL adapters | SQL, locks, constraints, versions, inbox/outbox/audit, current-state rows | Send client packets or mutate ECS directly |
| Optional Redis adapters | Disposable sessions/routing/rate limits/caches with TTL and fencing | Own player value or participate in cross-store dual writes |
| Persistence worker | Coalesced low-value checkpoints, outbox delivery, retries, reconciliation | Run unbounded queues or silently discard failed valuable writes |
| Replication | Convert immutable accepted ECS/application results to client snapshots/events | Decide authoritative outcomes |
| Observability | Low-cardinality metrics, redacted logs, trace context, health/readiness | Log credentials, ticket/cookie/key material, raw payloads, or identifiers as metric labels |

## 3.3 Application contracts

Replace the single `IGameStore` incrementally with operation-focused contracts, for example:

- `IAccountCredentialStore` and `AuthenticateAccountHandler`;
- `ICharacterSnapshotReader` and `ICharacterLifecycleTransactions`;
- `IInventoryTransactions`, `ICurrencyTransactions`, and `IProgressionTransactions`;
- `IPetTransactions`;
- `IWorldContentReader`;
- `IPlayerCheckpointWriter`;
- an internal `ITransactionalInboxOutbox` helper used by PostgreSQL implementations, not exposed as a generic domain repository.

Use command-specific request/result types that include `AccountId`, `CharacterId`, authenticated connection/session generation, client operation ID where supported, expected aggregate version, and protocol version. The result must distinguish committed, duplicate-with-same-result, conflict, validation rejection, and transient failure.

## 3.4 Consistency tiers

1. **Transactional player value**: inventory, equipment, currency, progression grants, pet ownership, paid operations, trades, auctions, deletion. PostgreSQL commit and idempotency record precede success acknowledgement.
2. **Checkpointed durable state**: position, vitals, selected cooldown/logout data if product requires it. ECS is authoritative while online; coalesced writes have bounded loss and explicit versions.
3. **Runtime state**: combat intents, AI, AOI, transient status timers, UDP replay state. ECS/in-memory only.
4. **Configuration/content**: versioned PostgreSQL catalogs or immutable packaged resources. A server instance pins a compatible content revision.
5. **Projections/caches**: summaries, leaderboards, presence, routing. Rebuildable and never authoritative.
