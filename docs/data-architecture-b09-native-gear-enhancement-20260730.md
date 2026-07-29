# B09 secure native Gear Enhancement increment

Date: 2026-07-30

Source base: `2a538fce155f5cc8fcf1d6c828b9adcc1701eba3`

Status: increment complete; B09 remains open

## Outcome

Gear Mentor and Origin Enhancer **Enhance**, **Add**, and **Delete**
attribute operations now share one secure, authoritative PostgreSQL boundary:

```text
stock selection and final action
  -> family-scoped native operation UUID
  -> authenticated TLS 0x0101 marker
  -> bounded application command envelope
  -> locked PostgreSQL inventory transaction
  -> stock result + authoritative bag refresh
  -> authenticated terminal 0x0102 result
```

The client proposes the gear, catalyst, and attribute-stone bag references.
The server binds their exact item snapshots, reloads and locks the complete
bag, runs the existing deterministic `GearEnhancementPlanner`, and either
commits all three item mutations or none.

No schema migration is required. The command uses the permanent inbox,
economy audit, inventory revision, immutable item ledger, and strict outbox
introduced by migrations 025 through 028.

## Native identity

The three stable secure command families are:

| Family | Operation | Stock success |
| ---: | --- | ---: |
| `10` | Enhance Attribute | `1010` |
| `11` | Add Attribute | `1013` |
| `12` | Delete Attribute | `1030` |

The physical Gear Mentors use NPC `5067` or `5209`, dialog `4`, and stock
actions `2`, `3`, or `6`. The shim requires exactly three ordered opcode
`10193` selections in Gear, Catalyst, Attribute Stone order. An intact,
complete staged triplet is accepted while no clear has begun. The stock client
normally clears those controls immediately before sending the final action;
after any clear begins, the shim accepts only the complete, same-order clear
burst and retains its immutable snapshot for one second from the final clear.
Partial, reordered, extra, or expired clears do not receive an operation UUID.

The Origin Enhancers use NPC `5140` or `5282`, dialog `118`, and the same
actions. Their exact 92-byte final packet carries kit-bag references in
arguments 6, 7, and 8; references `100..195` map to bag slots `0..95`. Every
other argument must remain `-1`. An all-`-1` operation packet is navigation,
creates no UUID, and clears stale physical selection/Combine state.

The pending-operation identity contains family, authenticated principal,
authenticated character, endpoint NPC, and the exact role-ordered slot
triplet. An exact retry or reconnect reuses its UUID. A different operation,
role order, endpoint, account, or character cannot alias it. Origin commits do
not bind settlement to mutable physical-selection state.

## Application contract

`GearEnhancementCommandEnvelope` accepts only authenticated secure command
provenance. Its canonical version-1 request contains:

- client operation UUID;
- server-derived account and character;
- operation family;
- exact NPC and dialog;
- Gear, Catalyst, and Attribute Stone role tags;
- three distinct bounded kit-bag slots; and
- each exact canonical compact item state.

Per-state bytes, combined state bytes, slots, roles, and total canonical
request size are bounded. The operation ID derives from family, subject, and
UUID. Reusing a UUID with different canonical content produces a permanent
request-hash conflict instead of another mutation.

The result contract stores the originating endpoint, finite stock result,
inventory revision, audit reference, optional outbox event ID, and exact
before/after mutation evidence in role order.

## PostgreSQL transaction

`PostgresGearEnhancementCommandExecutor` performs one transaction:

1. validate secure provenance and canonical item snapshots;
2. lock the owned character;
3. check the family-specific permanent inbox;
4. reject a same-UUID request-hash conflict;
5. ensure the opening economy baseline;
6. lock and decode the authoritative kit bag;
7. revalidate and plan the requested operation;
8. write the economy audit and canonical inbox receipt;
9. update the gear and consume one catalyst and one attribute stone;
10. advance `inventory_revision` exactly once;
11. append three immutable inventory-ledger entries;
12. append one strict inventory outbox event; and
13. commit before returning success.

Persistent identifiers remain family-specific:

