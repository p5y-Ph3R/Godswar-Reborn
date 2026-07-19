# Native status-effect sync (10167)

The bundled client renders its buff/status bar from the complete
`S2C 10167 / 0x27B7` player-status snapshot. This is established by the
working-original captures, including Sacred Zeal casts. No clear opcode
`10120` status packet appears in those captures.

The old `PlayerExtendedStatusTemplate` must not be restored: its literal was
only 324 bytes despite its embedded 340-byte header, and its data region was
nibble-shifted. Always build this packet field-by-field.

## Packet layout

The packet is always 340 bytes:

| Offset | Type | Meaning |
|---:|---|---|
| 0 | `u16` | Packet length (`340`) |
| 2 | `u16` | Opcode (`10167`) |
| 4 | `u32` | World object ID |
| 8 | `u32` | Active status count |
| 12 | `u32[20]` | Status IDs, ascending by ID |
| 92 | `u32[20]` | Remaining seconds paired with the status IDs |
| 172 | 168 bytes | Complete derived player/status data |
| 300 | `f32` | Aggregate fighter-EXP bonus |
| 324 | `f32` | Movement-speed multiplier; baseline is `1.0` |

The data block is an absolute snapshot, not a set of status deltas. The known
prefix is Max HP, Max MP, HP recovery, MP recovery, physical attack, physical
defense, magic attack, magic defense, Hit, Dodge, Critical, Critical Resistance,
physical damage bonus, magic damage bonus, damage absorption, received-healing
bonus, and healing bonus. Runtime Hit/Critical buffs must be added to the
character's derived totals before serialization. Sending a zero-based packet
would overwrite the client's displayed stats.

The original server allows ten beneficial and ten detrimental statuses. A
permanent status uses `4294967295` (`uint32 -1`) as the client-facing timer.
Every producer must compose all active sources into one replacement snapshot;
otherwise one status family will erase another.

The initial self snapshot is sent once after client opcode `10357`
(`EnterUiReady`). Sacred Zeal follows the captured order: cast visual (`10040`),
full status snapshot (`10167`), impact (`10046`), then mana (`10135`).

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
the stock EXP status families. VIP icons use the permanent timer while active;
the server's reconciliation pass removes them after entitlement expiry.
