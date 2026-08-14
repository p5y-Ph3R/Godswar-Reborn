# Pet skills, talents, and manager dialogue

## Skill-cell model

The pet-detail UI exposes **12 learnable skill cells**. This is distinct from
the **six auto-cast cells** on the pet action bar; the auto-cast limit must not
be used as the durable learned-skill limit.

A newly hatched pet starts with its species starter skill in opened slot 1.
Aptitudes below Smart start with one available/opened slot. Smart (numeric
aptitude 10) and every higher aptitude also start with an available, opened,
but empty slot 2.

Skill-slot progression is deliberately two-step and server-authoritative:

1. Pet Enhance Spring (`10099`) expands the available-slot boundary by one.
2. Golden Apple Juice (`10100`) opens the next available slot so a skill book
   can be consumed into it.

Neither item may move a boundary above 12, skip a boundary, overwrite a
learned skill, or be trusted merely because the client displays a successful
action. The owned-pet snapshot persists the available and opened boundaries
independently.

## Learned-skill rank and Trait policy

Migration `083` publishes the reviewed installed-client skill curves as
immutable PostgreSQL content. The process will only start with normalized
revision `64748AC27B0D815B9C30CFF78A7CE8AD519AE83DF528CB5CDFF4374503ABB473`,
derived from installed `Pet_Skill.xml` SHA-256
`B2EE9219E5E804AFA34797D6D2BCB8787B7C1C6EDF7914F4C1A6AC982A553F43`.
The normalized publication has 67 families, 384 `(Type, Priority)` tier
curves, and 1,655 concrete rank steps. Startup publishes and seals it once;
later row mutation, deletion, incomplete publication, or a different official
revision fails closed.

`Type` is the stable skill-family ID and `Priority` is the learned book tier.
The durable learned state should therefore store the family and highest
learned Priority, rather than treating every concrete `NextID` row as a
separate learned skill. Learning tier N requires tier N-1 to be learned and
the six-value `Trait` threshold to be met at that moment. `Trait` uses pet
Savvy order Agility, Strength, Accuracy, Technique, Wisdom, Luck; client
hundredths are normalized to server decimals. Once a tier is learned, a Fairy
redistribution does not deactivate it or lower its effect.

Every tier has a rank-zero step, so learning has an immediate default effect.
At runtime the resolver chooses the highest `Restrict[i]` not above the
authoritative pet rank and uses `Values[i]` as the **absolute** magnitude. It
does not add the steps together. The repeated `Values[]` curve is canonical
where source metadata disagrees: for example runtime row `552` says
`fact_Value=826` but its indexed `Values[]` magnitude is `926`. Full-width
commas in rows `6020-6023`, misleading XML element names around
`1420-1429`, and the reviewed repeated-metadata discrepancy in curve
`3000-3004` are normalized deterministically.

`Genre` and `Effect` remain distinct. `Add` and `Flag` are retained as opaque
content fields because their combat meanings have not been proven. The owner
stat projection is deliberately fail-closed to these reviewed passive
families:

| Family | Effect | Authoritative owner stat | Scaling |
|---:|---:|---|---|
| `408` | `19` | Ignore physical defense | value x 10,000 basis points |
| `412` | `4` | Physical attack | integer value |
| `413` | `2` | Hit rating | integer value |
| `419` | `21` | Physical damage bonus | value x 10,000 basis points |
| `423` | `0` | Maximum HP | integer value |

Family `413` is Platypus-exclusive Focus. Stock books `10530-10535` map to
runtime tiers `4600/4604/4608/4612/4616/4620`; tiers II-VI require visible
Accuracy Savvy `64/192/235/270/305`. At pet rank 100, Focus VI resolves its
highest rank-90 step and adds `119` Hit Rating.

Only active learned rows on the character's **carried** pet contribute. The
pet need not be summoned or Owner-Merged, so Recall and Call Out do not change
this passive source. Take/switch and hatch select a new source; Seal removes
one. Owner Merge is a separate additive projection and never causes learned
passives to be counted twice. All curve joins use the process-pinned learned
skill publication, the persisted tier, and the highest rank step not above
the authoritative pet rank. The client supplies none of those values.

