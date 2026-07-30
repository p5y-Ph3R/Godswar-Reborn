# B09 reachable value-mutation closure audit

Date: 2026-07-30

Status: completed secure-path mutation closure; unsupported operations are
explicitly fail-closed and remaining raw-only limitations are assigned

## Scope and classification rules

This audit starts at the game packet router and follows every currently
reachable operation that can create, destroy, move, transform, or spend
player value. It does not treat generated content text as implemented
gameplay. The classifications are:

- **Durable/replay-safe:** authenticated operation identity, permanent inbox
  result, authoritative PostgreSQL transaction, and durable audit/outbox
  evidence exist on the secure path.
- **Raw compatibility:** a mutation remains available to the original raw
  legacy protocol but cannot provide cross-reconnect retry identity.
- **Unreachable:** the opcode is recognized or logged but no mutation is
  called.
- **Dev-only:** reachable only through an explicitly enabled developer
  command.
- **B11-owned:** character lifecycle, not an economy command.
- **B12-owned:** progression, rewards, or pet durability rather than the B09
  inventory/currency aggregate.

The router evidence is
`src/Godswar.Server/Game/GameClientHandler.cs::HandlePacketAsync`. Direct
store calls were cross-checked in `src/Godswar.Server/Game` against
`src/Godswar.Server/State/IGameStore.cs`. Secure command families are defined
by `src/Godswar.Server/Application/Commands/CommandEnvelope.cs`.

## Finite reachable mutation inventory

| Reachable operation | Entry point and durable evidence | Classification | B09 disposition |
|---|---|---|---|
| Talent upgrade | `GameClientHandler.Talents.cs::HandleUseOrEquipAsync`; `PostgresTalentUpgradeCommandExecutor` | Durable/replay-safe secure path; raw aggregate-version compatibility | Already closed |
| Equipment forge and Silver spend | `GameClientHandler.Forging.cs::HandleForgeStartAsync`; `PostgresEquipmentForgeCommandExecutor` | Durable/replay-safe family 3; raw compatibility | Already closed |
| Gear Mentor make attribute stone | `GameClientHandler.NpcDialog.cs`; `PostgresMakeAttributeStoneCommandExecutor` | Durable/replay-safe family 6; raw compatibility | Already closed |
| Transform crystals | `GameClientHandler.DurableMaterialConversion.cs`; PostgreSQL transform executor | Durable/replay-safe family 7; raw compatibility | Already closed |
| Combine gem pieces | `GameClientHandler.DurableMaterialConversion.cs`; PostgreSQL combine executor | Durable/replay-safe family 8; raw compatibility | Already closed |
| Decompose gear | `GameClientHandler.DurableDecompose.cs`; PostgreSQL decompose executor | Durable/replay-safe family 9; raw compatibility | Already closed |
| Enhance/add/delete gear attributes | `GameClientHandler.DurableGearEnhancement.cs`; PostgreSQL gear-enhancement executor | Durable/replay-safe families 10-12; raw compatibility | Already closed |
| Delete a kit-bag item | `GameClientHandler.InventoryActions.cs::HandleStorageItemAsync`; `PostgresKitBagItemDeleteCommandExecutor` | Durable/replay-safe family 13; raw compatibility | Closed; secure tokenless downgrade guard added by this increment |
| Move/swap kit-bag items | same handler; `PostgresKitBagItemMoveCommandExecutor` | Durable/replay-safe family 14; raw compatibility | Closed; secure tokenless downgrade guard added by this increment |
| Explicit drag/drop equip or unequip | same handler; `PostgresEquipmentBagTransferCommandExecutor` | Durable/replay-safe family 15; raw compatibility | Secure path closed; downgrade guard added by this increment |
| Holy Stone mount/remove/drill and Gold spend | `GameClientHandler.NpcDialog.cs`; `GameClientHandler.DurableHolyStone*.cs`; PostgreSQL Holy Stone executor | Durable families 16-18 for supported forms; advanced action 701 is unsupported and cannot mutate value | Closed safety boundary; action 701 requires a verified live wire capture before implementation |
| Zodiac grid activation and Gold spend | `GameClientHandler.Zodiac.cs`; `PostgresZodiacSkillGridActivationCommandExecutor` | Durable/replay-safe family 19; managed aggregate-version identity | Already closed |
| Zodiac grid upgrade and Energy/Talent Point spend | same handler; `PostgresZodiacSkillGridUpgradeCommandExecutor` | Durable/replay-safe family 20; native UUID | Already closed |
| Zodiac skill selection | `GameClientHandler.DurableZodiacSkillGridSelection.cs`, SID 102 | Durable family-21 UUID, PostgreSQL audit/inbox/outbox transaction, replay projection, and native fail-closed classifier implemented | Closed by the SID-102 increment |
| Right-click bag activation | `GameClientHandler.InventoryActivation.cs::HandleBreakItemAsync`, opcode 10051 | Raw compatibility only; ambiguous between equipment and pet hatch; secure requests fail closed and refresh authoritative state | Deferred to the coupled B12 bag-activation/pet-hatch command |
| Developer item/mount grant and bag clear | `GameClientHandler.DeveloperCommands.cs`; durable developer executors where a UUID is supplied | Dev-only; durable secure path plus explicit compatibility forms | Not a production B09 blocker |
| Monster kill EXP/Talent rewards | `GameClientHandler.Progression.cs::ApplyMonsterRewardAsync`; `PostgresGameStore.Progression.cs` | B12-owned; transactional but death/reward identity is not yet restart-proof | Excluded from B09; required by B12 |
| Zodiac level and online Energy accrual | `GameSessionRegistry.Progression.cs`; `PostgresGameStore.Progression.cs` | B12-owned progression intervals | Excluded from B09; required by B12 |
| Pet egg hatch, presence, and level | `GameClientHandler.PetEggs.cs`, `Pets.cs`, and `PetLevel.cs`; `PostgresGameStore.Pet*.cs` | B12-owned; PostgreSQL transactions exist but the packet retry contracts are incomplete | Excluded from B09; required by B12 |
| Character creation/deletion | `GameClientHandler.LoginWorldEntry.cs`; `PostgresGameStore.Characters.cs` | B11-owned lifecycle | Excluded from B09 |
| Position, HP, and MP persistence | movement/combat/background registry handlers and `SaveCharacterPositionAsync` / `SaveCharacterVitalsAsync` | Durable runtime checkpoint state, not an economy transfer | B10 checkpoint work, not B09 |

