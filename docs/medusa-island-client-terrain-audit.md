# Medusa Island client terrain and traversal audit

This note pins the reverse-engineering evidence used by the Medusa placement
work. It separates facts recovered from the installed client from authored
spawn and traversal candidates. It does not enable production spawning.

## Pinned client artifacts

| Artifact | Size | SHA-256 | Other identity |
| --- | ---: | --- | --- |
| `C:\Godswar Origin\Origin.exe` | 6,676,480 | `C80FC15418BC1865731105AE05CE96DA3015FEC9E8E51337263D1C475301EEEE` | File/product version `2.46.0.2257`; PE timestamp `2013-12-13T03:06:50Z`; image base `0x00400000` |
| `C:\Godswar Origin\GodsWar.map` | 8,417,405 | `2BEF5FA86D53AAA56150EE6210E9F93AA8966E5E0E8C3DF92556086F948BB9A7` | Linker map dated 2009; it names an older build and must not be used as an address map for the installed executable |
| `Map/Medusa_Island.hmp` | 9,269,584 | `2519287645950257306D055B70571B40EA7143A0A051EC77CE027A105EC9B598` | map 200 |
| `Map/Medusa_Island2.hmp` | 9,269,584 | `2519287645950257306D055B70571B40EA7143A0A051EC77CE027A105EC9B598` | map 204; byte-identical to map 200 |

The executable's `.text` and `.rdata` section raw offsets equal their RVAs,
so each RVA below is also the file offset. VAs assume the pinned image base.

## Symbol bridge

The older linker map identifies this public `CTerrain` API at lines
19582-19590:

```text
006D6140 CTerrain::get_block_table()
006D6160 CTerrain::LoadBlockData(FILE*)
006D62B0 CTerrain::is_block(float,float)
006D6400 CTerrain::get_planar_to_world(int,int,float*,float*,float*)
006D6440 CTerrain::LoadTerrainData(FILE*)
006D6E80 CTerrain::get_world_to_planar(float,float,float,int*,int*)
```

Those old VAs do not align with the installed 2013 binary. The current
implementations were matched by their CTerrain field use, allocation sizes,
file-read shape, coordinate math, callers, and the installed
`.\SceneManage\CTerrain.cpp`/`CTerrain::create_tiled_terrain` strings.

## The 4 MiB plane is the live block table

The current CTerrain constructor is at VA `0x004F2D30`. At
VA `0x004F2F04` / RVA `0x000F2F04` it allocates 16,384 bytes into field
`+0x680` and 4,194,304 bytes into field `+0x688`:

```text
68 00 40 00 00 E8 42 20 27 00 68 00 00 40 00
89 86 80 06 00 00 E8 32 20 27 00 89 86 88 06 00 00
```

The matched block loader is VA `0x004F7550` / RVA `0x000F7550`. It clears
`width * height << 8` bytes at `+0x688`, then calls `fread` with that pointer,
the computed length, count 1, and the HMP `FILE*`. Its read sequence at
VA `0x004F758B` is:

```text
0F B7 4E 2E 0F B7 56 2C 8B 44 24 24 0F AF CA 50
8B 86 88 06 00 00 6A 01 C1 E1 08 51 50 E8 A0 2F 2C 00
```

For the Medusa header values `width=128`, `height=128`, `cellX=4.0`, and
`cellZ=4.0`, that read is exactly 4,194,304 bytes. In both Medusa HMPs the
loader's stream position is decimal 356,684 (`0x5714C`), so the plane is
`[356684,4550988)` (`[0x5714C,0x45714C)`). Its SHA-256 is
`A13395AB9CF89AB3C2B3AF3DFA2DE607574404F9BEA192B29644482AA962419F`;
it contains 1,239,620 zero bytes and 2,954,684 one bytes. The next eight
bytes are `04 00 00 00 00 00 7C C3`.

The matched query is VA `0x004F4EC0` / RVA `0x000F4EC0`, corresponding to
the linker's `CTerrain::is_block(float,float)`. Its prologue is:

```text
83 EC 08 56 8B 35 58 61 57 01 0F B7 46 2C
0F B7 4E 2E 89 44 24 04 DB 44 24 04 89 4C 24 04 D9 46
```

It loads the exact `+0x688` pointer at VA `0x004F4F5A`. The decisive result
sequence at VA `0x004F4FB7` is:

```text
03 E8 80 3C 2B 00 5F 5D 5B 0F 95 C0 5E 83 C4 08 C2 08 00
```

That is `cmp byte ptr [table + index], 0; setne al`. Therefore:

- table byte 0 means `is_block == false`;
- any nonzero byte means `is_block == true`;
- inputs outside the centered terrain envelope return true before the lookup.

This is also an active traversal consumer, not dead or editor-only code.
Direct call sites exist at VAs `0x0047E97E`, `0x00492289`, and
`0x00499B79`. The first computes a prospective actor X/Z, calls
`0x004F4EC0`, tests `AL`, and enters its movement-stop/reset path when the
result is nonzero. Its call/result bytes are:

```text
E8 3D 65 07 00 84 C0 74 1A
```

### Authoritative projection