| Operation | Inbox family / ledger reason | Outbox event |
| --- | --- | --- |
| Enhance | `gear_mentor_enhance_attribute` | `inventory.gear_mentor_attribute_enhanced` |
| Add | `gear_mentor_add_attribute` | `inventory.gear_mentor_attribute_added` |
| Delete | `gear_mentor_delete_attribute` | `inventory.gear_mentor_attribute_deleted` |

Terminal planner rejection writes an audit and canonical inbox receipt but
does not mutate inventory, advance its revision, append ledger entries, or
publish an event. Exact retries return the stored success or rejection.

## Handler response and recovery

Only UUID-bearing secure packets use the durable executor. Tokenless traffic
retains the existing compatibility transaction and does not gain
cross-reconnect idempotency.

After a permanent result, the handler reloads the authoritative PostgreSQL
character projection and sends:

1. the stock result on the receipt's original NPC/dialog;
2. deletion acknowledgements for consumed material slots when required;
3. the complete authoritative bag refresh; and
4. authenticated family `10`, `11`, or `12` result `0x0102` last.

Every UUID-bearing final action checks the permanent inbox before hashing a
new request. This lets a reconnect replay the original receipt even when the
UI was rebuilt from already-mutated authoritative items. An inbox miss with a
complete immutable selection proceeds as a new command; a miss without that
selection remains pending rather than inventing a rejection or unsafe
mutation. The same replay lookup runs before rejecting an unknown NPC, route,
or behavior.

Provider unavailability, cancellation, uncertain commit outcome, or
authoritative projection reload failure emits no terminal `0x0102`; the UUID
remains available for truthful retry.

## Verification

The completed working tree passed:

- Release solution build with zero warnings and errors;
- full managed protocol harness: `208` passed, `0` failed;
- focused command-contract, mixed strict-outbox, physical/Origin handler, and
  pre-route replay checks;
- durable PostgreSQL Add/Enhance/Delete success paths, including the Origin
  endpoint, binding propagation, attribute IDs and levels, stacked-material
  updates, material-row deletion, and exact Gear/Catalyst/Stone ledger order;
- permanent rejection, exact replay, request conflict, family isolation,
  same-UUID and distinct-UUID races, and lost-response recovery;
- injected rollback after audit, inbox, each of the three item writes,
  inventory revision, ledger, outbox, and immediately before commit;
- mandatory B03 PostgreSQL gate: `26` required checks and all `3` migration
  scenarios, with successful cleanup;
- serial Win32 Release build with `/W4 /WX`;
- native offline and full check suites; and
- changed-file size/line, patch, and documentation-link checks.

The final Release `Net.dll` is 241,152 bytes with SHA-256:

```text
9EC492FD33014F7DA34CA61F9F109BB79E8459EC90A152AFA4CAD115165336D2
```

The native check executable SHA-256 is:

```text
132F3E7C6B9294F0E10B63A1CC31CCB3CFDD843E3B66ECC4ACA5608D32471BF7
```

The machine-readable PostgreSQL receipt is
`artifacts/b03/b09-native-gear-enhancement-result.json` (11,731 bytes),
SHA-256:

```text
74AA788FF65C0289514FD69446524F918590341EBB169B93B8B223C3CC807B5D
```

It reports `passed`, duration `266979` ms, 26 checks, 3 scenarios, and passed
cleanup. These are local development results, not production-capacity claims.

## Rollback and remaining B09 work

Rollback requires a matched prior server and shim. Permanent inbox, audit,
ledger, and outbox rows are valid economy history and must remain.

The native retry identity lasts ten minutes while the PostgreSQL inbox is
permanent. After native expiry, a normal login still restores the
authoritative bag, but the stock client no longer retains the old UUID. In
particular, a later Origin Enhancer confirmation receives a new UUID and is a
new intent, not an indefinite replay of the expired operation. Its current
authoritative item snapshots are revalidated, but it may legitimately perform
another enhancement if the resulting gear and remaining materials permit it.
The ten-minute client idempotency window must not be described as permanent
retry identity; only an inbox row addressed by its retained UUID is permanent.
A live proprietary-client smoke remains required after installing the matched
shim and server.

B09 remains open. Forge is the next high-value inventory-plus-wallet mutation;
its exact random outcome must be stored and replayed. Other tokenless
inventory, reward, and currency paths remain compatibility work.
