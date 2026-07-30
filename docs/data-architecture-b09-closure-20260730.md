# B09 inventory/currency ledger closure

Date: 2026-07-30
Status: completed for the currently reachable secure economy surface

## Outcome

B09 is complete under its documented boundary: every currently reachable
secure inventory, equipment, material, Forge, Holy Stone, Zodiac-grid, or
wallet mutation is either:

1. executed by an authoritative PostgreSQL transaction with durable command
   identity, audit/inbox evidence, and applicable ledger/outbox evidence; or
2. rejected before value can mutate when the stock wire contract cannot be
   identified safely.

This is not a claim that every planned gameplay feature is implemented.
Checkpoint persistence remains B10, character lifecycle remains B11, and
kill rewards, Zodiac accrual/level, pets, and the coupled right-click
bag-activation/pet-hatch command remain B12.

## Six-item closure

### 1. Zodiac SID 102 selection

Command family 21 now gives the stock 24-byte SID-102 request a stable native
UUID across reconnect. The managed boundary independently enforces the exact
length, module/SID, zero tail, grid range, row family, profession, learned
skill family, active grid, and duplicate-row rules.

`PostgresZodiacSkillGridSelectionCommandExecutor` locks the owning character
and atomically commits the selected skill Kind, audit, permanent inbox result,
and latest-wins outbox event. Exact retry returns the stored receipt;
UUID/request mismatch conflicts. The first successful commit emits SID 102,
then a full sync and family-21 `Applied`; replay suppresses a second native
animation and emits the current full sync plus `Replayed`.

Evidence:

- `client/network-shim/src/SecureZodiacSkillGridSelectionIdentity.*`
- `src/Godswar.Server/Application/Zodiac/ZodiacSkillGridSelection*`
- `src/Godswar.Server/Infrastructure/Zodiac/PostgresZodiacSkillGridSelection*`
- `src/Godswar.Server/Game/GameClientHandler.DurableZodiacSkillGridSelection.cs`
- `docs/data-architecture-b09-zodiac-grid-selection-20260730.md`

### 2. Transfer and right-click downgrade boundaries

Secure tokenless family-13 delete, family-14 bag move, and family-15
equipment/bag transfer requests now fail closed and refresh authoritative
state. Forge family 3 and Gear Mentor/Origin families 6-12 have the same
no-downgrade rule. Identified Gear Mentor/Origin actions must also be exactly
92 bytes on the server; durable replay is checked before a malformed retry is
settled.

Opcode 10051 carries only a bag slot and is shared by equipment activation
and pet-egg hatching. The native shim cannot truthfully choose an aggregate.
Secure opcode-10051 traffic therefore performs no compatibility mutation and
receives an authoritative refresh. Raw TCP retains its temporary,
non-idempotent compatibility behavior. A durable combined bag-item activation
command is assigned to B12 with pet hatch; no client item hint is trusted.

### 3. Advanced Holy Stone

Action 701 is explicitly recognized as Equipment Advance Drilling. Client
content proves Socket Spell III (`4272`) and IV (`4273`), but the repository
contains no clear client-to-server commit capture for argument roles,
equipment targeting, response IDs, or refresh order.

The exact empty page transition remains available. Every value-bearing or
malformed action-701 shape fails before UUID creation, inventory, Gold,
executor, or legacy-store access. This is a safe unsupported boundary, not a
fabricated implementation. Enabling third/fourth socket drilling requires
the capture matrix in
`data-architecture-b09-holy-stone-advanced-classification-20260730.md`.

### 4. Final mutation audit

The packet router and all direct `IGameStore` calls under
`src/Godswar.Server/Game` were re-audited. Supported secure economy mutations
are covered by families 3 and 6-21 or the existing durable talent command.
Recognized `PickupDrops`, `MoveItem`, `Sell`, and related inventory opcodes
are log-only today and cannot mutate value.

The finite classification, raw limitations, and ownership assignments are in
`data-architecture-b09-mutation-closure-audit-20260730.md`.

### 5. Additional trust-boundary hardening

The server no longer relies on the local shim as a validation boundary:

- Zodiac SID 100 requires `Value2=-1` and `Value3=0`;
- SID 101 requires `Value2=-1` and `Value3=0`;
- SID 102 requires `Value3=0`;
- identified Gear Mentor/Origin actions require declared and actual length
  92; and
- supported Holy Stone, Forge, bag, and equipment shapes retain their exact
  managed validation.

Invalid authenticated packets cannot reach either durable executors or raw
compatibility stores.

### 6. Frozen-tree gates

All required gates passed:

- `dotnet build GodswarServer.sln -c Release --no-restore`:
  **0 warnings, 0 errors**.
- Complete managed harness:
  **234 passed, 0 failed**.
- `tools/BuildClientNetworkShim.ps1 -Configuration Release`:
  **passed** under the strict native warning policy.
- Complete `Godswar.NetShim.Checks.exe`:
  **all network-shim checks passed**.
- Mandatory B03 PostgreSQL gate:
  **34 required checks, three migration scenarios, cleanup passed in
  406,070 ms**.
- `git diff --check`: **clean** apart from repository line-ending notices.
- Changed/new file-size audit: **all files below 20 KB**.

Machine-readable local report:
`artifacts/b09-closure/postgres-ci-result-20260730-final.json`

Verification hashes:

- report SHA-256:
  `5E80461E0DFCEC081F6A85BF13981337044CCE552712C05487440253F9C317A1`
- `Net.dll` SHA-256:
  `0F0850E4A70CDB56AA360A6F7AA070A6E88B1D013470469B88F28E426CBEC9A0`
- `Godswar.NetShim.Checks.exe` SHA-256:
  `8A063C866BC8D7DBCEB9831B84DB5587A61802DF09A29D4D28CF4DDB86E8BEC8`

## Declared limitations and next ticket

- Secure right-click equipment activation and pet-egg hatch are unavailable
  until B12 supplies one truthful, replay-safe bag-item activation contract.
  Drag/drop equipment transfer remains durable through family 15.
- Advanced Holy Stone action 701 remains unavailable until a clear retail
  capture exists.
- Raw TCP mutations retain weaker retry guarantees until the raw protocol is
  retired or isolated by the later networking decision.
- Multi-process player ownership fencing remains B15.

The next dependency-ordered roadmap ticket is B10: checkpoint versions and
bounded persistence workers.
