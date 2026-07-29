# B09 secure native Make Attribute Stone increment

Date: 2026-07-29

Source base: `49ed0b48c618a9f9fbca49cf7cae5a273387e3ea`

Status: increment complete; B09 remains open

## Outcome

Gear Mentor **Make Attribute Stone** is the first stock-client inventory
operation to cross the complete secure retry boundary:

```text
stock final packet
  -> bounded native operation UUID
  -> authenticated TLS 0x0101 marker
  -> application command envelope
  -> one PostgreSQL inventory transaction
  -> legacy result + authoritative bag refresh
  -> authenticated terminal 0x0102 result
  -> native pending-operation completion
```

The server remains authoritative. The client supplies only a selected bag slot
and the previously observed item snapshot. PostgreSQL locks and revalidates the
current character and bag before consuming 99 matching Attribute Dust and
creating one matching Attribute Stone.

This increment adds no schema migration. It uses the permanent command inbox,
audit, and strict outbox stream introduced by migrations 025/026, plus the
inventory revision and immutable inventory ledger introduced by migrations
027/028.

## Native operation identity

The secure Win32 shim now recognizes only the audited final action:

- legacy opcode `10069`;
- exact clear packet length `92`;
- physical Gear Mentor NPC `5067` or `5209`;
- dialog `4`;
- action sub-ID `4`.

Opcode `10193` selection packets supply the semantic bag slot. Scratch values
inside the final action are not trusted.

`SecureClientRuntime` owns a process-wide registry rather than tying pending
identity to one proxy or socket:

- at most 16 pending operations;
- scope is the fixed login username fingerprint plus the positive character ID
  observed from an exact authenticated server `EnterMain` (`0x2723`) packet;
- non-refreshing ten-minute pending lifetime;
- Windows CSPRNG UUIDv4 generation;
- the same unresolved principal/character/NPC/slot reuses its UUID across
  reconnect;
- character changes clear only the ephemeral selection, retaining pending
  identities so an A/B/A switch yields U1/U2/U1 after each reselection;
- a genuinely different canonical request with that UUID is rejected by the
  server request hash rather than guessed fresh by the shim; and
- a separate bounded 16-entry, ten-minute tombstone set absorbs duplicate
  terminal results while wrong-family and unknown results still fail closed.

`LegacyCommandDescriptorStream` describes every secure-game stock send before
the stock `SendMsg` call. Because the stock cipher preserves length, the stream
can associate split or coalesced ciphertext with exact clear packet
boundaries. It writes `0x0101` immediately before the valuable packet's
`LegacyBytes` frame while holding one outer write lock. Missing, partial,
misordered, or capacity-exceeding descriptors stop the secure channel.
If stock `SendMsg` fails before a descriptor starts, the descriptor is
cancelled. If ciphertext already consumed any part of it, the shim tears down
the secure session and resets coordination instead of letting a later send
inherit the remainder; the process registry retains the UUID for retry.

## Server command-result protocol

Server-to-client frame `0x0102` has a fixed 32-byte payload:

| Offset | Size | Value |
| ---: | ---: | --- |
| `0` | `1` | version `1` |
| `1` | `1` | Applied, Replayed, Rejected, or Conflict |
| `2` | `2` | big-endian command family `6` |
| `4` | `4` | big-endian native result sub-ID |
| `8` | `8` | big-endian inventory revision at the outcome |
| `16` | `16` | RFC 4122/network-order client UUID |

It is valid only server-to-client on an authenticated, bound secure game
channel. The shim consumes it outside the stock byte stream. Exact duplicate
results are idempotent; malformed, contradictory-family, expired, and unknown
results fail closed.

`Applied` and `Replayed` identify durable inbox outcomes. A deterministic
pre-dispatch rejection can use `Rejected` only when no authoritative mutation
could have started. Transient database, cancellation, unknown-commit, and
write failures do not send a terminal result, preserving the UUID for retry.

## Durable PostgreSQL transaction

`PostgresMakeAttributeStoneCommandExecutor` performs this order while holding
the character row lock:

