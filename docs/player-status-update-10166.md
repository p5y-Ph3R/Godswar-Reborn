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
92-95  i32    profession                 1 Champion
100-103 i32   level                      200
104-107 i32   current_hp                 43023
108-111 i32   current_mp                 11732
144-147 i32   max_hp                     43023
148-151 i32   max_mp                     11732
152-155 i32   physical_attack            7624
156-159 i32   physical_defense           3344
160-163 i32   physical_attack_duplicate  7624
164-167 i32   physical_defense_duplicate 3344
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
44-47   f32    165.0        hex 00002543
48-51   u32    0            hex 00000000
52-55   f32    -97.0        hex 0000C2C2
56-59   f32    1.0          hex 0000803F
60-83   i32x6  0,0,0,0,0,0  hex 000000000000000000000000000000000000000000000000
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
Capture S2C 10166 / 0x27B6 from the working server while opening character info.
Compare the unknown offsets above first.
Confirm whether 44/52 are live X/Z, whether 84/88 are class/status flags, whether 112-119 are packed flags/hair/default HP, and whether 212-235 carries talent/status tail data.
```