## Recognized opcodes that cannot currently mutate value

`GameClientHandler.cs` routes `Kitbag`, `Storage`, `PickupDrops`, `MoveItem`,
and `Sell` only to `LogInventoryPacket`. No store or command executor is
called. These are **unreachable**, not missing durable mutations. They become
new B09/B12 work only when gameplay implementation makes them mutate value.

Forge selection/cancel, Gear Mentor selections, bag inspection, item-info
requests, NPC dialogue navigation, player inspection, and ordinary status
packets only build transient UI state or read projections. They are not
value mutations.

## Why opcode 10051 cannot truthfully mean “right-click equip”

The stored retail client-to-server vector is the exact 92-byte packet at
`captures/working-multiplayer-20260514-193356.log:5840-5841`. The only
authoritative role carried by the request is a kit-bag page/index at full
packet offsets 12 and 14. The selected-world-object field is explicitly not
an action discriminator.

The current server resolves the authoritative item in that bag slot and then
chooses one of two different aggregates:

```text
equipment item -> bag/equipment replacement
pet egg        -> consume egg and create owned pet
```

That routing is visible in
`GameClientHandler.InventoryActivation.cs::HandleBreakItemAsync`. No stored
retail pet-hatch request proves a distinct packet shape. The native regression
test
`client/network-shim/tests/SecureEquipmentBagTransferRegistryTests.cpp::
CheckSharedOpcode10051StaysUnmarked` therefore deliberately prevents family
15 identity from being attached to opcode 10051.

Labeling every opcode-10051 request as equipment would conflate pet creation
with inventory equip. Trusting opaque bytes or a client item hint would not
fix that: the server must continue to resolve the authoritative item.

This increment adds the complementary managed guard: secure or otherwise
identified opcode-10051 traffic does not silently downgrade into either
compatibility mutation. It receives an authoritative bag/equipment refresh
without changing value. Raw legacy TCP retains the existing server-selected
right-click behavior; the secure transport does not.