1. validate secure command provenance and the canonical item snapshot;
2. derive the account/character/family operation digest;
3. read the permanent inbox before any mutation;
4. reject the same UUID with a different request hash;
5. ensure the character's opening economy baseline;
6. lock all authoritative kit-bag rows;
7. run `GearMentorPlanner` against the locked projection;
8. append the immutable command audit and canonical inbox result;
9. apply every add/update/delete mutation;
10. advance `inventory_revision` exactly once;
11. append one immutable ledger entry per item instance;
12. append the strict `inventory_projection_v1` outbox event; and
13. commit before returning success.

The stored receipt records the selected slot, source Dust, output Stone,
binding, native result, audit reference, inventory revision, and event ID.
Terminal business rejections are stored and replayed without advancing the
inventory revision or publishing an event.

An exact retry returns the stored receipt. `TryReplayAsync` needs only the
authenticated account/character subject and client UUID, so it works after
the ephemeral NPC selection and connection are gone.

## Handler ordering and client reconciliation

The live handler routes only a secure UUID-bearing Make Attribute Stone packet
to the durable executor. Raw/tokenless traffic retains the measured legacy
compatibility path.

For a terminal durable outcome, the server:

1. reloads the authoritative bag projection;
2. writes the stock NPC result;
3. writes any required deletion acknowledgements;
4. writes the complete bag detail and slot-index refresh; and
5. writes `0x0102` last through the same serialized TLS outer write gate.

All durable rejections also receive a full authoritative bag refresh. Early
no-character, malformed, wrong-map, wrong-dialogue-route, and wrong-NPC
behavior exits complete an attached secure UUID with a deterministic
pre-mutation rejection rather than stranding it until expiry.

If persistence or post-commit projection loading has an unknown outcome, the
server sends no terminal result. A retry then resolves the permanent inbox
before requiring session-scoped selection state.

## Verification

The completed working tree passed:

- Release solution build: zero warnings, zero errors;
- full server protocol harness: `202` passed, `0` failed;
- data-boundary ratchet: `0` new, `0` stale, `0` rule violations;
- focused command-contract, codec, TLS ordering, and shared inventory-outbox
  checks;
- Win32 Release shim build;
- native offline and full check suites;
- focused disposable-PostgreSQL transaction check; and
- mandatory B03 gate: `23` required checks and all `3` migration scenarios,
  including successful cleanup.

The PostgreSQL campaign covers commit/replay/conflict, replay without selection
context, concurrent executors, terminal rejection replay, every pre-commit
fault probe, and post-commit lost-acknowledgement recovery. Additional fixtures
cover the planner's add/update/delete mutation combinations, principal
business-rejection statuses, and atomic opening-baseline rollback plus retry.
Native checks also cover same-account A/B/A character switching and prove that
a stock send failure after partial ciphertext cannot contaminate the next send.

The final Release `Net.dll` is 236,544 bytes with SHA-256:

```text
6D7AE189A2BBC9709F8C8E091E01A2435DEBB83124576959EC527B10CB0CA5EA
```

The machine-readable B03 receipt is
`artifacts/b03/b09-native-stone-result.json` (10,613 bytes), SHA-256:

```text
6F5609643B9A8B37A92481779AEB0140908AB2372B36DEC6AAA8AF0E52F544C3
```

It reports `passed`, duration `232649` ms, and successful disposable-database
cleanup. These are local development results, not production capacity claims.

## Rollback and limitations

Rollback is binary/configuration based:

- restore the previous server and shim together;
- raw local-development compatibility remains available only in its explicit
  profile;
- committed inbox, audit, ledger, and outbox evidence remains valid and must
  not be deleted; and
- the shared inventory consumer continues to accept earlier event types.

The operation-token retry window in the native process is ten minutes. The
PostgreSQL inbox is permanent, but once a native token expires the client no
longer possesses that identity. Login/reload still exposes the committed
authoritative bag before a new operation.

A live proprietary-client TLS smoke is still required after installing the
matching rebuilt shim and server binary. Automated tests exercise the exact
production registry, descriptor, framing, executor, and outbox components but
do not launch `Origin.exe`.

B09 remains open. Transform Crystals and Combine Gem Pieces should use the same
deterministic pattern next. Gear Add/Enhance/Delete and Decompose follow.
Forge remains later because it must atomically coordinate wallet and inventory
revisions and replay its stored random outcome exactly.
