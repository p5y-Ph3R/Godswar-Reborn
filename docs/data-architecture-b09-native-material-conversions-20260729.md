# B09 secure native material-conversion increment

Date: 2026-07-29

Source base: `eef2e51dedb92f7572cf03f575258a14793813d7`

Status: increment complete; B09 remains open

## Outcome

Gear Mentor **Transform Crystals** and **Combine Gem Pieces** now use the
secure, durable inventory-command path previously established for Make
Attribute Stone:

```text
stock final packet
  -> family-scoped native operation UUID
  -> authenticated TLS 0x0101 marker
  -> validated application command envelope
  -> one PostgreSQL inventory transaction
  -> stock result + authoritative bag refresh
  -> authenticated terminal 0x0102 result
  -> native pending-operation completion
```

The server remains authoritative. The client supplies only the physical Gear
Mentor, selected bag slot, and captured item snapshot. The transaction locks
and revalidates the current character and bag, resolves the recipe through
`GearMentorPlanner`, and either commits every inventory/ledger/inbox/outbox
change or commits no mutation.

No schema migration was required. The implementation uses migrations 025/026
for permanent inbox, audit, and strict outbox state and migrations 027/028 for
inventory revision and immutable item-ledger evidence.

## Native operation identity

The Win32 shim accepts only the audited Gear Mentor packet shape:

- legacy opcode `10069`;
- exact clear packet length `92`;
- physical Gear Mentor NPC `5067` or `5209`;
- dialog `4`; and
- an exact stock action from `-1` or `1..9`.

Transform's final wire action `8` receives command family `7`. Combine is a
two-step stock interaction: the first wire action `9` arms page `201`, an exact
opcode `10193` item selection establishes the slot, and the second wire action
`9` receives command family `8`. Other Gear Mentor actions clear stale Combine
page/selection state instead of inheriting it.

Pending identity is scoped by family, authenticated login principal,
authenticated character, physical NPC, and selected bag slot. A retry of the
same unresolved operation reuses its UUID. Transform and Combine cannot reuse
one another's identity. The existing bounded registry still enforces:

- at most 16 pending entries and 16 completed tombstones;
- a non-refreshing ten-minute lifetime;
- Windows CSPRNG UUIDv4 generation;
- same-account A/B/A character isolation;
- descriptor/clear-packet ordering across split and coalesced stock writes;
- session teardown after a partial failed stock write; and
- fail-closed handling for malformed, unknown, expired, or wrong-family
  terminal results.

## Command families and stock results

The authenticated `0x0102` terminal frame remains the fixed 32-byte protocol
defined in
[secure legacy command results](network-infrastructure-secure-command-results.md).
This increment assigns two stable families:

| Family | Operation | Stock success | Stock rejection results |
| ---: | --- | ---: | --- |
| `7` | Transform Crystal | `1823` | invalid/stale/slot `1822`; capacity `1020` |
| `8` | Combine Gem Pieces | `304` | invalid/stale/slot `301`; fewer than 99 pieces `302`; capacity `303` |

`Applied` and `Replayed` settle a durable inbox success. A stored business
failure or proven pre-mutation routing failure uses `Rejected`; a reused UUID
with different canonical request content uses `Conflict`. Database,
cancellation, send, projection-load, or unknown-commit failures do not emit a
terminal result, leaving the UUID available for truthful retry.

## Application and PostgreSQL boundary

`GearMentorTransformCrystalCommandEnvelope` and
`GearMentorCombineGemPiecesCommandEnvelope` accept only authenticated secure
provenance. Their canonical request contains the family, server-derived
account/character subject, client UUID, physical NPC, selected slot, and exact
expected item snapshot. Maximum slot, string, and payload sizes are bounded.

`PostgresGearMentorMaterialConversionCommandExecutor` is split into focused
partial files below the repository's 20 KB limit. For both families it:

1. validates the family-specific command envelope;
2. locks the authoritative character row;
3. checks the permanent inbox before mutation;
4. rejects a request-hash conflict for the same UUID;
5. ensures the opening economy baseline;
6. locks and decodes all authoritative bag rows;
7. resolves and plans the exact recipe;
8. stores the command audit and canonical inbox receipt;
9. applies all item additions, updates, and deletions;
10. advances `inventory_revision` exactly once on success;
11. appends immutable item-ledger entries;
12. appends one family-specific strict outbox event; and
13. commits before returning the outcome.

