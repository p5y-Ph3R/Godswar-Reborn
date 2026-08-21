# PlayerStatusUpdate 10166 Baseline

Purpose: local decoded baseline for `S2C 10166 / 0x27B6 PlayerStatusUpdate`. Use this when comparing unknown fields against the working server captures.

Character used for baseline: `KREI_ARLOTT_KING`

Raw packet:

```text
0xEC00B627481400004B5245495F41524C4F54545F4B494E47000000000000000000000000000000000100000000002543000000000000C2C20000803F00000000000000000000000000000000000000000000000028000000010000000100000000000000C80000000FA80000D42D00000100DC05350000000000000000000000000000000000000000000000000000000FA80000D42D0000C81D0000100D0000C81D0000100D0000AD0700000A050000EF0200007702000076000000430000007D3F953E0000000066090000D0D5163E00000000000000000000000001000000DC0500002C04000005000000
```

Known fields:

```text
00-01  u16    packet_length              236
02-03  u16    opcode                     10166 / 0x27B6 PlayerStatusUpdate
04-07  u32    object_id                  0x1448 local player object id
08-39  ascii  name                       KREI_ARLOTT_KING
40     u8     gender                     1 male
44-47  f32   position_x
52-55  f32   position_z
56-59  f32   movement_speed_multiplier  1.0 walking; 1 + mount speed bonus
60-61  bytes reserved                   zero
62     u8    camp                       0 Sparta; 1 Athens
63     byte  reserved                   zero
64-67  i32   credit                     existing reputation field; never reuse
92-95  i32    profession                 1 Champion
100-103 i32   level                      200
104-107 i32   current_hp                 43023
108-111 i32   current_mp                 11732
144-147 i32   max_hp                     43023
148-151 i32   max_mp                     11732
152-155 i32   hp_recovery                7624
156-159 i32   mp_recovery                3344
160-163 i32   physical_attack            7624
164-167 i32   physical_defense           3344
168-171 i32   magic_attack               1965
172-175 i32   magic_defense              1290
176-179 i32   hit                        751
180-183 i32   dodge                      631
184-187 i32   critical                   118
188-191 i32   critical_resistance        67
192-195 f32   physical_damage_bonus      0.2915 = 29.15%
196-199 f32   magic_damage_bonus         0
200-203 i32   damage_absorb              2406
204-207 f32   healing_received           AcceptCure; 0.1473 = 14.73%
208-211 f32   outgoing_healing           Cure; 0
228-231 i32   talent_points              1068
232     u8    pk_mode                    captured ordinary value 5
233-235 bytes reserved                   zero
```

Unknown/template fields and current local values:

```text
41-43   bytes  00 00 00
48-51   u32    0            hex 00000000
68-83   i32x4  0,0,0,0      hex 00000000000000000000000000000000
84-87   i32    40           hex 28000000
88-91   i32    1            hex 01000000
96-99   i32    0            hex 00000000
112-115 bytes  01 00 DC 05  as i32 = 98304001, likely packed shorts 1,1500
116-119 i32    53           hex 35000000
120-143 i32x6  0,0,0,0,0,0
212-215 i32    0            active stock field; semantics not yet recovered
216-219 i32    0            active stock field; semantics not yet recovered
220-223 i32    1            hex 01000000
224-227 i32    1500         hex DC050000
```

Working-server comparison target:

```text
The working Ride completion sends this packet immediately after opcode 10167.
Captured walking packets carry `1.0` at offset 56; a 24% mounted character
carries `1.24`, matching the observed movement-step ratio. Continue comparing
84/88, 112-119, and 212-235 against working captures.
```

## Local movement fields and interaction safety

The client handler begins at VA `0x004E9273`. Its local-player branch is
selected at `0x004E929D..0x004E92A2`, loads destination `GameData+0x25C` at
`0x004E92AE`, loads wire source `[ebx+0x0C]` at `0x004E92D1`, and copies
`0x22` dwords at `0x004E92D4..0x004E92D9`. Therefore wire offset 56 maps to
`GameData+0x28C`, byte 62 maps to `GameData+0x292`, and offset 64 maps to
`GameData+0x294`. PersonalInfo reads `GameData+0x294` as Credit, so offset 64
must remain untouched.

The remote-player branch resolves the target entity at
`0x004E93D8..0x004E93E3` and performs the equivalent `0x22`-dword copy at
`0x004E9417..0x004E9428`. Remote wire byte 62 therefore maps to the target
entity's byte `+0x292` too. The hostile-player helper reads and compares these
camp bytes at `0x004A1F31..0x004A1F3D`; a different camp takes its accepted
path at `0x004A208A`.

Wire dword 60 is **not available for extensions**. Native NPC targeting reads
byte `GameData+0x292`, the third byte of this dword, as the local camp. For
example, encoding the riding bonus `0.54f` produces bytes `71 3D 0A 3F`,
changes that camp byte to invalid value `10`, and makes the client reject every
NPC before it emits opcode `10067`.

The replacement server therefore clears the dword, then writes only validated
`GameCharacter.Camp` (`0` or `1`) at byte 62 for both local and remote status
packets. Bytes 60, 61, and 63 remain zero. UI-only extensions must use a
separately validated channel; they must never be stored at `GameData+0x290`.
Packet length and opcode remain unchanged.

## PK mode and remote training dummies

Wire byte 232 is active for both object domains. The local branch loads
`[ebx+0xEC]` at `0x004E933F` and writes local `GameData+0x23A40` at
`0x004E934F`. For a remote type-1 player, the branch at
`0x004E9440..0x004E9463` loads the same wire byte at `0x004E9465` and writes
the target entity's `+0x23A40` byte at `0x004E946F`.

The retained packet capture proves only that the ordinary value at byte 232 is
`5`. Native code proves its behavioral role: the hostile-player helper reads
target `+0x23A40` at `0x004A1E90`, compares it with `5` at `0x004A1E96`, and
sends that value down the protected return path at `0x004A1E9F..0x004A1F30`.
A non-5 value reaches the camp comparison. Native entity construction writes
mode `1` at `0x004A2B9F`, providing the bounded attackable projection used for
exact development training dummies.

Ordinary and local status packets retain captured mode `5`. Only a remote
status projection for a registry-recognized exact training dummy overrides
byte 232 with mode `1`. This is necessary because opcode 10166 follows the
spawn packet and otherwise resets both the remote camp and PK mode used by
basic-attack and selected-skill admission.

Offsets 204 and 208 are also unavailable. Native PersonalInfo reads the
second field as `Cure`, and the shared server serializer projects the pair as
healing received (`AcceptCure`) and outgoing healing (`Cure`). Offsets 212 and
216 are not safe padding either: retained stock packets contain nonzero values
in both fields, and the native stat-delta dispatcher updates their matching
GameData dwords. Their exact display semantics are still under investigation,
so neither field may be repurposed.