This stat-safe mapping does not invent proc triggers, targeting, damage,
healing, cooldown, buff, or animation behavior for the other effect genres.
They remain inert until their server semantics are proven.

Both items are direct right-click consumables using the shared client request
opcode `10051`. The server classifies the locked authoritative bag item,
updates the currently carried pet, consumes exactly one item in the same
transaction, and then sends the verified narrow `PetSkillState` response
(`10247`). That 36-byte response updates only the available, opened and learned
boundaries plus the twelve skill IDs. Opcode `10245` is the separate 16-byte
pet-care response for satiety, amity and lifetime and must not be used for
skill cells. The full owned-pet list (`10237`) remains a bootstrap/rebuild
packet because using it as a live delta can disturb carry/summon state.

After a committed learn, upgrade, or unlearn, the server reloads the complete
calculated character snapshot and publishes `10247`, then complete status
opcode `10167`, then local GameData opcode `10166`. Rank-changing pet Merge,
Take/switch, hatch, and Seal publish the same final `10167 -> 10166` stat pair
when their carried passive source or selected rank tier changes. The `10167`
frame preserves all active runtime statuses; it is never an empty replacement
delta. Safe stat snapshots may be replayed for reconciliation, while additive
pet-Merge opcode `10269` is never replayed.

For a surviving consumable stack, the server refreshes the committed count in
place and does not send slot-clear opcode `10052`. The stock client starts its
own clock/cooldown overlay when the item is used, and preserving the item UI
object lets that animation finish. A final consumed unit still receives
`10052` before the empty-bag refresh so a stale icon cannot remain.

## Innate talents

Talents are built into a pet by its aptitude. They are not learned from an
inventory consumable and cannot be supplied by the client. The immutable,
database-published aptitude definition owns the exact mask used at hatch:

| Aptitude | Innate talents | Mask |
|---|---|---:|
| Weak through Zealous (`1-9`) | None | `0` |
| Smart through Almighty (`10-13`) | Quest Dispatch, Healing, Merge | `26` |
| Godly through Transcendent (`14-16`) | Random Event, Quest Dispatch, Work, Healing, Merge | `31` |

The stable implemented bits are:

| Bit | Talent |
|---:|---|
| `1` | Random Event |
| `2` | Quest Dispatch |
| `4` | Work |
| `8` | Healing |
| `16` | Merge |

The client draws a sixth talent cell, but no verified stock name, item, or
meaning exists for bit `32`. That bit remains reserved and must stay clear
until compatible client and protocol evidence is available.

Stock item IDs `10110-10114` retain their names and icons only as inert client
compatibility artifacts. Their activation metadata is deliberately absent;
right-clicking, replaying, or forging a packet for one cannot change a pet's
talents. The native-profile `NativeGenius` value is also compatibility data,
not an authority: it varies by species and conflicts with the quality rule.

An existing pet is reconciled to its aptitude mask by migration `072`. Merge's
legacy boolean projection is updated in the same transaction. New hatches use
the process-pinned database content revision, so both hatch paths receive the
same quality-derived talents and never consult mutable client state.

### Healing runtime

Healing is an authoritative passive talent. After an accepted, nonlethal
monster hit leaves the owner at or below 40% maximum HP, a carried and
summoned pet with bit `8` heals a percentage of authoritative owner maximum
HP, capped by missing HP. At pet level 120 the rates are Smart 12%,
Overbearing 14%, Ferocious 16%, Almighty 18%, Godly 20%, Celestial 22%, and
Transcendent 25%. Level 1 starts at half of the aptitude rate and scales
linearly to the full rate at level 120. A successful heal starts the
stock-derived 180-second cooldown. Replayed, stale, rejected, zero-damage,
and lethal hits cannot trigger it. The client receives only the committed
green healing number and final vitals; it cannot choose the amount or reset
the cooldown.