No repository capture or packet route establishes a separate “right-click
unequip” command. Confirmed unequip traffic is the opcode-10052
equipment-slot/bag-slot transfer already covered by family 15. This audit
does not invent another direction for opcode 10051.

There are only two safe ways to make this route durable:

1. introduce a controlled client/shim packet discriminator that truthfully
   distinguishes equipment activation from pet hatch, then give each action
   its own durable command family; or
2. define one honest **bag-item activation** family and make both the
   equipment replacement and pet-hatch branches permanent-inbox,
   replay-safe transactions before marking the native packet.

Option 2 crosses the B09/B12 ownership boundary and should be done with the
B12 pet-hatch transaction rather than partially marking the shared opcode.

## Why raw opcode 10052 cannot be replay-safe

Opcode 10052 carries an equipment slot and a bag slot, but no direction or
operation identity. The server infers:

- empty equipment plus occupied bag: equip; and
- occupied equipment plus empty bag: unequip.

After a successful request those states are reversed. An identical tokenless
packet can therefore be either a late retry that must be ignored or a
deliberate reverse transfer that must be executed. No server-only heuristic
can distinguish them across a reconnect.

The native secure shim solves this with a stable UUID and family-15 permanent
inbox replay. This increment now rejects a tokenless opcode-10052 transfer on
a secure connection, because every supported secure shape should carry that
UUID. It refreshes authoritative state and performs no compatibility
mutation. Raw TCP keeps the legacy behavior until the raw transport is
retired; claiming cross-reconnect idempotency for it would be false.

## Finite B09 closure resolution

The six closure conditions are resolved as follows:

1. Zodiac SID 102 selection is family 21 with native reconnect-stable
   operation identity, strict server-side packet validation, one PostgreSQL
   transaction for state/audit/inbox/outbox, exact replay, and authoritative
   projection.
2. Advanced Holy Stone action 701 is not implemented from incomplete
   evidence. Empty navigation remains available, while every value-bearing
   or malformed shape is rejected before operation identity, inventory,
   wallet, executor, or compatibility-store access. This closes the value
   boundary without inventing a destructive wire contract.
3. Secure family-13 delete, family-14 bag move, and family-15 equipment
   transfer shapes cannot downgrade into tokenless mutations. Forge and Gear
   Mentor/Origin families 3 and 6-12 have the same guard.
4. Ambiguous opcode 10051 is explicitly deferred to B12's combined
   bag-item-activation/pet-hatch slice. Secure traffic fails closed; raw
   compatibility is documented as non-idempotent and temporary.
5. The final router/store audit found no additional reachable production
   inventory, equipment, wallet, or material mutation. Progression/reward,
   pet, lifecycle, and checkpoint writes are assigned to B10-B12; recognized
   log-only inventory opcodes are not mutations.
6. The frozen-tree Release build, all 234 managed protocol checks, strict
   native build and full native suite, file-size/diff hygiene review, and
   mandatory B03 PostgreSQL gate all pass. The B03 report records 34 required
   checks and three migration scenarios, including the SID-102 transaction.

Raw compatibility is a declared transport limitation, not evidence that a
UUID exists where the stock protocol supplies none. B09 closes the secure
authoritative path; retirement or isolation of raw mutation traffic belongs
to the later raw-protocol migration decision.

## Focused verification

The secure downgrade and ambiguity checks are covered in
`tests/Godswar.Server.ProtocolChecks/
EquipmentBagTransferDurableHandlerChecks*.cs`.

On the frozen modified tree:

- Release solution build: **0 warnings, 0 errors**.
- Complete managed protocol harness: **234 passed, 0 failed**.
- Strict Win32 Release native build: **passed with `/W4 /WX`**.
- Complete `Godswar.NetShim.Checks.exe` suite: **passed**.
- Mandatory B03 PostgreSQL gate: **34 required checks and three migration
  scenarios passed in 406,070 ms**.
- Machine-readable report:
  `artifacts/b09-closure/postgres-ci-result-20260730-final.json`.

The final closure summary is
`docs/data-architecture-b09-closure-20260730.md`.
