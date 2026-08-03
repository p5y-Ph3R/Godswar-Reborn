# Gear Enhancement Native Evidence

This appendix records the original-service capture, shipped client-script
behavior, and nearby NPC actor placement supporting the Gear Enhancement UI.

## Capture and native client path

The original-service capture
`captures/capture-proxy-20260514-173331.log` establishes the dialog handshake
and packet shape: opcode `10067` opens dialog `118`; the initial 92-byte opcode
`10069` request uses sub-ID `-1`; opcode `10070` returns operations `2,3,6`.
That capture used NPC `5140` / `Sparta_143`, confirming that dialog `118` is
the separate Origin Enhancer workflow. The stock Gear Mentors are NPC 070 and
use dialog `4` instead. The server returns the captured wire order `2,3,6` for
Origin Enhancer; the client lays those sub-IDs out visibly as Add, Enhance,
Delete.

The shipped scripts establish the native dialog path:

- `NpcFunBreak_SetMsg` assigns physical Gear Mentor controls as Gear, operation
  Catalyst, Attribute Stone and emits opcode `10193` for item selection;
- `NpcFunEnhancer_SetMsg` assigns Origin Enhancer native controls as Gear,
  Catalyst, Attribute Stone;
- Add, Enhance, and Delete remain native controls `800001..3` and generate the
  operation request through their shipped click handlers;
- confirmed item references occupy arguments `6`, `7`, and `8`.

`NpcFunLoad.xml` loads `NpcFunBreak.lua` and `NpcFunEnhancer.lua` separately.
The custom GWGE2/GWGE3 XML/Lua wrapper has been removed. Both scripts and the
stock `NpcFun.xml` dialog shell now run without presentation overrides.

## Nearby original NPC actors

City NPC placement comes from server opcode `10020` actor records, not from
the client's navigation destinations. The restored Sparta cluster uses these
original-service positions:

| NPC | Object | Position | Stock dialog |
|---|---:|---:|---|
| Gear Mentor (`Sparta_070`) | `5067` | `142,-165` | `4`, Gear Enhancement |
| Master Vestment Forger (`Sparta_085`) | `5082` | `126,-162` | `29`, Holy Suit Design |
| Class Shifter (`Sparta_044`) | `5041` | `141,-174` | Shift class |
| Ingredients Vendor (`Sparta_122`) | `5119` | `97,-174` | Vendor |
| Origin Enhancer (`Sparta_143`) | `5140` | `97,-163` | `118`, Enhancer |

The stale `Quest.xml` reference at `143,-170` for NPC 122 is not an actor
position and no longer overrides the captured Ingredients Vendor location.
Athens no longer mirrors this cluster: all 111 actors are seeded from the
original server actor table, including orientation.

| NPC | Protocol object | Athens actor position | Facing |
|---|---:|---:|---:|
| Gear Mentor (`Athens_070`) | `5209` | `142,-165` | `1.7` |
| Master Vestment Forger (`Athens_085`) | `5224` | `126,-162` | `4.7` |
| Class Shifter (`Athens_044`) | `5183` | `141,-174` | `2.3` |
| Ingredients Vendor (`Athens_122`) | `5261` | `97,-174` | `1.7` |
| Origin Enhancer (`Athens_143`) | `5282` | `97,-163` | `1.7` |

The actor table used source IDs `6101/6102` and unavailable
`*_FemVillager3` appearances for Athens 142/143. The emulator retains the
established protocol-safe city IDs and shipped `*_Hallo` appearances while
using the authoritative positions and facing.