The exact native heal formula was not recovered, so this is explicitly
project balance V2. The current authoritative incoming-damage slice is
monster-to-player combat. PvP, damage-over-time, and environmental damage must
publish the same shared accepted-damage event before Healing can cover those
future sources. The cooldown is process-scoped until cross-instance transfer
adds a preloaded TTL coordination projection.

### Owner Merge runtime

Owner Merge is an innate pet talent. The stock client presents its action with
the legacy Merge control/artwork, but item `11004` is not an input, inventory
requirement, or consumable for opcode `10274`. The server locks the carried
pet, requires it to be summoned, at full energy, and at least 40 amity, then
calculates the contribution from all six pet Savvy values. A successful request writes
the one contributing-pet flag and exactly 16 typed bonus rows in the same
transaction. A second distinct Merge activation toggles the state off; Take,
Call Out, Recall, and switching pets are rejected while the flag is active. The
dedicated command-family identity, inbox replay, optimistic pet
revision, outbox, and audit records make retries unable to duplicate or
silently clear bonuses.

The installed client emits the exact header-only request `04 00 22 28`
(opcode `10274`) from the action-bar Merge control. The legacy handler accepts
only that four-byte shape, selects the single authoritative active pet, and
executes dedicated durable family `PetOwnerMergeToggle` (`48`). The secure shim assigns the request a
stable operation UUID for retry/deduplication without accepting a client item,
bag slot, pet ID, or stat value.

The contribution curve is database-published project policy
`project-pet-unite-piecewise-marginal-v2`, based on the recovered stock
`Pet_Alter.xml` bases and curves. Its marginal-band effectiveness is
`100% / 85% / 70% / 60% / 50%`; see
[pet-owner-merge-balance.md](pet-owner-merge-balance.md) for the complete
database ownership and publication contract. All 16 values are durable and
appear in the server CharacterStats projection. Physical damage reduction is
applied to incoming monster physical damage. Magic damage reduction, critical
damage reduction, life absorption, and damage rebound remain projection-ready
until their corresponding authoritative combat paths exist.

The installed `Origin.exe` independently establishes the visual lifecycle:

- opcode `10275` is registered to the unite-start handler at `0x006A16F0`;
  its eight-byte frame carries the owner object ID, selects the current pet's
  `unitefile` effect (for example `PetUniteEffect/e_he_0001_all.gwm`), hides
  the companion, and changes both local and third-player pet managers to the
  merged state;
- opcode `10282` is registered to the unite-end handler at `0x006A17A0`;
  its eight-byte frame carries the owner object ID, removes the unite effect,
  and restores the manager state for local and nearby-player presentations.

The server sends `10275` to the owner with local object ID `0x1448` and to
nearby players with the authoritative world object ID. It sends `10282` in the
same two namespaces at expiry. It deliberately does not send full pet-list
opcode `10237` during either transition: that packet rebuilds active-pet
selection and provokes an immediate client Recall, which previously collapsed
the temporary stat projection. The pet stays logically carried and summoned
in PostgreSQL while its companion model is hidden.

At end, the owner's client receives its normal Call Out result and local
world-presence packet after `10282`. The installed client ignores that
world-presence packet for non-local owners, so normal companion restoration
for already-connected observers remains a native-protocol gap; the server
does not blindly broadcast the local-only packet. Join-in-progress observers
do receive `10275` when the target is currently merged. The presentation flag
and AOI world revision change atomically so map handoff reconciliation cannot
miss a concurrent Merge start or end.

Pet energy is normalized to `0..100` by the current server schema. The native
client uses `1800` units for a full bar, and retained server captures publish
that value on a roughly six-second update cadence while unmerged. Exact stock
drain cadence is not recovered. Project balance policy drains one normalized
point per 3 online seconds, mapping a full bar to a 5-minute lifetime. The
interval is injectable for deterministic tests.
Energy decrement and the
final transition are ownership-fenced PostgreSQL mutations. At zero the
server atomically clears `contributes_to_character`, deletes all 16 derived
bonus rows, refreshes character stats, sends `10282`, and restores the carried
companion. Disconnect ends Merge before releasing session ownership; login
also clears a stale active Merge left by an unclean process exit before the
first character-state projection.

