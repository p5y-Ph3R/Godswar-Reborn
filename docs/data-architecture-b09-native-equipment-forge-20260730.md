# B09 secure native Equipment Forge increment

Date: 2026-07-30

Source base: `33aa5d7f48d824292b0cb6714e4dd384f2bd1649`

Status: increment implemented and verified; B09 remains open

## Outcome

Ordinary equipment forging is moving behind one secure, authoritative
PostgreSQL transaction:

```text
stock Forge selections and Start
  -> family-3 native operation UUID
  -> authenticated TLS 0x0101 marker
  -> bounded application command envelope
  -> locked PostgreSQL inventory-and-wallet transaction
  -> stock Forge result + authoritative status/bag refresh
  -> authenticated family-3 terminal 0x0102 result
```

The client proposes only kit-bag references and requested odds-crystal
quantities. The server captures their exact authoritative item states, locks
and reloads the full bag and character economy aggregate, validates the
existing Forge rules, and samples the outcome itself.

Both successful and failed rolls are committed attempts. They consume the
primary material, every reserved odds crystal, and the configured Silver
cost. Only a successful roll changes the equipment. The exact roll,
probability, outcome, mutations, revisions, audit, and outbox event are
stored before any terminal response.

No schema migration is required. The command uses the permanent inbox,
economy audit, wallet and inventory revisions, immutable currency and item
ledgers, and strict inventory outbox introduced by migrations 025 through
028.

## Native identity

Equipment Forge uses secure command family `3`.

The shim recognizes only the exact ordinary-Forge packet shapes:

- opcode `10110`, exact 60-byte selection packet;
- mode `0`;
- destination `0` for equipment;
- destination `1` for the primary Ruby, Sapphire, or Emerald;
- destination `5` for an odds-crystal descriptor;
- action `88` for one linked odds-crystal increment;
- opcode `10109`, exact 40-byte Start packet; and
- opcode `10117`, exact 4-byte cancellation packet.

The action-88 scratch descriptor is not trusted. Each increment must link to
a previously validated destination-5 descriptor, and the server still
revalidates every authoritative stack. Odds selections may span multiple
stacks of one crystal item ID, are ordered by kit-bag slot, and have a total
quantity limit of 25.

The retry key contains family, authenticated principal, authenticated
character, ordinary mode, equipment slot, primary-material slot, and the
sorted odds `(slot, quantity)` list. An exact duplicate Start reuses the
pending UUID. A terminal family-3 result settles it; a later newly staged
attempt receives a new UUID.

Cancellation clears the mutable staging state without erasing pending retry
identity. Replacement modes, malformed packets, incomplete descriptor
linkage, excess quantity, account changes, and character changes cannot
create or alias an ordinary-Forge UUID.

## Application contract

`EquipmentForgeCommandEnvelope` accepts only authenticated secure command
provenance. Its canonical version-1 request binds:

- client operation UUID;
- server-derived account and character;
- equipment and primary-material role tags, distinct slots, quantity one,
  and exact canonical compact item states;
- zero to 25 role-tagged odds selections in ascending slot order;
- each odds quantity and a total odds quantity no greater than 25; and
- a SHA-256 digest of every bounded server-captured compact item state.

The combined source states are bounded before hashing, and the fixed-size
canonical request remains within the shared command-envelope limit. Reusing
the UUID with different canonical content produces a permanent request-hash
conflict instead of another attempt.

The durable receipt records a finite status, material type, exact roll,
success probability, Silver spent, equipment before and after, every
material stack transition, wallet and inventory revisions, audit reference,
and strict outbox event identity.

## PostgreSQL transaction

`PostgresEquipmentForgeCommandExecutor` performs one transaction:

1. validate secure provenance and canonical item snapshots;
2. lock the character and verify account ownership;
3. check the permanent family-3 inbox before evaluating current state;
4. replay an exact request or reject a same-UUID hash conflict;
5. ensure the opening economy baseline;
6. lock and decode the complete authoritative kit bag;
7. validate slots, states, stacks, material compatibility, limits, and
   Silver without sampling randomness;
8. persist terminal invalid/stale/insufficient results without mutating the
   economy;
9. for a valid attempt, sample one cryptographically secure roll in
   `[0, 99]`;
