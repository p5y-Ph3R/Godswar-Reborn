# Map runtime and travel support

## Status

The client-facing map catalog contains all 81 runtime map IDs:

- `0-69`
- `200-210`

Catalog support, automatic world travel, and authored world content are three
different completion boundaries:

| Boundary | Current state |
| --- | --- |
| Runtime catalog | All 81 client map IDs are catalogued and resource-addressable by the installed client; stock-client load acceptance is pending |
| Ordinary walking graph | 23 city/core maps (`0-22`), with 48 reciprocal directed links |
| Special-map admission | 58 non-ordinary/special maps are catalogued but require an explicit server-owned admission rule |
| NPC/monster content | Incomplete outside the available authoritative captures; never inferred from appearance-only client data |

This distinction is deliberate. A client map file proves that a scene can be
loaded; it does not prove that an arbitrary player may enter it or establish
authoritative NPC, monster, boss, dungeon, reward, or return-location state.

## Ordinary world map IDs

| ID | Scene key | Classification |
| ---: | --- | --- |
| 0 | `Sparta` | City |
| 1 | `Athens` | City |
| 2 | `Athens_Newbie` | Core world |
| 3 | `Parnitha_1` | Core world |
| 4 | `Sparta_Newbie` | Core world |
| 5 | `Nemea_1` | Core world |
| 6 | `Mycenae_All` | Core world |
| 7 | `Olympia_All` | Core world |
| 8 | `Thermopylae_All` | Core world |
| 9 | `Thebes_All` | Core world |
| 10 | `Larissa_All` | Core world |
| 11 | `Marathon_All` | Core world |
| 12 | `Parnitha_2` | Core world |
| 13 | `Peloponnese_All` | Core world |
| 14 | `Nemea_2` | Core world |
| 15 | `Derveni_All` | Core world |
| 16 | `Argolis_All` | Core world |
| 17 | `Isthmus_of_Corinth_All` | Core world |
| 18 | `Megara_All` | Core world |
| 19 | `Plataea_All` | Core world |
| 20 | `Oracle_of_Delphi_All` | Core world |
| 21 | `Olympus_All` | Core world |
| 22 | `Elasson_All` | Core world |

The source `SpanMapConfig.xml` supplies 44 raw rows. Duplicate portal rows are
collapsed, and five reciprocal pairs (ten directed links) recovered from the northern maps'
`Address.ini` files complete the connected ordinary-world graph. The
one-direction `Mycenae_All -> Thebes_All` and
`Mycenae_All -> Derveni_All` rows conflict with the address labels and remain
recorded but gated until live observation establishes their intended use.

## Special map IDs

The following 58 IDs are valid client scenes but are not ordinary
walk-across-map destinations:

- `23-45`: GM, Execratively, Agate, Sealbox, Guardwar, Labyrinth, Sicily,
  WarField, Pan's Labyrinth, Alcatraz Island, Lelantine Farm, Troy, Love
  Island, and their variants.
- `46-55`, `58-67`: Colosseum stages.
- `56-57`: War Hall and Arena.
- `68-69`: Parnassus and Tartarus.
- `200-210`: Medusa Island, Field Test variants, Atlantis Entrance, Fane,
  Salame variants, and Heracles.

Ordinary movement cannot select these maps. A future validated NPC, dungeon,
event, arena, or developer operation must own new admission. The current login
path can restore a previously persisted special-map location; instance expiry
and return-location enforcement are not implemented yet, so that restoration
path is a recorded admission gap rather than completed instance support.

Client evidence contains an unresolved anomaly: runtime IDs `60` and `61`
both name `Colosse13`; ID `60` references the `Colosse12/Address.ini` path.
The catalog preserves the client data exactly instead of silently renaming a
runtime scene.

## Authoritative transition sequence

Only an already accepted server movement segment can activate a walking
portal. The client never chooses the destination map or arrival coordinates.

1. Validate the source map, finite bounded coordinates, accepted segment
   length, and a six-unit portal radius.
2. Resolve the reciprocal target portal and place the player four units
   beyond the trigger radius toward the target map's center.
3. Serialize position persistence and commit the destination map/coordinates
   as a new epoch. Queued saves from the old epoch are discarded.
4. Atomically remove the source ECS ownership and stage the destination
   session as `WorldReady=false`.
5. Remove the player from source-map viewers.
6. Send the stock client's native 24-byte scene-change packet:

   | Offset | Field |
   | ---: | --- |
   | `0` | `u16` length `24` |
   | `2` | `u16` opcode `10018` |
   | `4` | `u32` local player object ID |
   | `8,12,16` | `f32` destination X/Y/Z |
   | `20` | reserved `u16`, zero |
   | `22` | destination runtime map ID |

