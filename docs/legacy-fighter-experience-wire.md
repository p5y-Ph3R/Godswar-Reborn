# Legacy fighter EXP wire experiment

## Decision

The original protocol remains four bytes wide. Fighter EXP is serialized as a
little-endian unsigned 32-bit value in the range `0..4,294,967,295`. The server
rejects negative or larger values; it does not silently wrap or clamp durable
state.

This widens the usable wire range without changing packet lengths or the stock
client binary. It is experimental until the stock client is exercised above
`2,147,483,647` because its internal consumers are not consistently typed.

## Client evidence

`C:\Godswar Origin\GodsWar.map` contains these decorated C++ signatures:

- `CLevelExp::Update(unsigned int, unsigned int)` (`...QAEXII@Z`)
- `CPlayer::GetNextGradeExp()` returning `unsigned int` (`...QAEIXZ`)
- `CPlayer::SetNetGradeExp(unsigned int)` (`...QAEXI@Z`)
- `CGameObject::GetExp()` returning signed `int` (`...QBEHXZ`)
- `CGameObject::GetMaxExp()` returning signed `int` (`...QBEHXZ`)
- `CGameObject::SetExp(int)` and `SetMaxExp(int)` (`...QAEXH@Z`)

The progress-bar API therefore accepts the full UInt32 range, but some general
object accessors expose the same four bytes as signed. Values above
`2,147,483,647` may still display incorrectly or trigger a signed comparison in
an untested UI/NPC path. No stock-client binary patch is included.

## Outgoing fields

| Packet builder | Field |
| --- | --- |
| `EnterMain` | current fighter EXP at offset 84; client EXP-bar maximum at offset 88 |
| `PlayerDetail` | current fighter EXP at offset 92 |
| `PlayerStatusUpdate` | current fighter EXP at offset 96 |
| `ExperienceGain` | gained fighter EXP at offset 4; resulting total at offset 8 |
| `MonsterDeathReward` | current fighter EXP at offset 48 |
| `PlayerLevelUp` | current fighter EXP at offset 16 |

For an ordinary fighter below the level cap, `EnterMain` offset 88 remains the
next-level threshold. For either a durably sealed level-89 fighter or a
level-200 fighter, it is the unsigned storage ceiling `4,294,967,295`. The
sealed behavior matches working-original capture
`capture-proxy-20260514-173331.log` (`F5D0BF67 FFFFFFFF` at offsets 84 and 88).
The client therefore renders stored sealed EXP against the real accumulation
cap instead of clipping the bar against level 89's `2,616,333` level-up
threshold. Level 199 still uses its ordinary table threshold. Talent,
equipment, pet, Holy Box, and Zodiac experience are distinct protocol fields
and are not changed by this experiment.

Boundary golden vectors are covered by
`LegacyFighterExperienceWireChecks`: `2,147,483,647`, `2,147,483,648`,
`4,000,000,000`, and `4,294,967,295`.