The query uses header dimensions and cell sizes plus constants 0.5 and
0.0625 (one sixteenth). For in-range coordinates its nonnegative float-to-int
conversion is equivalent to floor:

```text
blockX = floor((worldX + width * cellX / 2) / (cellX / 16))
blockZ = floor((height * cellZ / 2 - worldZ) / (cellZ / 16))
index  = blockZ * (height * 16) + blockX
```

The installed maps are square, yielding the existing Medusa projection:

```text
blockX = floor((worldX + 256) * 4)
blockZ = floor((256 - worldZ) * 4)
index  = blockZ * 2048 + blockX
```

The client comparisons admit exact equality at the outer edges, although
`worldX=256` or `worldZ=-256` produces an index on or beyond the next row.
Offline validation therefore correctly uses the safe half-open bounds
`-256 <= X < 256` and `-256 < Z <= 256`.

The earlier 381/381 `Address.ini` cross-map correlation remains useful as an
independent transform check. The executable consumer now establishes the
stronger semantic fact: this plane is the client's actual block authority,
not merely a land, material, or minimap mask. A zero cell still does not prove
adequate clearance from nearby blocked cells, static meshes, encounter
objects, or a valid teleport trigger.

## Height evidence

The matched current planar-to-world converter is VA `0x004F31D0` / RVA
`0x000F31D0`. It computes X and Z using cell sizes and the centered constant
256, but explicitly writes zero to the middle/Y output with
`fldz; fstp [edx]`:

```text
A1 58 61 57 01 D9 40 30 DA 4C 24 04 DD 05 50 16 96 00
DC E9 D9 C9 D9 19 D9 EE D9 1A D9 40 34 DA 4C 24 08
8B 44 24 0C DE E9 D9 18 C2 0C 00
```

Its effective conversion for Medusa is `(4*i-256, 0, 256-4*j)`. The three
transport models below also have Y=0. This is authoritative for CTerrain's
grid-to-world plane; the HMP does not expose a separate per-cell gameplay
height query. It is not, by itself, proof that an actor at an arbitrary X/Z
will avoid a raised or blocking static mesh.

The scene-change handler also shows why original arrival height cannot be
recovered from terrain alone. At VA `0x004EB9B2` it logs
`MSG_SCENE_CHANGE MapID=%d` using the packet's map ID, and at
VA `0x004EBA20` it consumes packet floats at offsets `+0x0C`, `+0x10`, and
`+0x14` as the server-supplied X/Y/Z arrival. The relevant bytes begin:

```text
D9 47 0C ... D9 47 10 ... D9 47 14
```

Thus Y=0 is the best client-terrain candidate, while the original server's
chosen entry/transfer Y remains unavailable without a packet capture or
equivalent authoritative server data.

## Transport evidence and hard stop

The HMP static-node records are 0x128-byte render records: position at
`+0x10`, scale at `+0x1C`, quaternion at `+0x28`, followed by texture, model,
object, and root strings. Three records use the same
`scene_transport_01` texture/model:

| Record | Object | Exact X/Y/Z |
| --- | --- | --- |
| `0x907C` | `obj000135` | `(-130.745865, 0, 140.661072)` |
| `0xFF7C` | `obj000233` | `(-77.4255524, 0, 95.3334732)` |
| `0x101CC` | `obj000235` | `(-34.2554092, 0, 53.5264778)` |

They are render landmarks. Their records contain no destination map,
destination position, direction, trigger radius, or progression condition.
The object names are generic sequence names, so record order does not prove
portal order or direction.

The two other suggestive static records are likewise only render transforms:

| Record | Model | Exact X/Y/Z |
| --- | --- | --- |
| `0x11C64` | `use_gate_002_all.jcs` | `(221.273605, 0, -224.577499)` |
| `0x1B3DC` | `use_gate_003_all.jcs` | `(103.644981, -2.09999919, -170.667999)` |

The client does load `/Settings/Sys/SpanMapConfig.xml`; the string is
referenced at VA `0x00513C8B` and the parser contains `RouteConfig`,
`MapConfig`, `Route%d`, and `Map%d`. Both installed locale copies are
byte-identical with SHA-256
`22BF6C21A7FB6AFA87041108F408522AE90C57E5AD11D3E68273EE759466C228`.
They define only ordinary overworld map IDs and contain no map-200 or map-204
route entry. The apparent `204` in map 0's `Map1="4,204,-120"` is an X
coordinate in a destination tuple, not Medusa map ID 204.

Both Medusa `Address.ini` files also contain `AddressCount=0` for both camps
and are byte-identical. No client configuration recovered here binds a
Medusa landmark to a landing.

The current five authored hard points therefore remain unverified:

- the entry is only near the gate-2 render model;
- the first source/destination pair is authored beside two identical rings,
  with no encoded pairing or direction;
- the second source is authored beside the third identical ring;
- the final destination has no matching ring or client landing record.

The scene-change packet consumer confirms that authoritative destination
coordinates normally arrive from the server. The installed client can certify
the block table and flat CTerrain Y projection, but cannot recover the missing
Medusa trigger conditions, pairings, arrival coordinates, or facing. Until a
packet capture, original server data, or controlled in-client acceptance
proves all five, traversal and `ProductionLive` must remain fail-closed.
