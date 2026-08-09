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
60-63  f32   equipped_riding_speed_bonus 0.0 no mount; 0.25 = +25%
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
204-207 f32   ignore_physical_defense    0.1473 = 14.73%
208-211 f32   ignore_magic_defense       0
228-231 i32   talent_points              1068
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
212-215 i32    0            hex 00000000
216-219 i32    0            hex 00000000
220-223 i32    1            hex 01000000
224-227 i32    1500         hex DC050000
232-235 i32    5            hex 05000000
```

Working-server comparison target:

```text
The working Ride completion sends this packet immediately after opcode 10167.
Captured walking packets carry `1.0` at offset 56; a 24% mounted character
carries `1.24`, matching the observed movement-step ratio. Continue comparing
84/88, 112-119, and 212-235 against working captures.
```

## Local movement fields and interaction safety

The client handler at VA `0x004E9273` copies wire offset 8 to
`GameData+0x25C` for `0x22` dwords. Therefore wire offset 56 maps to
`GameData+0x28C`, offset 60 maps to `GameData+0x290`, and offset 64 maps to
`GameData+0x294`. PersonalInfo reads `GameData+0x294` as Credit, so offset 64
must remain untouched.

Wire offset 60 is **not available for extensions**. Native NPC targeting reads
byte `GameData+0x292`, which is the third byte of this dword, as the local
interaction identity/faction. For example, encoding the riding bonus `0.54f`
produces bytes `71 3D 0A 3F`, changes that identity byte to `10`, and makes the
client reject every NPC before it emits opcode `10067`.

The replacement server therefore keeps all four bytes at offset 60 zero for
local and remote status packets. Equipped Riding Speed must be calculated in
client-owned UI state or sent through a separately validated extension; it
must never be stored at `GameData+0x290`. Packet length and opcode remain
unchanged.
