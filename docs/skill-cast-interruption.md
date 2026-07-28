# Authoritative skill-cast interruption

## Scope

The server treats every supported skill whose client `Magic.ini`
`IntonateTime` is greater than zero as an authoritative pending cast. The same
coordinator is used by ordinary combat skills, Ride, and the Sparta City /
Sparta Suburb backhaul skills. Instant skills continue to resolve immediately.

`tools/GenerateSkillTalentTemplates.ps1`
imports decimal `IntonateTime` and `CoolingTime` values into the
`SkillTalentSeed.Generated.cs` facade and its deterministic generated chunks;
`SkillCombatCatalog` exposes them as `CastTime` and `Cooldown`.

Only one cast may be pending for a character. Starting another skill
interrupts the earlier pending cast. Unsupported skills remain rejected even
if the original client has timing metadata for them.

## Interruption triggers

A pending cast is cancelled without consuming MP or applying its effect when:

- the client sends the native cast-interruption request;
- accepted legacy or secure/realtime movement changes the character position;
- a direct map transition begins;
- the character dies or its life revision changes;
- the character, map, target, range, equipped mount, or other required state
  becomes invalid before completion;
- a `HaltIntonate`, stun, cage, or silence-style runtime status is applied;
- another skill or a normal attack replaces the pending cast; or
- the session shuts down.

The completion claim is the cast's linearization point. State is validated,
then revalidated immediately before the atomic claim. An interruption which
wins first cancels the effect; once completion wins, a later status or
movement belongs to the character's next action.

Single-target casts also retain the monster spawn generation captured at
start. A despawned or respawned monster reusing the same object ID cannot
inherit the old cast. AOE observers receive a matching completion impact even
when none of the damaged monsters are in that observer's health-update set.

## Native client notification

The original client already implements the required notification. No client
patch is needed.

The bidirectional packet is:

```text
u16 length       = 8
u16 opcode       = 10171 (0x27BB)
u32 caster ID    = little-endian
```

The server sends local object ID `0x1448` to the caster and the authoritative
world object ID to other viewers. The stock client clears the casting UI and
displays its native `Skill09` message, "Skill is disturbed".

Malformed requests, requests for another object ID, and duplicate requests
after a cast has already been claimed are ignored. This prevents duplicate
notifications when movement cancels server-side and the client subsequently
sends its own `10171`.

## Runtime-status policy

The policy follows the installed English `Status.ini`.

- Statuses `299-305` carry `HaltIntonate` and interrupt the cast in progress,
  but do not continuously block a later cast.
- Statuses `330-331`, `400-402`, `407-408`, `564`, `1433`, `1436`, `1444`,
  and `1446-1447` continuously block casting as stun/cage controls.
- Statuses `360-364`, `404`, and `1448-1449` continuously block casting as
  silence controls.

Active blocking controls are published as a small immutable snapshot. Skill
ingress and completion read that snapshot without waiting on status packet
I/O. The pending cast is claimed before the authoritative status mutation,
while the client receives the status snapshot before the interruption packet.

The generic status hook is ready for PvP or monster control effects. The
current game-server gameplay paths do not yet apply stun or silence statuses
to players, so each new source must call
`ApplyRuntimeStatusAndPublishAsync`; it must not modify only the client UI.

## Verification

Protocol checks cover:

- exact native `10171` packet bytes;
- cast timing metadata, including fractional seconds;
- ordinary combat casts with no early MP, damage, impact, or persistence;
- successful delayed backhaul completion;
- native request, movement, normal-attack, and replacement interruption;
- accepted authenticated realtime movement interruption;
- legacy/ECS lethal-damage interruption before HP/life commit;
- local caster and remote-viewer object-ID projection;
- exact-once notification after movement plus the client's follow-up cancel;
- cancellation-token ownership, start/interrupt ordering, and concurrent
  shutdown races;
- status-before-interruption packet ordering and Frozen one-shot semantics;
- stun and silence interruption plus blocking of later casts;
- stale monster-generation rejection and AOE observer completion parity; and
- no MP, vitals persistence, destination persistence, or scene change after
  interruption.

Run:

```powershell
dotnet build GodswarServer.sln --no-restore
dotnet run --project tests\Godswar.Server.ProtocolChecks\Godswar.Server.ProtocolChecks.csproj --no-build
```