Opcode `10278` writes the client's current pet-energy field. The server sends
it only to the owning client at Merge start, each authoritative drain tick,
and Merge end; normalized database energy `0..100` is safely scaled to native
units `0..1800` (`percentage * 18`). It is not broadcast to observers.

## Pet Manager dialogue compatibility

The stock Pet Manager advertises two ordered top-level functions from the
same NPC script. The server publishes both for `Athens_088` and `Sparta_088`:

| Route | Dialogue index | Client label | Published menu |
|---:|---:|---|---|
| 0 | 31 | Pet Raising | 1-11 |
| 1 | 36 | Reset Pet's Points | 100, 101 |

Pet Raising routes its original menu and informational pages:

| Menu sub-ID | Function | Informational page(s) |
|---:|---|---|
| 1 | Soul Contract | 11, 101 |
| 2 | Rebirth | 12, 102 |
| 3 | Merge | 13, 103 |
| 4 | Check Growth | 14, 104 |
| 5 | Seal Spirit | 15, 105 |
| 6 | Unlearn Skill | 16, 106-111 and 114-119 |
| 7 | Bind pet/owner | 17, 112 |
| 8 | Change appearance | 113 |
| 9 | Pet Call charm | Native client page |
| 10 | Owner Merge | Native client page; innate talent, no item consumed |
| 11 | Change Gender | Native client page |

Unlearn Skill is implemented for all twelve slots. The native choices
`106-111` select slots 1-6 and `114-119` select slots 7-12. The stock client
confirms the selection with the exact 92-byte nested frame: parent sub-ID `6`,
the selected erase sub-ID in argument 0, and the remaining seventeen arguments
set to `-1`. A direct selected sub-ID with eighteen `-1` arguments remains a
compatible form. The server selects the summoned owned pet and the first
authoritative Strong Purge Potion (`10101`), deletes the selected skill,
compacts later skills left, consumes exactly one potion, and advances the pet
and inventory revisions in one PostgreSQL transaction. Retries reuse the
durable command result rather than consuming another potion. Native terminal
results are `1011` (no summoned pet), `1061` (no potion), `1062` (empty slot),
and `1063` (success); success refreshes the bag and opcode `10247` skill state.

Reset Pet's Points exposes the stock entry pages:

| Menu sub-ID | Function | Informational/action page |
|---:|---|---|
| 100 | Reset basic Savvy distribution | 111, 116 |
| 101 | Reset Growth Rate distribution | 112, 117 |

Action `117` remains a durable paid Growth preview. Fairy's Feather action
`116` is a one-phase durable reset that atomically consumes one feather and
commits a redistribution of the same total Basic Savvy;
Phoenix's Feather action `117` previews six new Growth rates. A Fairy Reset
commits immediately and page `120` shows the committed values. A Phoenix
Reset consumes its feather but does not mutate Growth until **OK** accepts its
latest session-fenced preview; **Cancel** leaves Growth unchanged. Replayed
operation identities cannot consume or apply twice. A successful Fairy Reset
or Phoenix OK sends the extended 68-byte opcode `10286` to refresh the
existing pet object without the destructive full-list opcode `10237`,
preserving carried and summoned state. The exact-hash, two-locale
installer is `tools/PatchClientPetGrowthResetDialog.ps1`. The Fairy balance
policy and provenance rules are documented in
`docs/pet-basic-savvy-reset.md`.

The client's separate Advance Pet Raising dialogue (`119`) is not published
because no working-server evidence currently binds it to these NPCs.

The menu and read-only pages are wired. Skill removal and direct right-click
use of Pet Enhance Spring and Golden Apple Juice are verified and implemented.
State-changing modal packet layouts for remaining skill-book actions have not
yet been verified from an original-server capture.
Those mutations therefore remain capture-gated: the server must not guess a
request layout or consume an item until the exact packet shape and response
ordering are proven.