The persistent families, event types, and ledger reasons are deliberately
distinct:

| Operation | Inbox family / ledger reason | Outbox event |
| --- | --- | --- |
| Transform | `gear_mentor_transform_crystal` | `inventory.gear_mentor_crystal_transformed` |
| Combine | `gear_mentor_combine_gem_pieces` | `inventory.gear_mentor_gem_pieces_combined` |

Both events use the existing strict `character_inventory` aggregate stream.
Terminal business rejections store a canonical audit/inbox receipt but do not
advance inventory revision, write a ledger mutation, or publish an outbox
event. Exact retries return that stored receipt.

The complete authoritative recipes remain documented in
[Gear Mentor material workflows](gear-mentor-material-workflows.md). All four
Crystal downgrade recipes and all five L4/L5 piece-combination recipes are
covered by disposable PostgreSQL tests.

## Handler ordering and recovery

Only UUID-bearing secure packets enter the durable executor. Tokenless traffic
is measured as unsupported command identity and remains on the explicit legacy
compatibility path.

The handler consumes its ephemeral selected-item state before awaiting the
database. A terminal durable outcome then:

1. reloads the authoritative PostgreSQL inventory projection;
2. sends the stock NPC result;
3. sends deletion acknowledgements for changed occupied slots;
4. sends the complete bag detail and slot-index refresh; and
5. sends authenticated `0x0102` last through the serialized TLS write gate.

If selection state disappeared after reconnect, the executor first searches
the permanent inbox by authenticated subject, family, and UUID. Durable replay
also runs before an unknown-NPC, wrong-route, or wrong-behavior rejection, so a
commit followed by disconnect cannot be misreported as a new routing failure.
Replay reloads the authoritative bag and preserves the same stock-before-secure
response ordering.

An inbox miss continues normal pre-mutation rejection. An unavailable
repository, route-reader failure, unknown commit, or failed authoritative
projection reload sends no terminal result.

## Verification

The completed working tree passed:

- Release solution build: zero warnings, zero errors;
- full managed protocol harness: `205` passed, `0` failed;
- focused command-envelope, result-code, planner, outbox-consumer, retry-route,
  and TLS response-order checks;
- mandatory B03 PostgreSQL gate: `24` required checks and all `3` migration
  scenarios, with successful cleanup;
- all Transform and Combine recipes and add/update/delete stack branches;
- durable rejection replay, same-UUID replay/conflict, cross-family UUID
  isolation, concurrent duplicate and distinct-UUID races;
- every injected pre-commit fault plus post-commit lost-response recovery;
- serial Win32 Release shim build with `/W4 /WX`; and
- native offline and full check suites.

The final Release `Net.dll` is 237,056 bytes with SHA-256:

```text
BE77AB891F5C585493795FF6460ABF308981B0539C13D1A13A2BD6077DC02B4E
```

The machine-readable B03 receipt is
`artifacts/b03/b09-native-material-conversions-result.json` (10,987 bytes),
SHA-256:

```text
C82CFF14C4659161CA3144C2DACFD444440E9B0ED6B79A9EDFBB5E3E5A014131
```

It reports `passed`, duration `243590` ms, 24 checks, 3 scenarios, and passed
cleanup. These are local development results, not production capacity claims.

## Rollback and remaining B09 work

Rollback requires a matched prior server and shim binary. Permanent command
inbox, audit, ledger, and outbox evidence remains valid and must not be
deleted. The shared inventory consumer remains compatible with earlier event
types.

The native retry identity lasts ten minutes, while the PostgreSQL inbox is
permanent. After native expiry, login still restores the authoritative bag,
but the stock client no longer has that old operation UUID.

A live proprietary-client smoke is still required after installing the
matching rebuilt shim and server. Automated checks exercise the production
classifier, registry, framing, handler, executor, and outbox components but do
not launch `Origin.exe`.

B09 remains open. Decompose and Gear Add/Enhance/Delete are the next Mentor
inventory mutations. Forge follows after its inventory-plus-wallet transaction
can persist and replay the exact random outcome. Other tokenless inventory,
reward, and currency mutations also remain compatibility paths.
