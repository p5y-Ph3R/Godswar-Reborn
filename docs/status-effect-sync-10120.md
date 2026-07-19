# Native status-effect sync (10120)

The bundled client renders its buff/status bar from `S2C 10120 / 0x2788`
(`MSG_STATUS`). Its receive handler was verified directly in `Origin.exe`.

The preserved R3 server declaration in `Common/MsgDefine.h` is an older,
incompatible revision: it uses a 280-byte packet and 16-bit timers. Do not use
that layout with this client.

## Packet layout

The packet is always 340 bytes:

| Offset | Type | Meaning |
|---:|---|---|
| 0 | `u16` | Packet length (`340`) |
| 2 | `u16` | Opcode (`10120`) |
| 4 | `u32` | World object ID |
| 8 | `u32` | Active status count |
| 12 | `u32[20]` | Status IDs, ascending by ID |
| 92 | `u32[20]` | Remaining seconds paired with the status IDs |
| 172 | `StatusData` (168 bytes) | Aggregate status-derived data |
| 300 | `f32` | Aggregate fighter-EXP bonus (`StatusData.m_GetEXP`) |

The original server allows ten beneficial and ten detrimental statuses. The
wire packet therefore always reserves twenty ID/time slots. A permanent status
uses `4294967295` (`uint32 -1`) as the client-facing remaining-time sentinel.

The client copies exactly 42 dwords from wire offset 172 into its local
`StatusData`, proving both the 168-byte aggregate block and 340-byte total. The
initial self-status packet must be sent only after client opcode `10357`
(`EnterUiReady`): before that boundary the handler rejects the update unless
the local object exists and the client state is fully entered.

## Reborn EXP status IDs

The English client definitions live in
`Localization/en_us/Settings/Sys/Status.ini`.

| ID | Status | Kind | Bonus | Client time |
|---:|---|---:|---:|---:|
| 1500 | Bronze VIP EXP Bonus | 1008 | +5% fighter EXP | Permanent |
| 1501 | Silver VIP EXP Bonus | 1008 | +10% fighter EXP | Permanent |
| 1502 | Gold VIP EXP Bonus | 1008 | +15% fighter EXP | Permanent |
| 1503 | Platinum VIP EXP Bonus | 1008 | +20% fighter EXP | Permanent |
| 1504 | Faction Area EXP Bonus | 1009 | +25% fighter EXP | 43,200 seconds |

The four VIP definitions deliberately share a kind, making membership tiers
mutually exclusive. Area control is a separate kind and can stack with VIP and
the stock EXP status families. These definitions use effect 15 only, so they do
not advertise Talent EXP or pet EXP.

VIP icons are sent with the permanent-status sentinel while membership is
active. The server reconciles online status sets every 30 seconds and removes
the icon after entitlement expiry. Timed potion, event, guild, weekend, and
area statuses retain their actual client countdowns.
