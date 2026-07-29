# B07 legacy operation identity and command envelope

Date: 2026-07-29
Roadmap dependency: B01B, B02, B03, and B06
Next roadmap ticket: B08 - PostgreSQL inbox/outbox foundation

## Outcome

B07 selects the existing talent-upgrade request as the first valuable command
to cross the application command boundary. The unchanged client sends this
request through `Opcodes.UseOrEquip` (`10049`) and already echoes the rank it
expects to upgrade. That expected rank identifies a single state transition:

```text
talent rank N -> talent rank N + 1
```

The logical operation identity is:

```text
authenticated account
  + authenticated/owned character
  + talent-upgrade command family
  + validated talent ID
  + expected rank N
```

It is independent of TCP connection, secure-transport epoch, packet sequence,
session generation, receive time, and server process lifetime. An exact retry
after reconnect therefore produces the same identity. A legitimate next
upgrade carries expected rank `N + 1` and produces a different identity.

B07 establishes that identity, a bounded server-derived command envelope, and
an exact expected-rank precondition in both storage providers. A replay cannot
purchase the next rank, including after restart, because the persisted rank no
longer equals the request's expected rank. B07 does not persist the original
request hash or result and therefore cannot replay the exact success response
or prove a different-hash conflict after restart. B08 will store the identity,
canonical request hash, mutation result, audit reference, and outbox event in
the same PostgreSQL transaction as the rank/point mutation.

## Why talent upgrade was selected

The captured decoder in
`Game/GameClientHandler.PacketDecoding.cs::TryReadTalentUpgrade` accepts an
exact 24-byte payload and reads:

| Field | Offset | B07 interpretation |
| --- | ---: | --- |
| Talent ID | 4 | Untrusted intent; must resolve to a template for the authenticated character's profession |
| Client rank | 8 | Untrusted expected rank for the requested `N -> N + 1` transition |
| Client talent points | 16 | Diagnostic UI echo only; excluded from command semantics, identity, and authorization |

`GameClientHandler.InventoryActions.cs::HandleUseOrEquipAsync` supplies account
and character IDs from `_account` and `_character`; the payload cannot choose
them. Both `JsonGameStore.Progression.cs` and
`PostgresGameStore.SkillsAndTalents.cs` already derive the current rank,
upgrade cost, player-level requirement, and spendable talent points from
server state. Before B07 they ignored the echoed rank when applying a mutation,
which meant a replay could buy another rank. B07 now compares the expected rank
with the authoritative rank while holding the existing JSON lock or PostgreSQL
character-row lock.

This identity is stronger than a time-window content hash: it names the
aggregate transition, not merely bytes that happened to arrive near each
other.

## Identity and canonical request contract

Contract version 1 uses the bounded family code `talent_upgrade`.
The operation key is the canonical tuple:

```text
v1 / accountId / characterId / talent_upgrade / talentId / expectedRank
```

The application may encode that tuple into a deterministic, fixed-size
SHA-256 value using a domain-separated, fixed-width binary representation.
The tuple above remains the normative identity; changing serialization,
field order, integer width, byte order, or domain prefix requires a new
identity version.

The canonical request hash is SHA-256 over a separate, domain-separated
versioned representation of:

```text
command contract version
  + talent_upgrade family
  + validated talentId
  + validated expectedRank
```

It is calculated after decoding and bounds validation, not over raw packet
bytes. Reserved bytes, TCP framing, encryption, transport sequence, endpoint,
session generation, timestamps, and account/character display values are not
part of the request hash.

The points echo is excluded because it does not affect the authoritative
operation. Hashing it would make a legitimate retry conflict merely because
the player's displayed balance changed. The server validates it only as a
bounded legacy diagnostic; persisted points and `TalentProgression` remain
the sole balance and cost authority.

The following outcomes are distinct:

| Condition | Outcome |
| --- | --- |
| Exact payload is malformed or outside bounds | Reject before constructing an envelope |
| Template is not valid for the authenticated character | Reject as invalid/unauthorized intent |
| Persisted rank differs from expected rank and no committed result exists | Reject as stale precondition |
| Preconditions such as level or points are not met | Reject without consuming the transition identity |
| Same operation identity and same request hash after commit | B07 safely rejects the stale rank without another spend; B08 returns the stored result |
| Internally inconsistent envelope hash | B07 rejects it as a request-hash conflict before persistence |
| Same committed operation identity and a different request hash | B08 detects the durable security conflict; B07 has no committed-hash record |
| Expected rank advances after a successful upgrade | New legitimate operation |

Contract limits for this slice are:

- packet payload: exactly 24 bytes;
- authenticated account ID and character ID: positive 32-bit server values;
- talent ID: non-negative bounded 32-bit value present in the template catalog
  for the character's server-owned profession;
- expected rank: `0..99`, because `TalentProgression.RankCap` is `100` and
  this command requests exactly one next rank;
- client talent-point echo: `0..Int32.MaxValue`;
- one command produces at most one talent-rank increment and one
  server-calculated point debit.

