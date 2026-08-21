# Manual realm selection client patch

## Purpose

The stock client automatically submits its saved or recommended realm as soon
as the final `GameServerInfo` record arrives. With multiple realms, successful
credential login therefore skips the server-selection screen and proceeds to
character selection.

`PatchClientManualRealmSelection.ps1` suppresses only that automatic submit.
The client can still read `LastSelectServer.xml` and highlight its preferred
realm, but it does not send `SelectServer` until the user activates the native
**Enter Game** action.

This patch does not change the character-selection **Back** reconnect path.
That path already has a separate native return-state gate and is handled by a
separate fix.

## Native evidence

The supported composite client is the 6,676,480-byte PE32 image with SHA-256:

`74ADEEC986C7005CE1A986027AFB8AAAEEC8E4DA58CA3A28F3794E3DC14C442C`

The terminal realm-record dispatcher calls VA `0x005F98E0`. That handler:

1. checks the character-selection return gate at VA `0x005F990D`;
2. reads `LastSelectServer.xml` through VA `0x005FC8D0`, or chooses the
   recommended realm record;
3. calls VA `0x005FBDB0` from VA `0x005F9A19`.

VA `0x005FBDB0` constructs and sends a 44-byte opcode `4` (`SelectServer`)
packet. The real server-page **Enter Game** event independently calls the same
sender from VA `0x005F699A`. After selection, the native line handler builds
the 92-byte opcode `6` (`LoginReturnInfo`) packet at VA `0x005FC31E`.

The server handlers already require the explicit wire sequence:

```text
Login -> ServerList -> SelectServer -> SendServer -> LoginReturnInfo -> Redirect
```

The unwanted transition was therefore client-driven, not a server-side realm
auto-selection.

## Patch design

Exactly one complete instruction is replaced:

| Purpose | File offset | VA | Original | Patched |
|---|---:|---:|---|---|
| automatic realm submit | `0x1F9A19` | `0x005F9A19` | `E8 92 23 00 00` | `90 90 90 90 90` |

The original relative call targets VA `0x005FBDB0`. The patch uses five NOPs,
so execution continues through the native function epilogue without sending a
packet. No code cave is used.

The patcher separately pins and preserves:

- the manual call at VA `0x005F699A`;
- the terminal realm-record dispatch;
- saved-server lookup and the Back return gate;
- opcode `4` packet construction; and
- opcode `6` post-selection continuation.

The manual-selection-only SHA-256 is:

`9896D740DB9FC3A82478DFB696A70E3BB3D9F8619E4575069F1BA311B39AD4CA`

### Composite patch preservation

The independent character-selection Back guard owns file offset `0x1F58B6`
and its 112-byte cave at `0x53E3E0`. The manual-selection patcher recognizes
and validates both exact Back-guard ranges but never includes them in its
mutation allowlist.

The independent pet-owner merge octagram visual changes its selector call at
`0x2A1780` and its 208-byte selector/scaler cave at `0x53E580`. Those ranges
are also outside the manual-selection mutation allowlist. The patcher reports
their recognized state as `PetOwnerMergeOctagram = Applied` or `Reverted`.

All eight exact compositions are supported:

| Octagram visual | Manual selection | Back guard | SHA-256 |
|---|---|---|---|
| reverted | original | original | `74ADEEC986C7005CE1A986027AFB8AAAEEC8E4DA58CA3A28F3794E3DC14C442C` |
| reverted | patched | original | `9896D740DB9FC3A82478DFB696A70E3BB3D9F8619E4575069F1BA311B39AD4CA` |
| reverted | original | patched | `C22D932A70A037B0983DE7DAB3D3A9DA44DD3A56DB143C6D31FBCA8913EF50F9` |
| reverted | patched | patched | `318BA84B9F7720E827D91F658387D6FA2C9F61E8E05D5901647F54EE525208DF` |
| applied | original | original | `8D15E202D8178927E69F06909659EA14DD7FD0EE8BE853BD3394E5EEE684D31F` |
| applied | patched | original | `4EF7A3A5F62BB739081CD76425D4AF14BEFDB03D1F36DABECF66624B1C4BA2DB` |
| applied | original | patched | `FE01690D51B5A6C1FAEE48627372F35FFE9E110966E01F7D1EA96163EE8DEF61` |
| applied | patched | patched | `FFCC3508FA48DCCEF1135BD92194BD46A95872B4CED914FE5B025801C9C5AFD5` |

Apply and Revert move only horizontally between the two states in the same
Back-guard and octagram plane. Consequently installing or reverting manual
realm selection preserves the exact Back hook/cave and the exact octagram
selector/scaler visual bytes.

## Safe operation

Status is read-only:

```powershell
.\tools\PatchClientManualRealmSelection.ps1 -Mode Status
```

Apply and revert refuse to mutate the exact target while its matching process
is running. Each real state change creates and hash-verifies a timestamped
backup, stages and verifies the candidate beside the target, replaces it
atomically, and attempts automatic restoration if installation verification
fails.

```powershell
.\tools\PatchClientManualRealmSelection.ps1 -Mode Apply
.\tools\PatchClientManualRealmSelection.ps1 -Mode Revert
```

All eight exact hashes are supported, so Status, idempotent Apply, and
idempotent Revert continue to work whether the Back guard is absent or
installed and whether the octagram visual is reverted or applied. Foreign,
partial, or tampered client states are refused.

Run fixture-copy verification with:

```powershell
.\tools\TestClientManualRealmSelectionPatch.ps1
```

The test accepts any supported live state, synthesizes all four orthogonal
manual-selection planes only in temporary copies, and never modifies the
source fixture. Coverage includes all eight exact hashes, exact guard and
octagram preservation, PE mapping, relative call targets, five-byte-only
mutation, manual-path preservation, native opcode continuations, Status,
Apply/Revert idempotence, verified backups, exact rollback, running-process
refusal, and foreign, partial, and prerequisite-tamper rejection.
