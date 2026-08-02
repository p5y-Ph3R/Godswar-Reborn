# Fighter level sealing

The durable fighter-level seal is stored in PostgreSQL as
`public.character_base.fighter_level_sealed`. The database accepts a sealed
state only while `fighter_job_lv = 89`, matching the original client's Level
Sealer rule.

When sealed, monster rewards do not advance fighter level. Fighter EXP still
accumulates and saturates at `4,294,967,295`, the complete unsigned 32-bit
range carried by the original four-byte client field. PostgreSQL and the C#
runtime use signed 64-bit storage so the value cannot wrap internally. The
server reports only EXP actually credited at that ceiling. It never uses `-1`,
a sentinel, wraparound, or an "infinite" wire value. Talent EXP and Talent Point
progression are unchanged. Holy Box EXP deductions remain permitted.

At world entry, an ordinary fighter below level 200 receives the next-level EXP
threshold as the progress-bar maximum. A sealed level-89 fighter and any
level-200 fighter receive `4,294,967,295` as that maximum. The durable seal is
loaded into the runtime character and ECS projection, so the locked-level choice
is authoritative rather than inferred from a large current EXP value. A
character already online must relog after changing the seal because the stock
protocol establishes this denominator during world entry.

The unsigned client interpretation above `2,147,483,647` is experimental; see
`legacy-fighter-experience-wire.md` for the stock-client evidence and known
risk.

## Local test2 fixture

The offline helper can seal or unseal a test character:

```powershell
.\tools\SetLocalDevelopmentFighterLevelSeal.ps1 `
    -State Seal -AccountId 13 -CharacterName test2 -Confirm:$false

.\tools\SetLocalDevelopmentFighterLevelSeal.ps1 `
    -State Unseal -AccountId 13 -CharacterName test2 -Confirm:$false
```

The helper refuses to run unless the configured game-server container is
stopped and explicitly configured with
`GODSWAR_RUNTIME_PROFILE=LocalDevelopment`. It also refuses an active
checkpoint owner and refuses to seal a fighter whose level is not exactly 89.
Each invocation writes a permanent `command_audit` record, including an
idempotent `already_sealed` or `already_unsealed` outcome.

Because the helper runs only while there is no world/checkpoint owner, it
does not advance `progression_reward_revision`: no live runtime, cache
projection, or outbox consumer can observe an intermediate fixture state.
Normal monster rewards continue advancing that revision after server start.

Unsealing only removes the durable seal. It does not immediately spend stored
EXP or recalculate the fighter level while the character is offline. The next
positive fighter-EXP reward uses the normal progression rules and may apply
multiple earned level-ups. The future Level Sealer NPC command must define
this catch-up behavior explicitly and perform any immediate catch-up, Gold
charge, and seal transition in one PostgreSQL transaction if that is the
desired game rule.

This fixture does not charge Gold. The original NPC UI/action and its eventual
10,000 Gold transaction are deliberately deferred; that charge must be added
atomically with the authoritative NPC command, not simulated by this offline
development tool.