A failed precondition does not reserve the rank transition forever. Once the
precondition changes, the client may request the same `N -> N + 1` transition
again. B08 must create the durable inbox row atomically only with a committed
mutation (or explicitly distinguish non-consuming rejection records).

## Command envelope and trust boundary

After strict legacy decoding, the handler constructs a bounded application
envelope containing:

- command/contract version and `talent_upgrade` family;
- account and character IDs from the authenticated session;
- connection/session generation for correlation only;
- deterministic operation identity and canonical request hash;
- server receive timestamp;
- validated talent ID and expected rank.

The adapter retains the bounded client-points echo as diagnostic input for the
temporary broad-store signature, but it is not part of the application
command envelope.

The authenticated IDs, receive timestamp, and session correlation are
server-derived. The talent ID and expected rank are untrusted client intent;
the points echo is untrusted diagnostics only. Before mutation, the server
must verify character ownership, profession/template membership, exact
expected persisted rank, rank cap, player level, persisted point balance, and
server-calculated cost.

Transport replay defense and business idempotency remain separate:

- TLS/TCP framing and the secure session protect delivery and association.
- UDP/TCP sequence or replay windows protect transport packets.
- The B07 operation identity names one business transition.
- The B08 PostgreSQL inbox will prove whether that transition committed.

No account ID, character ID, operation ID, request hash, raw payload, account
name, character name, endpoint, credential, ticket, or session key belongs in
a metric label. Logs should use bounded reason/family codes and should avoid
raw payloads.

## Legacy compatibility

The original client can use this B07 command without a binary change or a
launcher-generated token. Both raw development TCP and the secure
launcher/gateway path carry the same existing 24-byte request, while the
server derives authenticated identity from the bound session.

This is intentionally a **command-specific** compatibility result. It does
not prove a general stable identity for inventory, forge, pet, character
lifecycle, currency, reward, or other valuable operations. Those families
must supply one of:

1. a naturally echoed expected aggregate version with one-transition
   semantics equivalent to this talent command;
2. a client/shim operation ID retained across retries; or
3. a server-issued token echoed by a compatible client/shim.

If none exists, the adapter must report an `unsupported_legacy_retry`
identity strength and must not pretend that connection-local suppression is
durable idempotency.

B07 also adds a bounded process-local attempt registry. It correlates an
operation ID with its canonical request hash across handler/connection
replacement, classifies pending/completed duplicates, rejects a different
hash for the same operation, releases failed preconditions for retry, and
expires/evicts entries within fixed limits. It is defense-in-depth only:
PostgreSQL rank state remains authoritative, and B08 replaces its result/hash
role with the durable transactional inbox.

## Rejected candidates

### Pet level

`Opcodes.PetLevelRequest` (`10285`) carries only the pet ID in its exact
eight-byte request. Two intentional level-up clicks can therefore be
byte-for-byte identical, and the packet contains no expected pet level,
revision, or operation token. Current pet persistence also creates audit
GUIDs inside the store per receipt; those GUIDs cannot identify a retry.
Using packet bytes, arrival time, TCP sequence, or a new server GUID would
either merge legitimate actions or execute retries twice.

### Forge and enhancement

Forge attempts intentionally repeat the same gear/material/slot combination.
A failed attempt followed by another real attempt can be indistinguishable
from a lost-ack retry, and the captured request has no proven echoed item
revision or operation token. Selecting forge would therefore require a shim
or compatible-client token before B08 could safely retain results.

Talent upgrade avoids both ambiguities because expected rank identifies one
and only one rank transition.

## B08 handoff

B08 must add a PostgreSQL command inbox keyed by the authenticated operation
scope and a request hash. For talent upgrade, the following must occur in one
transaction:

1. lock or conditionally validate the authenticated character and talent;
2. read/insert the inbox identity;
3. reject same identity/different hash;
4. validate expected rank, level, rank cap, and persisted points;
5. increment rank and debit the server-calculated cost;
6. persist the canonical result and audit reference in the inbox;
7. append the versioned outbox event;
8. commit before a success acknowledgement.

An exact retry, including one on a different connection, reads and returns the
stored result. A disconnect after commit is not an uncertain second spend.
PostgreSQL remains the authoritative value owner; no Redis or in-memory cache
may decide whether the mutation committed.

B08 must also define result/token retention. Once a committed identity is
purged, an old operation cannot silently become new. Expired identities must
be rejected or covered by a longer durable ledger/audit rule.

## Required verification

B07 coverage must prove:

- exact 24-byte decoder acceptance and truncated/oversized/negative rejection;
- positive authenticated IDs are server-derived and payload bytes cannot
  replace them;
- deterministic operation identity for the same principal, character,
  talent, and expected rank;
- identity stability when session/connection generation changes;
- a different identity for the next expected rank and for another talent;
- canonical hash stability despite irrelevant framing/reserved bytes;
- unchanged talent request hash when only ignored/reserved/UI-echo fields
  change;
