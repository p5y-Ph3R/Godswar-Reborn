# B09 secure native Gear Mentor Decompose increment

Date: 2026-07-29

Source base: `0171624bdbbdcbf35e17b34626e1c3029dc78f98`

Status: increment complete; B09 remains open

## Outcome

Gear Mentor **Decompose Gear** now uses a secure operation UUID and one
authoritative PostgreSQL transaction:

```text
one-to-three stock selections + final action
  -> family-9 native operation UUID
  -> authenticated TLS 0x0101 marker
  -> bounded application command envelope
  -> locked PostgreSQL inventory transaction
  -> persisted exact random Dust outcomes
  -> stock result + authoritative bag refresh
  -> authenticated terminal 0x0102 result
```

The client proposes selected bag slots and exact item snapshots; it never
chooses eligibility, output Dust, output quantity, binding, or the resulting
inventory. The server locks and reloads the character bag, runs
`GearMentorPlanner`, and commits the complete result before acknowledging it.

No schema migration was required. The command uses the permanent inbox,
economy audit, strict outbox, inventory revision, and immutable item-ledger
tables introduced by migrations 025 through 028.

## Native identity and selection capture

The Win32 shim marks only this audited final packet shape:

- opcode `10069`;
- exact clear-packet length `92`;
- physical Gear Mentor NPC `5067` or `5209`;
- dialog `4`; and
- final action `1`.

Decompose is secure command family `9`. Its pending-operation key contains the
family, authenticated principal fingerprint, authenticated character, NPC,
and exact ordered list of one to three selected bag slots. An exact retry or
reconnect reuses the UUID; a different order, NPC, family, principal, or
character cannot alias it.

The stock client clears selected controls immediately before the final action.
The shim therefore retains a one-shot ordered selection snapshot for one
second. The full clear burst must match the captured controls exactly.
Partial, reordered, extra, or expired clears fail closed without creating an
operation identity.

The native suites cover all three selection counts, reconnect reuse, identity
isolation, result/tombstone settlement, expiry, and the existing families
`6..8`. One limitation remains explicit: live logs demonstrated the one-slot
sequence, while the stock UI and managed server support one-to-three items.
Multi-slot order currently follows observed `10193` selection/control order
and still receives full backend validation.

## Durable command and random outcome

`GearMentorDecomposeGearCommandEnvelope` accepts only authenticated secure
provenance. Its canonical version-1 request includes:

- the client operation UUID;
- server-derived account and character;
- physical NPC;
- one-to-three distinct ordered bag slots; and
- each exact compact item state.

Selection count, slots, per-state bytes, combined state bytes, and canonical
request size are bounded. The operation ID is derived from family, subject,
and UUID; the request hash detects a UUID reused for different request data.

Randomness occurs only while creating the first transaction's authoritative
plan. Production uses `RandomNumberGenerator.GetInt32`. The permanent receipt
stores one exact Dust item ID, quantity, and binding value for each source
slot. A duplicate or reconnect reads that receipt and never rerolls.

The existing explicit local drop rule remains documented in
[Gear Mentor material workflows](gear-mentor-material-workflows.md). It does
not claim parity with an unknown proprietary probability table.

## PostgreSQL transaction

`PostgresGearMentorDecomposeCommandExecutor` is split into focused partial
files below the repository size limit. A new command performs this sequence:

1. validate secure provenance and canonical item snapshots;
2. lock the owned character;
3. check the permanent command inbox;
4. reject a same-UUID request-hash conflict;
5. ensure the opening economy baseline;
6. lock and decode the authoritative bag;
7. revalidate and plan all selected gear and output capacity;
8. select exact Dust outcomes once;
9. insert economy audit and canonical inbox receipt;
10. apply every item delete/update/add;
11. advance `inventory_revision` once;
12. append immutable item-ledger entries;
13. append one strict inventory outbox event; and
14. commit before returning success.

The persistent identifiers are:

| Purpose | Value |
| --- | --- |
| Inbox family | `gear_mentor_decompose` |
| Ledger reason | `gear_mentor_decompose` |
| Outbox event | `inventory.gear_mentor_gear_decomposed` |
| Aggregate | `character_inventory` |
| Ordering | strict |

A terminal business rejection stores its canonical audit/inbox receipt but
does not mutate inventory, advance its revision, write an item-ledger entry,
or publish an event. Exact retries return the same stored success or
rejection. The existing `CharacterInventoryOutboxConsumer` owns the
projection checkpoint.

## Stock results and response order

The permanent receipt stores one of these finite native results:

| Result | Stock code |
| --- | ---: |
| Success | `1005` |
| Selection missing | `1024` |
| Player level too low | `1015` |
| Invalid equipment | `1003` |
| Equipment level too low | `1014` |
| Insufficient quality/grade | `1004` |
| Class Suit equipment | `1032` |
| Insufficient output capacity | `1020` |
| Stale expected item | `1002` |
| Invalid or duplicate selection | `1019` |

Capacity remains defensively mapped, although the current one-output-stack per
selected gear rule always frees enough slots; a completely full bag is tested.

Only secure UUID-bearing packets use this executor. Tokenless Decompose
remains an explicitly measured compatibility path.

After any permanent result, the handler reloads the PostgreSQL character
projection. It then sends:

1. the stock NPC result;
2. deletion acknowledgements for changed occupied source slots on success;
3. the complete authoritative bag refresh; and
4. authenticated family-9 `0x0102` last.

The handler resolves the permanent inbox before rejecting an unknown NPC,
route, or behavior on reconnect. Provider unavailability, cancellation,
unknown commit outcome, or projection reload failure sends no terminal
`0x0102`; the UUID remains pending for a truthful retry.

## Verification

The completed working tree passed:

- Release solution build with zero warnings and errors;
- full managed protocol harness: `207` passed, `0` failed;
- focused command-contract and pre-route replay-handler checks;
- disposable PostgreSQL integration coverage for success, every reachable
  business rejection, full-bag capacity, exact replay, conflict, races,
  injected transaction stages, lost-response recovery, ledger/outbox
  evidence, and cleanup;
- mandatory B03 PostgreSQL gate: `25` required checks and all `3`
  migration scenarios, with successful cleanup;
- serial Win32 Release build with `/W4 /WX`;
- native offline and full check suites; and
- repository size, patch, and documentation-link checks.

The final Release `Net.dll` is 240,128 bytes with SHA-256:

```text
F101D310A4065C8453238B8EC1579B5D44C48FC7B9471428AAA20EE65B38F54A
```

The machine-readable receipt is
`artifacts/b03/b09-native-decompose-result.json` (11,361 bytes), SHA-256:

```text
0D7DA0474671D9191DD9D1E508C159C347D35822BA44116E5E01E9D0B01EFD73
```

It reports `passed`, duration `243282` ms, 25 checks, 3 scenarios, and passed
cleanup. These are local development results, not production-capacity claims.

## Rollback and remaining B09 work

Rollback requires the matching prior server and shim. Permanent command inbox,
audit, ledger, and outbox records must remain; they are valid economy history
and compatible with the shared consumer.

After the native ten-minute identity window expires, a normal login still
loads the authoritative bag, but the stock client no longer retains that old
UUID. A live proprietary-client smoke remains required after installing the
matching `Net.dll`.

B09 remains open. Gear Mentor Add/Enhance/Delete are the next inventory
mutations. Forge should follow when its item-plus-wallet transaction can store
and replay its exact random outcome. Other tokenless inventory, reward, and
currency paths remain compatibility work.