7. Expect a fresh client `10007` and `10200`, in either arrival order. Reply
   to `10200` with current player detail. The stock binary's scene-loader call
   flow sends these packets in that order; the server accepts either order
   defensively.
8. Load and send destination NPC, monster, and player AOI state, then
   atomically mark the destination session ready.
9. Refresh local status and area-dependent EXP effects.
10. For secure realtime sessions, publish a destination keyframe with a new
    world generation while preserving the triggering input acknowledgement.
    Delayed old-generation UDP input is rejected.

Opcode `10357` is a one-shot login/UI-ready message and is not a map-change
gate. A map transition does not replay EnterMain, inventory, skills, talents,
or the full login bootstrap. Readiness has a 60-second deadline. The target
location is durable before the scene packet is sent, so a timeout disconnects
the session and a subsequent login restores the target instead of reviving
stale source-map ownership.

## Evidence and generated sources

Primary client evidence:

- `Localization/en_us/Settings/Sys/MapIdToNameConfig.ini`
- `Localization/en_us/Settings/Sys/SpanMapConfig.xml`
- per-map `Localization/en_us/Monster/<scene>/Address.ini`
- the client terrain and `.hmp` assets

The audited `Origin.exe` with SHA-256
`753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79`
handles server opcode `10018` at VA `0x004EB9B2` and starts scene loading at
`0x004EBA08`. The ready block at `0x0046C54D` sends opcode `10007`, then calls
`0x00576890`, which builds and sends opcode `10200`. This proves the expected
fresh readiness flow statically. The repository still has no captured
server-`10018` live transaction, so the full scene/model outcome remains a
required stock-client acceptance gate.

Repository projections:

- `src/Godswar.Server/State/MapTemplateSeed.Generated.cs`
- `database/postgres/008_maps.sql`
- `src/Godswar.Server/Game/MapTraversalCatalog.cs`
- `src/Godswar.Server/Game/MapTraversalDetector.cs`

The suffix in `MapID<n>` is the runtime map ID. The trailing numeric value in
the configuration row is a separate client scene/resource selector and must
not replace the runtime ID on the wire.

## Content boundary

The client `Monster.ini` files describe appearances and assets, not
authoritative live spawn instances, health, object IDs, rewards, or lifecycle
state. Raw original-server `MOBS.ini` rows still require a coordinated
client-config and server packet-builder import when their template keys are
not present in the installed client configuration.

Until authoritative data is imported, a successful scene transition can
correctly render a map with no server-owned NPCs or monsters. That is a
content gap, not permission to fabricate spawn data.

The multi-segment NPC scene-key parser is corrected. Generated quest-backed
NPC references now resolve 2,084 of 2,104 rows (221 of 223 unique keys) across
22 maps: `0-6`, `8`, `9`, `11-16`, `18`, `19`, `30`, `31`, `38`, `44`, and
`68`. The unresolved keys are `Marathon_All_006` and
`Peloponnese_All_006`, with ten references each. These remain
quest/reference hints rather than proof of a complete actor population. The
active development database contains 84 captured NPC packets and 270 captured
monster packets, all on Sparta/map `0`. Fresh migrations create the packet
schemas but seed no captured spawn rows, so this active-database content must
be exported into a reviewed reproducible seed before it can be called
portable.

The recovered original-server city files add valuable evidence: Athens has
111 actor rows and 285 monster-instance rows, while Sparta has 108 actor rows
and 295 monster-instance rows. Those monster rows are not yet directly
loadable because seven of eight city template keys are absent from the
installed client's current per-map `Monster.ini`, and the server runtime
requires validated complete opcode-`10020` packets.

## Verification gate

Offline coverage must pass for:

- the exact 81-map catalog and classifications;
- deduplicated, reciprocal portal geometry and safe arrivals;
- malformed/oversized movement rejection;
- exact scene-change bytes;
- hidden ECS transfer, activation, collision rollback, and stale-source
  rejection;
- position epoch ordering and old-save rejection;
- handler readiness ordering;
- realtime world-generation rollover and stale-generation rejection.

The first stock-client acceptance route is `Sparta (0) -> Sparta_Newbie (4)
-> Sparta (0)`. It must prove scene/model rendering, reciprocal arrival,
destination AOI, source removal, continued movement, relog persistence, and
secure stale-generation rejection. The remaining evidence-backed ordinary
links can then be accepted as one bounded campaign. Special maps require
their own admission/content acceptance.
