# Class Suit workflow

Status: implemented for the original client dialogue `37` at the Athens and
Sparta Gear Mentor NPCs.

## Native routing

Gear Mentor keeps its original dialog `4`. The same physical NPC also exposes
the stock Class Suit dialog `37`. The server sends one opcode-`10067` native
advertisement with the extended-function flag `0x200` and packed dialog value
`37004`. The client decodes that base-1000 value from right to left as `4`,
then `37`, so Gear Enhancement and Class Suit are sibling choices in
database-authored `route_order`. It is not a Gear Enhancement submenu entry.

| City | NPC key | Interaction ID |
|---|---|---:|
| Sparta | `Sparta_070` | `5067` |
| Athens | `Athens_070` | `5209` |

The captured stock-client item reference `205` means the currently equipped
weapon. Conversion actions map it explicitly to authoritative equipment slot
`10`; insignias remain bag selections. The live stock dialog sends direct bag
slots `0..95` (for example, `7` and `3`), while captured variants may encode
the same slots as `100..195`; the bounded decoder accepts both exact forms.
Unknown equipped references fail closed, and Add/Delete Class Attribute remain
bag-only until their native equipped-slot encodings are captured and verified.

The stock dialog sends each item choice through opcode `10193`. On confirm it
clears those visual controls before sending the final opcode `10069`; some
builds consequently leave the final item references empty. The server keeps a
short-lived, account/character/NPC/dialog-bound snapshot of the choices and
accepts only the exact ordered clear burst. Every selected bag item must still
match its captured compact state, and the snapshot is consumed before durable
execution. Explicit inline references remain supported for captured clients
that include them.

Dialogue content is published as immutable NPC-dialogue revision V2. The V1
revision remains readable for rollback.

## Authoritative conversions

The server owns the complete mapping of 62 equipment branches (248 Class Suit
templates). A client supplies only captured item references: a bag slot or the
verified equipped-weapon reference, plus bag-only material slots. It never
supplies the result item, required quantity, profession, or level.

| Operation | Source | Material | Result |
|---|---|---|---|
| Exchange Class Suit | eligible common equipment | Promotional Insignia I (`3931`) | Class Suit I |
| Upgrade Class Suits | Class Suit I | Promotional Insignia II (`3962`) | Class Suit II |
| Convert into Class Suit III | Class Suit II | Promotional Insignia III (`14069`) | Class Suit III |
| Convert into Class Suit IV | Class Suit III | Promotional Insignia IV (`14073`) | Class Suit IV |

Each conversion step consumes the insignia for its target tier. The exact
per-slot cost and resulting equipment level are the stock-client values from
`NF_L0_CY002`, `NF_L0_CY003`, `NF_L0_CY006`/`NF_L0_CY007`, and
`NF_L0_CY008`:

| Equipment slot | Insignias per conversion | Tier I level | Tier II level | Tier III level | Tier IV level |
|---|---:|---:|---:|---:|---:|
| Weapon | 3 | 120 | 125 | 135 | 150 |
| Ring | 2 | 120 | 125 | 131 | 151 |
| Waist | 2 | 120 | 125 | 139 | 159 |
| Necklace | 1 | 120 | 125 | 134 | 154 |
| Gloves / hand | 1 | 121 | 126 | 132 | 152 |
| Boots / foot | 1 | 121 | 126 | 136 | 156 |
| Chest | 3 | 122 | 127 | 133 | 153 |
| Head | 3 | 123 | 128 | 140 | 160 |
| Leggings | 2 | 124 | 129 | 137 | 157 |
| Wrist | 2 | 124 | 129 | 138 | 158 |
| Shield | 1 | 123 | 128 | 139 | 159 |

The authoritative target template, character profession, and character level
are read under the same PostgreSQL transaction as the inventory update. The
client cannot choose or override the output level or insignia cost.

Quality, grade, binding, item EXP, Holy Suit state, ordinary attributes, and
Holy Stone sockets are preserved. The output becomes bound when either input
is bound.

Reverse conversion is implemented for Class Suit I through IV. It restores the
branch's canonical common item and refunds every insignia cost paid along that
branch: Tier I refunds Insignia I, Tier II refunds I and II, and so on through
Tier IV. Refunds inherit the equipment's binding state and the whole conversion
commits atomically, including split stacks. The stock content has no same-level
common template for the Class Suit III/IV levels, so those tiers deliberately
return the same canonical common template used at the start of the branch
rather than inventing an item ID. Quality, grade, item EXP, Holy Suit state,
ordinary attributes, and Holy Stone sockets remain preserved; class-specific
attributes are removed.

Forward conversion preserves both class-specific fields only on the legitimate
Class Suit III-to-IV path. A malformed Common, Tier I, or Tier II source that
already carries one is rejected for operator repair; conversion never silently
drops that player value.

## Class-specific gear attributes

