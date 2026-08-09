# Client Fashion Effect Protocol

## Native controls

The stock Fashion panel persists these client-owned preferences in
`Localization/en_us/Settings/User/BagSet.xml`:

- control `110083`, `Option1`: Fashion `Show`;
- control `110085`, `Option2`: Fashion `Effect`.

`Effect` gates the native avatar's armor/body and held-weapon aura renderers.
It does not carry an aura ID or change equipment. The aura assets and ranks are
still derived by the client from the authoritative equipped-item quality and
grade projection.

### Verified Fashion-tab equipment slots

The three stock-client buttons are consecutive equipment slots, but they are
not three costume slots:

| Slot | Native Fashion-tab role | Verified client item definitions | Current server support |
|---:|---|---|---|
| 12 | `Stylish` / costume (`110013`) | Normal costume items use client type `stylish` (the `7000`-series items below do not belong here). | Supported: authoritative equip/unequip, persistence, self and observer appearance, `Show`, and `Effect`. |
| 13 | `Create` / production-tool legacy accessory (`110014`) | `7000` and `7001`, client type `create`, `SkillFlag=12`. | Reserved and unsupported. The server does not accept or project this slot as Fashion. |
| 14 | `Pet` / Owl legacy accessory (`110015`) | `7002` and `7003`, client type `pet`, `Ride16198`, `SkillFlag=20`; the referenced client ride assets are the Owl variants. | Reserved and unsupported. This is not the durable pet inventory and is not the server's mount slot 20. |

The client evidence is in `ItemBagsExUI.xml` and `ItemBaseAttribute.xml`.
Server `EquipmentSlots.Stylish` deliberately names only slot 12; slots 13 and
14 must not be inferred as additional Fashion slots.

## Opcode 10202 / 0x27DA

Native client request, 16 bytes total:

| Offset | Size | Meaning |
|---:|---:|---|
| 0 | 2 | packet length `16` |
| 2 | 2 | opcode `10202` |
| 4 | 4 | client-provided object ID; never authoritative |
| 8 | 4 | checkbox value: `0` off, `1` on |
| 12 | 4 | uninitialized native stack data; ignore and never log |

Server projection, 12 bytes total:

| Offset | Size | Meaning |
|---:|---:|---|
| 0 | 2 | packet length `12` |
| 2 | 2 | opcode `10202` |
| 4 | 4 | server-selected target object ID |
| 8 | 4 | effect visibility: `0` removes, `1` rebuilds |

The server binds requests to the authenticated session, validates the exact
request size and boolean domain, and ignores both native noise fields. Self
responses use the local-player object ID; observer projections use the
server-owned world object ID and remain scoped to the current world instance.

## Runtime and ordering rules

`GameCharacter.EquipmentEffectsVisible` is runtime-only and excluded from
durable JSON. The client remains the preference owner and resends `Option2` on
login. Runtime character replacement preserves the accepted value.

The accepted `Effect` preference applies only while a Fashion item is equipped
and `Show` is on. The authoritative `0x27DA` value is therefore resolved as:

| Fashion state | Stored `Effect` preference | Projected aura state |
|---|---|---|
| Equipped and shown | Off | Off for the Fashion appearance |
| Equipped and shown | On | On for the Fashion appearance |
| Equipped but hidden | Either | On for the restored ordinary gear |
| Absent | Either | On for ordinary gear |

Hiding or unequipping Fashion restores the ordinary armor and weapon aura; it
does not let an old Fashion `Effect=off` suppress ordinary rank effects.
Equipping Fashion always defaults its authoritative `Show` state to on.
Unequipping clears a stale hidden state, while the client-owned `Effect`
preference remains available for the next shown Fashion projection.

On initial login and after a map/detail scene rebuild, an equipped Fashion must
receive a forced self projection even when the incoming `Show` value equals the
current runtime value. The verified order is:

1. `PlayerDetail` (`0x273B`)
2. local `PlayerStatusUpdate` (`0x27B6`)
3. self `EquipmentVisualRefresh` (`0x27D9`)
4. self `EquipmentEffectVisibility` (`0x27DA`)

Sending `0x27D9` before the local avatar exists can be discarded by the native
client. Omitting it when `Show` is already on leaves ordinary gear displayed
until the player toggles the checkbox. The immediately following `0x27DA` must
use the resolved aura state above, not the raw stored checkbox value.

Every equipment/Fashion model refresh (`0x27D9`) must be followed immediately
by `0x27DA` for the same object. This is required for login, world visibility,
inspection, equip/unequip, forging, Fashion Show changes, and later observers.
Otherwise model reconstruction can lose the aura or restore an old effect
choice.

The original server captures confirm both `0` and `1` projections. Native
`Origin.exe` receives the packet in `MSG_DEL_ASPEFF`; zero removes both effect
renderers and nonzero rebuilds both.
