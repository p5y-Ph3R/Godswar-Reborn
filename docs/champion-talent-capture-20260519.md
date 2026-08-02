# Champion Talent Capture - 2026-05-19

Source: working server via `tools/Godswar.CaptureProxy`.

Capture session:
- `packet_capture_sessions.id`: `b4d21fc2-cbde-4149-8990-645c4781cdb5`
- Output path: `C:\Reborn\captures\champ-talent-20260519-115333.log`
- Target login: `127.1.1.110:5999`
- Local proxy: login `5998`, game `7000`

Observed talent-related packets:
- `S2C 10019 / EnterMain`: champion character enter packet.
- `S2C 10237 / OwnedPetList` (historically misclassified as
  `SkillUiState`): `0800fd2702000000`
- `S2C 10196 / SkillList`: empty skill list packet appeared twice.
- `S2C 10042 / TalentRankList`: 18 champion talent records.
- `S2C 10041 / TalentSkillUnlockList`: 2 unlock records.
- `S2C 10166 / PlayerStatusUpdate`: sent around enter and later status refresh.

`10042 TalentRankList` structure from capture:
- Header: `u16 length`, `u16 opcode`, `u32 object_id`, `u32 record_count`.
- Record length: 16 bytes.
- Record fields: `i32 talent_id`, `i32 current_rank`, `i32 current_value`, `i32 next_cost`.

Captured champion talent records:

| Index | Talent ID | Name | Tree Order | Rank | Current Value | Next Cost |
|---:|---:|---|---:|---:|---:|---:|
| 0 | 64 | Inner Peace | 10 | 0 | 1 | 1 |
| 1 | 59 | Arcane Guard | 12 | 0 | 1 | 1 |
| 2 | 61 | Arcane Heal | 14 | 0 | 1 | 1 |
| 3 | 65 | Phalanx Guard | 15 | 0 | 1 | 1 |
| 4 | 51 | Basic Dexterity | 1 | 0 | 1 | 1 |
| 5 | 55 | Archaian Spearplay | 5 | 0 | 1 | 1 |
| 6 | 62 | Improved Dexterity | 8 | 0 | 1 | 1 |
| 7 | 67 | Centaurian Strength | 17 | 0 | 1 | 1 |
| 8 | 50 | Basic Accuracy | 0 | 0 | 1 | 1 |
| 9 | 53 | Stamina | 3 | 0 | 1 | 1 |
| 10 | 56 | Improved Accuracy | 6 | 0 | 1 | 1 |
| 11 | 63 | Rage | 9 | 0 | 1 | 1 |
| 12 | 66 | Endurance | 16 | 0 | 1 | 1 |
| 13 | 52 | Basic Block | 2 | 0 | 1 | 1 |
| 14 | 54 | Basic Agility | 4 | 0 | 1 | 1 |
| 15 | 58 | Fisherman's Net | 11 | 0 | 1 | 1 |
| 16 | 60 | Meditation | 13 | 0 | 1 | 1 |
| 17 | 57 | Regeneration | 7 | 0 | 1 | 1 |

`10041 TalentSkillUnlockList` structure from capture:
- Header: `u16 length`, `u16 opcode`, `u32 object_id`, `u32 record_count`.
- Record length: 8 bytes.
- Record fields: `i32 skill_or_unlock_id`, `i32 value`.

Captured unlock records:

| Index | ID | Value |
|---:|---:|---:|
| 0 | 250 | 0 |
| 1 | 3062 | 0 |

Important discrepancy:
- The working server sent 18 champion talents.
- The current local `talent_templates` table has 19 class-1 talents. Local id `68` / `Champion's Might` was not sent by the working server in this capture.

Local implementation note:
- `GetTalentStatesAsync` now mirrors the captured champion order and filters class-1 talent output to the 18 captured IDs.
- Enter bootstrap used the captured empty `10237 OwnedPetList` packet:
  `0800FD2702000000`. The server now builds this packet dynamically from
  persisted pets.
- Enter bootstrap now uses the captured empty `10196 SkillList` packet: `0C00D4270000000000000000`.
- Champion `10041 TalentSkillUnlockList` now sends unlock IDs `250` and `3062` with value `0`.

Current local progression rule:
- Talent rank cap is server-owned and currently set to `100`.
- Upgrade cost is progressive by target rank, but tuned so high ranks are not a bad trade for tiny stat gains:
- Ranks `1-10`: cheap linear cost, rank `N` costs `N`.
- Ranks `11-40`: moderate progression, rank `40` costs `619`.
- Ranks `41-60`: slower midgame ramp, rank `60` costs `912`.
- Ranks `61-80`: endgame ramp, rank `80` costs `1888`.
- Ranks `81-90`: late-endgame ramp, rank `90` costs `2710`.
- Ranks `91-100`: prestige ramp, rank `100` costs `4025`.
- Total cost from `0 -> 100` is `103505` points per talent, so `2,000,000` points is enough to max all 19 current local talent rows.
- Talent stat contribution uses an effective-rank curve instead of raw rank: `1-40` = `+1/rank`, `41-60` = `+2/rank`, `61-80` = `+3/rank`, `81-90` = `+5/rank`, `91-100` = `+7/rank`.
- Example: `Basic Accuracy` at rank `94 -> 95` now costs `3330` and gains `+21 Hit`, instead of costing roughly `16860` for only `+3 Hit`.
- Required character level is server-owned:
- Ranks `1-40`: rank `40` requires player level `120`.
- Ranks `41-60`: rank `60` requires player level `140`.
- Ranks `61-100`: progress from required player level `141` through `160`;
  rank `100` requires level `160`. This policy is shared by Warrior, Champion,
  Priest, and Mage.
- The client has labels for `Required Expertise Points` and `Required Character Level`, but no editable client data table has been found for this progression. The known client-side logic is compiled in `Origin.exe`, with `CPlayer::GetPassiveSkillExpendValue` mapped at `006f2020`.
- Client tooltip compatibility patch:
- `Localization\en_us\Settings\Sys\Skill.ini` champion talent sections `[50]..[68]` have their per-rank display value multiplied by `2.6`, so the stock linear tooltip reaches the server's rank-100 effective total. Example: `Basic Accuracy` changed from `Hit=2,3` to `Hit=2,7.8`, so rank `100` displays `+780 Hit`.
- `Localization\en_us\Text\Message.dat` changes `NextLevel` to `Next level (progressive curve)` to avoid implying stock linear growth.
- This does not make mid-rank tooltip values exact. The client tooltip renderer still multiplies current rank by the data value. Exact per-rank curve text and per-talent milestone bonus descriptions need a native tooltip patch or a discovered client-supported description field.
