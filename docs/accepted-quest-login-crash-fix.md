# Accepted-quest login crash

## Symptom

The client intermittently exited after entering the world. The July 22 dump
`20260722141944.dmp` recorded access violation `C0000005` at `0x005D1CC3`.
The failing instruction dereferenced a null UI-control pointer while refreshing
the accepted-quest display.

This is separate from the character-selection avatar-builder crashes at
`0x005F060E` and `0x005F4ADD`, and from the later disconnect/state-transition
avatar-resource fault at `0x005F58BC`. The installed two-builder avatar guard
is intact and does not modify the quest handler or either separate call site.

It is also separate from the later world-target QuestView crash at
`0x00493A4E`, documented in `quest-view-target-crash-fix.md`.

## Protocol diagnosis

The installed client's native dispatcher provides the decisive opcode mapping:

- opcode `10090` maps to case `0x004E2475`;
- that case logs `MSG_PLAYER_ACCEPTQUESTS COUNT=%d`;
- it reads records with a `0x2A8`-byte stride and calls `0x005D1C90`;
- the crash is the first dereference in that refresh routine;
- opcode `10329` maps to the separate `MSG_OPEN_VIEWMAP_ID` handler and is not
  the source of this crash.

The PostgreSQL templates contain five historical opcode-10090 pages with
record counts `3, 3, 3, 3, 2`. They are accepted-quest snapshots captured from
one working-server character, not generic skill or talent bootstrap data. The
dump preserved a count of three at the caller, matching one of the first four
pages.

The working server sent accepted-quest pages before `EnterComplete`. The local
server replayed them much later, after `ClientReady`, player detail, and
`EnterUiReady`, when this client lifecycle could have null quest controls. More
importantly, replaying another character's quest state is invalid regardless of
timing.

## Runtime policy

`GameClientHandler.CanReplayCapturedPostEnterPacket` validates captured frame
lengths and rejects opcode `10090`. The historical templates remain available
for reverse-engineering, but login cannot send them. Skills and talents already
use their own authoritative packets, so they do not depend on these captures.

When quests are implemented, build opcode-10090 pages from the authenticated
character's authoritative quest state and send them in the original lifecycle
position: after the initial skill packets and before `EnterComplete`. Do not
re-enable the captured templates. An empty opcode-10090 packet is not a safe UI
initializer: the native handler still calls the quest-data refresh routine,
which dereferences its own controls even when the record count is zero.

## Verification

The protocol checks cover a native-shaped 2048-byte, three-record quest page,
malformed frames, and a valid non-quest frame. During a PostgreSQL-backed login,
the server should log one suppression summary and must not log any sent
`SynGameData` packet with opcode `10090`.
