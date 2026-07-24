# Character-selection loading gate V3 failure

## Decision

Readiness-only loading gate V3 is rejected. Its immutable evidence run is:

`artifacts/network-shim/manual-parity/20260724T043833399Z-2bd75dd7`

The run was sealed as `Fail` on 2026-07-24. Do not relabel its earlier
account-7 observation as acceptance.

## Observed failure

The first cold account-13/test2 launch followed this sequence:

| Event | UTC |
| --- | --- |
| Game connection accepted | `2026-07-24T09:00:33.719747330Z` |
| LoginGameServer received | `2026-07-24T09:00:34.227898774Z` |
| CharacterPreview opcode `10002` sent | `2026-07-24T09:00:34.240423865Z` |
| Client closed the TCP connection | `2026-07-24T09:00:49.032432521Z` |

The server accepted the account, sent AfterLogin and CharacterPreview, did not
send a full/reject/error response, and did not restart. The client never sent
EnterGame. It waited for about 15 seconds, displayed the native
server-unavailable dialog, and crashed after the dialog was acknowledged.

The new dump is:

- path: `C:\Godswar Origin\Dump\20260724210050.dmp`
- SHA-256:
  `7A5B34B86A2A2E9F8281A1B9F7DDDA9579AAE9AFDC839E4A43D26C7575E993D9`
- exception: x86 `C0000005`
- fault VA: `0x005F58BC`
- fault input: avatar root `0x015760A0` was null

An immediate clean relaunch received CharacterPreview at
`2026-07-24T09:01:02.839742914Z` and sent EnterGame only
`0.695400` seconds later. This is an intermittent client resource-lifecycle
race, not server capacity.

## Why V3 failed

V3 correctly preserved native `Process()` delegation, message identity, and
message order. It retained the one audited preview until all six avatar roots
were non-null. However, returning null from `PickMsg()` also exits the LOGIN
update's packet-dispatch path. If the native character-selection lifecycle has
not initialized the roots, the retained preview cannot itself make them ready.
The client then reaches its independent selection timeout after about 15
seconds.

Adding a server delay is rejected. Working-original captures send AfterLogin
and CharacterPreview nearly back-to-back, and the failed local run already had
more separation than those captures.

## Final bounded correction

The final V4 attempt is a matched, reversible Origin/Net pair:

1. The shim recognizes the first exact AfterLogin bootstrap record and
   schedules native state 2 without overwriting another pending transition.
2. Immediately after native LOGIN state registration, Origin invokes its
   existing LOGIN initializer at `0x00467280` on the main thread and verifies
   all six avatar roots.
3. The opcode-`10002` gate remains fail-closed and releases only the original
   pointer after readiness.
4. The later `0x005F58BC` path checks all six roots. If one is missing, it
   skips the unsafe avatar calls and schedules a clean state-2 transition.

If the next cold test still reaches the waiting/server-unavailable path, stop
iterating on this correction. Restore Net first while Origin remains V4, verify
stock Net with no `NetLegacy.dll`, then run
`PatchClientAvatarPreload.ps1 -Mode Revert`; continue with the next secure
network milestone. That boundary is a product decision, not evidence V4 passed.

## Installed V4 candidate

The matched candidate is installed with automated tests passing:

- Origin SHA-256:
  `E0F5BC951C6E37550F4D9CC1E25BFDCB4F020466ADD854DC2E7EA04E0D22F81C`
- Origin Apply backup:
  `C:\Reborn\backups\origin-avatar-preload-v4-Apply-20260724-213316596-5256fb25`
- Net SHA-256:
  `EF531F8CB20A4FCA8D1DBA979FD131ECA002383AE862890435426DF948817597`
- Net Apply/stock-restore backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-213354864`
- Net Apply manifest SHA-256:
  `5E8986F01742F855D2248B899C58590AB57F4B72D1C27A10F25BDEC290CAD04B`
- Legacy SHA-256:
  `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C`
- live state: one final cold smoke pending; not accepted
