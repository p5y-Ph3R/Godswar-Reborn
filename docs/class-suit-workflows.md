# Class Suit workflow

Status: implemented for the original client dialogue `37` at the Athens and
Sparta Gear Mentor NPCs.

## Native routing

Gear Mentor keeps its original dialog `4`. The same physical NPC also exposes
the stock Class Suit dialog `37`; the server sends both top-level dialogue
advertisements in database-authored `route_order`.

| City | NPC key | Interaction ID |
|---|---|---:|
| Sparta | `Sparta_070` | `5067` |
| Athens | `Athens_070` | `5209` |

Dialogue content is published as immutable NPC-dialogue revision V2. The V1
revision remains readable for rollback.

## Authoritative conversions

The server owns the complete mapping of 62 equipment branches (248 Class Suit
templates). A client supplies only selected bag slots; it never supplies the
result item, required quantity, profession, or level.

| Operation | Source | Material | Result |
|---|---|---|---|
| Exchange Class Suit | eligible common equipment | Promotional Insignia I (`3931`) | Class Suit I |
| Upgrade Class Suits | Class Suit I | Promotional Insignia II (`3962`) | Class Suit II |
| Convert into Class Suit III | Class Suit II | Promotional Insignia III (`14069`) | Class Suit III |
| Convert into Class Suit IV | Class Suit III | Promotional Insignia IV (`14073`) | Class Suit IV |

Insignia cost is determined by slot: weapon/head/chest `3`; ring/waist/wrist/
legs `2`; amulet/gloves/boots/shield `1`. Target minimum level comes from the
pinned item-template publication. The authoritative character profession and
level are read under the same PostgreSQL transaction as the inventory update.

Quality, grade, binding, item EXP, Holy Suit state, ordinary attributes, and
Holy Stone sockets are preserved. The output becomes bound when either input
is bound.

Safe reverse conversion is implemented for Class Suit I and II. It restores a
canonical common item and refunds the corresponding bound insignias. Tier II
refunds both the Tier I and Tier II costs. Class Suit III/IV reverse conversion
is rejected because the shipped client contains no complete safe recipe.

## Class-specific weapon attributes

Adding a class attribute requires a matching Class Suit weapon, one Flame
Spark (`9990`), and one allowed class stone. Deleting it requires one Water
Grain (`9991`). Only one class-specific attribute can exist on a weapon.

| Professions | Stone | Attribute |
|---|---:|---:|
| Warrior or Champion | `9950` Primal | `200` Physical Attack |
| Warrior or Champion | `9951` Courage | `201` Hit |
| Warrior or Champion | `9952` Energy | `210` Physical Damage |
| Warrior or Champion | `9953` Rage | `211` Critical |
| Priest or Mage | `9954` Holy | `220` Magic Attack |
| Priest or Mage | `9955` Blessing | `221` Healing |
| Priest or Mage | `9956` Rune | `230` Magic Damage |
| Priest or Mage | `9957` Force | `231` Critical |

The stock “fifth attribute” page remains visible, but its final action is
fail-closed until an exact original wire capture establishes the missing
operation semantics.

## Persistence and anti-duplication boundary

Every completed mutation uses a UUID-backed command identity on the secure
shim path (or a connection-scoped server UUID in explicitly enabled raw local
development). PostgreSQL locks the ownership fence, character revision, and
bag rows before validation. Gear mutation, material consumption/refund,
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