10. build the canonical receipt and persist its audit and permanent inbox
    evidence;
11. consume the primary material and odds crystals in deterministic slot
    order, and update equipment only on success;
12. debit Silver and advance `wallet_revision` only when the configured cost
    is positive;
13. advance `inventory_revision` exactly once;
14. append only real currency and inventory mutations to immutable ledgers;
15. append one strict `inventory.equipment_forged` outbox event; and
16. commit before returning a terminal result.

A legitimate zero-Silver Forge does not advance the wallet revision and does
not attempt a forbidden zero-delta currency-ledger row. It still consumes
materials, advances the inventory revision, and publishes its committed
inventory event.

A failed roll does not write a no-op equipment ledger entry. Its material
consumptions, positive Silver debit, inventory revision, audit, inbox, and
outbox event remain durable. Exact retry reads that stored receipt and never
samples another roll.

Terminal validation rejection writes audit and inbox evidence only. It does
not consume items or Silver, advance either revision, append mutation-ledger
rows, or publish an event. This makes a stale retry permanently truthful even
if the bag later changes.

## Handler response and recovery

Only UUID-bearing secure Start packets use the new executor. Tokenless raw
legacy traffic retains the prior compatibility transaction and does not gain
cross-reconnect idempotency.

Every secure Start first checks the permanent inbox. This permits replay
after reconnect even when the stock UI selection has disappeared and the
authoritative bag already reflects the attempt. An inbox miss requires one
complete, unexpired server-captured selection. The handler clears that
selection before awaiting persistence so a concurrent duplicate cannot
consume it twice.

For a durable committed attempt or durable terminal rejection, the handler
sends:

1. stock opcode `10109`, with result kind `1` for both a successful roll and
   a committed failed roll, or kind `0` for a validation rejection;
2. authoritative local-player status;
3. the complete authoritative bag refresh; and
4. authenticated family-3 result `0x0102` last.

The PostgreSQL projection refresh replaces only durable Silver and kit-bag
state. Runtime position, vitals, mount state, and other live ECS state are
not overwritten.

Provider unavailability, cancellation, an uncertain commit outcome, or a
failed authoritative projection reload emits neither a stock terminal result
nor `0x0102`. The client UUID remains pending so a later retry can discover
and replay the permanent outcome.

## Verification

The final frozen tree passed:

- `dotnet build GodswarServer.sln --no-restore -c Release`: zero warnings
  and zero errors;
- the complete managed protocol harness: 211 passed, zero failed;
- the focused data-boundary, legacy identity, Forge contract, durable
  handler/replay, packet protocol, and outbox checks;
- the strict Win32 Release network-shim build with `/W4 /WX`;
- native offline checks and the complete native check suite;
- the mandatory B03 PostgreSQL 17 gate: 27 of 27 required checks and all
  three migration scenarios passed, with database cleanup passed; and
- independent transaction, identity, security, schema, file-size, and
  replay review with no blocking finding.

The B03 run took 269,679 ms. It applied and verified all 29 migrations
through `20260729_028_economy_ledger_hardening`. Its Forge transaction check
ran against a disposable database without skips. The machine-readable
report is
`artifacts/b03/b09-native-equipment-forge-result.json` (12,100 bytes,
SHA-256
`7E49D06557FA3F69C797D39AA50D7E76375FD67A6BD4642FCA55A6ED3D69D331`).
No `godswar_b03_*` database remained after the gate.

The rebuilt native artifacts were:

- `Net.dll` SHA-256
  `50132986B73DA245C114E74B51D99CBECF306DC7AC37A020CAFF1E031CC8F18B`;
  and
- `Godswar.NetShim.Checks.exe` SHA-256
  `A6065740679BF4494DEBF30A26006EBA4F983A5D364F67312CA040F28C7D204D`.

## Rollback and remaining B09 work

Rollback requires a matched prior server and shim. Permanent Forge inbox,
audit, ledger, and outbox rows are valid economy history and must remain.

The native retry identity lasts ten minutes while the PostgreSQL inbox is
permanent. Only a client that retains the original UUID can address that
permanent receipt after reconnect; a newly staged Forge is a new intent.

B09 remains open after this increment. Tokenless inventory, reward, and
currency compatibility paths still require explicit operation identity and
operation-specific durable transactions.