- process-local pending/completed duplicate and same-operation/different-hash
  classification for the generic command boundary;
- one legitimate rank after another is not treated as a duplicate;
- JSON replay rejection and a legitimate next-rank transition;
- PostgreSQL concurrent same-rank requests commit exactly once under the
  existing row lock;
- raw legacy TCP and secure gateway decoding produce the same envelope;
- unsupported pet/forge retries are not assigned fabricated durable IDs.

B08 adds crash-after-commit/reconnect, durable hash-conflict, stored-result,
inbox retention, and outbox delivery proofs. B07 tests alone must not be
described as durable exactly-once execution.

## Observability

The command boundary publishes low-cardinality instruments:

- `godswar_commands_total` with bounded `family`, `identity_strength`, and
  `outcome`;
- `godswar_legacy_commands_without_retry_identity_total` with a reviewed
  command-family code; this counts attempts exposed to unsupported retry
  semantics, not retries the server cannot reliably detect.

Expected outcomes include `accepted`, `malformed`, `invalid_intent`,
`stale_precondition`, `precondition_failed`, `duplicate`,
`request_hash_conflict`, `provider_unavailable`, and `cancelled`. B07 records
envelope/handler outcomes; B08 adds durable inbox/outbox outcome and latency
metrics.

## Repository evidence

| Concern | Repository location |
| --- | --- |
| Roadmap transport and command rules | `docs/data-architecture-roadmap/09-udp-tcp-integration.md` |
| Identity/inbox rules | `docs/data-architecture-roadmap/10-consistency-messaging-strategy.md` |
| Legacy opcode routing | `src/Godswar.Server/Protocol/Opcodes.cs`; `src/Godswar.Server/Game/GameClientHandler.cs` |
| Generic envelope, hashes, bounded attempt registry, metrics, and legacy policy | `src/Godswar.Server/Application/Commands` |
| Talent command contract | `src/Godswar.Server/Application/Talents/TalentUpgradeCommandEnvelope.cs` |
| Captured request decoder | `src/Godswar.Server/Game/GameClientHandler.PacketDecoding.cs::TryReadTalentUpgrade` |
| Legacy application adapter | `src/Godswar.Server/Game/LegacyTalentUpgradeCommandAdapter.cs` |
| Authenticated handler inputs | `src/Godswar.Server/Game/GameClientHandler.InventoryActions.cs::HandleUseOrEquipAsync` |
| Current PostgreSQL mutation | `src/Godswar.Server/State/PostgresGameStore.SkillsAndTalents.cs::UpgradeTalentAsync` |
| Current JSON compatibility mutation | `src/Godswar.Server/State/JsonGameStore.Progression.cs::UpgradeTalentAsync` |
| Rank/cost/level rules | `src/Godswar.Server/Application/Talents/TalentProgression.cs` |
| Current broad-store contract | `src/Godswar.Server/State/IGameStore.cs::UpgradeTalentAsync` |
| Architecture ratchet allowance | `tests/Godswar.Server.ProtocolChecks/DataBoundaryArchitectureBaseline.cs` |
| Envelope/metrics/compatibility proofs | `tests/Godswar.Server.ProtocolChecks/LegacyTalentCommandEnvelopeChecks.cs` |
| PostgreSQL ownership/race/replay proof | `tests/Godswar.Server.ProtocolChecks/PostgresTalentUpgradeIntegrationChecks.cs` |
| Mandatory PostgreSQL gate registration | `tools/InvokeB03PostgresCiGate.ps1` |

## Rollback and limitations

The per-command legacy adapter is the rollback boundary. Rolling it back
restores the prior talent handler but also restores replayable rank/point
mutation behavior; it is not a production-safe long-term mode.

Known limits:

- only talent upgrade has a selected legacy identity;
- B07 has no durable inbox/outbox migration, stored command result, or
  committed request hash; the expected-rank guard prevents a second mutation
  but cannot replay the original result after process/database restart;
- the bounded attempt registry survives connection replacement only within
  the current server process and is never an authoritative commit record;
- a client points echo is bounded diagnostics only, not equivalence or
  authority;
- session generation is diagnostic and must never enter the operation key;
- JSON remains a local-development compatibility provider, not the
  production authority for durable idempotency;
- B15's PostgreSQL player-ownership fence is still required before safe
  multi-process player ownership.

## Next dependency

B08 can now implement the PostgreSQL inbox/outbox vertical slice around this
exact talent transition without inventing an operation identity at the
persistence layer.

## Verification receipts

Completed on 2026-07-29:

- Release solution build: succeeded with zero warnings and zero errors;
- focused boundary/envelope/warrior talent checks: 4 passed, 0 failed;
- full protocol suite: 193 passed, 0 failed;
- mandatory disposable PostgreSQL 17 gate: 15 required checks and three
  migration scenarios passed, including the B07 concurrent talent command
  check;
- architecture ratchet: zero new debt, zero stale debt, and zero rule
  violations;
- changed files remain below the repository's 20 KB maintainability limit.