Adding a class attribute requires any matching Class Suit III or IV gear item,
one Flame Spark (`9990`), and one allowed class stone. Class Suit I/II and
common gear are ineligible. Deleting a class attribute likewise requires Class
Suit III/IV gear and one Water Grain (`9991`). Every eligible gear branch can
hold two distinct, profession-compatible class-specific attributes in dedicated
fields in addition to all five ordinary attributes. The existing Delete dialog
has no attribute selector, so it removes the second Class Suit field first and
the first field on the next use.

| Professions | Stone | Attribute | Grade 1 to 25 value |
|---|---:|---:|---:|
| Warrior or Champion | `9950` Primal | `200` Physical Attack | `+33` to `+840` |
| Warrior or Champion | `9951` Courage | `201` Hit | `+4` to `+124` |
| Warrior or Champion | `9952` Energy | `210` Physical Damage | `+1%` to `+26%` |
| Warrior or Champion | `9953` Rage | `211` Critical | `+3` to `+93` |
| Priest or Mage | `9954` Holy | `220` Magic Attack | `+25` to `+628` |
| Priest or Mage | `9955` Blessing | `221` Healing | `+2.1%` to `+59.6%` |
| Priest or Mage | `9956` Rune | `230` Magic Damage | `+1.2%` to `+31.2%` |
| Priest or Mage | `9957` Force | `231` Critical | `+3` to `+93` |

These eight attributes are add/delete-only: they have no Quartz Plate upgrade
chain. Their effective bonus is nevertheless selected from the original
`L1`-through-`L25` content values by the gear's authoritative grade, so a Grade
25 item receives the right-hand value in the table.

The stock “fifth attribute” page remains visible, but its final action is
fail-closed until an exact original wire capture establishes the missing
operation semantics.

Specifically, wire operation `107` remains deliberately fail-closed and is not
implemented. It must not be wired until an exact original-client capture
establishes its request and response semantics. Ordinary Gear Enhancement owns
the native fifth slot; the Class Suit extension does not guess operation 107.
See [Five ordinary plus two Class Suit attributes](class-suit-seven-attribute-protocol.md)
for the durable and client wire contract.

## Persistence and anti-duplication boundary

Every completed mutation uses a UUID-backed command identity on the secure
shim path (or a connection-scoped server UUID in explicitly enabled raw local
development). PostgreSQL locks the ownership fence, character revision, all
bag rows, and the selected equipment row when applicable before validation.
Gear mutation, material consumption/refund,
inventory revision, permanent command inbox, audit, inventory ledger, and
outbox event commit atomically before the client is acknowledged.

The durable receipt records a stable replay intent: operation, exact NPC and
dialog endpoint, gear slot, and material slots. A retry with that same stable
intent returns the stored receipt even after the successful transaction has
changed the items in those slots. Reusing a UUID with a different endpoint or
slot selection is a conflict and cannot mutate inventory. When execution
reaches the full command envelope, its item-snapshot hash remains an additional
tamper/staleness check. Pre-route replay only accepts a fully parsed Class Suit
mutation; malformed or navigation packets can never replay a receipt.

## Item-content release and rollback

The four Promotional Insignias are part of immutable item manifest v6. The
current server accepts only a complete sealed v6 publication. Manifest v5 is
retained as immutable history and contains the same item and Holy Suit policy
families except for these four reviewed insignias.

Once the official pointer has advanced to v6, do not start an old v5-only
binary against it. The safe rollback order is:

1. Drain players, stop every server worker, verify that no character has an
   active checkpoint owner, and take a tested PostgreSQL backup.
2. Record the exact published v6 hash and the exact intended retained v5 hash.
   Both revisions must be sealed, complete, and pass their version-specific
   count and SHA-256 validation.
3. Use a reviewed offline compare-and-swap operation under the `ITEMSCON`
   advisory transaction lock to move only the singleton `items` pointer from
   that exact v6 hash to that exact v5 hash. Do not edit immutable rows.
4. Verify that the pointer is sealed v5, that neither revision fingerprint
   changed, and that the four insignias are absent from the v5 definition set.
5. Start only a tested old binary that understands the installed forward-only
   schema. A binary whose migration catalog ends before migration
   `20260802_052_class_suit_item_content` will reject the database even after
   the pointer move; use a schema-compatible rollback image or restore its
   matching pre-052 backup.

The existing v4 rollback tool does not implement the v6-to-v5 pointer move and
must not be forced or bypassed. Until a dedicated offline tool is reviewed,
the documented pointer transition is a release-engineering requirement, not
an ad-hoc SQL procedure. The PostgreSQL item-template integration check
`AssertV5ToV6ClassSuitUpgradeAsync` exercises a disposable sealed v5 pointer,
proves the v5 fingerprint remains unchanged, and proves normal startup
atomically republishes the retained v6 release.

Relevant implementation:

- `State/ClassSuitConversionCatalog.cs`
- `State/ClassSuitConversionPlanner*.cs`
- `State/ClassSuitAttributePlanner.cs`
- `Game/ClassSuitProtocol.cs`
- `Game/GameClientHandler.ClassSuit.cs`
- `Infrastructure/Inventory/*ClassSuit*.cs`
- `client/network-shim/src/SecureClassSuitCommandIdentity.*`
