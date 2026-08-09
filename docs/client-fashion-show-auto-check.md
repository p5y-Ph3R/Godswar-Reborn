# Fashion Show auto-check client patch

## Purpose

The stock client constructs the Fashion **Show** checkbox unchecked and only
auto-checks it once per process during world initialization. If a character
logs in without Fashion and equips a costume later, the authoritative server
shows the costume but the local checkbox can remain unchecked.

`PatchClientFashionShowAutoCheck.ps1` corrects that client-only UI mismatch.
It checks **Show** only when the local character changes native Fashion slot
12 from empty to occupied. It does not change the checkbox for:

- a Fashion-to-Fashion replacement;
- an equipment update for another player;
- legacy slots 13 or 14; or
- an ordinary refresh where slot 12 was already occupied.

The server already changes `FashionHidden` to `false` for an authoritative
empty-to-equipped transition. The patch therefore invokes only the native
checkbox setter and deliberately sends no duplicate visibility request.

## Native evidence

The supported installed client is the 6,676,480-byte PE32 image with SHA-256:

`92F6740BD0095F869C4FF54E7269CB4E21B8B43BB89A078AF711A5C1973AD181`

### Fashion controls and slots

`C:\Godswar Origin\Localization\en_us\UI\XML\ItemBagsExUI.xml`
defines these Fashion-tab controls:

| XML line | Control | ID | Native meaning |
|---:|---|---:|---|
| 59 | `Stylish` | 110013 | Fashion armor/costume |
| 61 | `Create` | 110014 | Legacy production-tool/create accessory |
| 63 | `Pet` | 110015 | Legacy equipped pet item |
| 66 | `VisPart2` | 110083 | Show checkbox |
| 68 | `VisPart3` | 110085 | Effect checkbox |

At VA `0x00573270..0x005732A1`, `Origin.exe` loads 21 controls using
`110001 + index`. Consequently the three Fashion-tab item controls map to
zero-based equipment slots 12, 13, and 14 exactly.

The validated incoming-equipment switch confirms the item types and record
addresses:

| VA | Item-definition type | Actor record | Slot |
|---|---:|---:|---:|
| `0x004ADA48` | `0x0C` (`stylish`) | `actor+0x7438` | 12 |
| `0x004ADA66` | `0x0D` (`create`) | `actor+0x7530` | 13 |
| `0x004ADA84` | `0x0E` (`pet`) | `actor+0x7628` | 14 |

`ItemBaseAttribute.xml` lines 1879-1880 contain the only stock `create`
items: IDs 7000/7001, tag `Buckhorn`, localized as **Deer Horn**, with
`Hair="1"`. Lines 1883-1884 contain the only stock `pet` items: IDs
7002/7003, tag `Ride16198`, localized as **Owl**, with `Speed` and `MaxHP`.
Slot 14 is a legacy equipped-item slot and is not the newer pet inventory and
summon system.

### Why the stock default is insufficient

- VA `0x005734C7..0x005734D9` obtains control 110083 and calls its setter with
  zero, constructing **Show** unchecked.
- VA `0x00579998..0x005799BB` restores `BagSet.xml` `Option1` into Show.
- VA `0x0057862A..0x00578689` auto-sends visibility opcode `0x27D8` with
  hidden flag zero and checks Show when slot 12 is occupied. Its process-global
  latch at `0x015AD190` is read and written only in this block, so it runs once
  per process rather than once per empty-to-equipped transition.
- VA `0x00576D5C..0x00576D92` forces Show and Effect unchecked whenever the
  slot-12 occupancy field at `actor+0x752C` is zero.

## Patch design

The hook replaces exactly one complete instruction:

| Purpose | File offset | VA | Original | Patched |
|---|---:|---:|---|---|
| common validated-item copy | `0x0ADB4E` | `0x004ADB4E` | `B9 3E 00 00 00` | `E9 4D 64 51 00` |

The continuation is VA `0x004ADB53`. At the hook:

- `EAX` is the destination equipment record;
- `EBP` is the actor;
- `EBX` is the incoming item record; and
- `[EAX+0xF4]` is the old occupancy marker.

The 67-byte trampoline:

1. saves all general registers and EFLAGS before any comparison;
2. requires `EAX == EBP+0x7438` (slot 12);
3. requires `[EAX+0xF4] == 0` (old slot empty);
4. calls the native UI accessor at VA `0x005736B0`;
5. requires `[ui+8] == EBP` (local actor only);
6. obtains the Show control at `[ui+0x5308]`;
7. invokes its native `vtable+0xDC` setter with `1`;
8. routes every success and failure path through the shared register/EFLAGS
   restore;
9. replays `mov ecx,0x3e`; and
10. returns to VA `0x004ADB53`.

Exact trampoline bytes:

```text
9C 60 8D 95 38 74 00 00 3B C2 75 2B 83 B8 F4 00 00
00 00 75 22 E8 F6 F6 BA FF 85 C0 74 19 3B 68 08 75
14 8B 88 08 53 00 00 85 C9 74 0A 8B 11 6A 01 FF 92
DC 00 00 00 61 9D B9 3E 00 00 00 E9 70 9B AE FF
```

### Exclusive executable allocation

The trampoline owns file offsets `0x5C3FA0..0x5C3FFF`, mapping to VA
`0x009C3FA0..0x009C3FFF`. This is the complete terminal 96 bytes of the
executable `.rdata` raw section. It begins immediately after, and does not
overlap, these existing allocations:

- QuestView frame guard: `0x5C3F00..0x5C3F1F`;
- character speed stats: `0x5C3F20..0x5C3F9F`.

The patcher requires all 96 owned bytes to be zero in the original hash state,
requires the exact padded trampoline in the patched hash state, verifies the
PE mapping and section boundary, and refuses partial, occupied, tampered, or
foreign states. It does not reuse an existing patch cave.

## Safe operation

Status is read-only:

```powershell
.\tools\PatchClientFashionShowAutoCheck.ps1 -Mode Status
```

Apply and revert refuse to mutate the exact target while its matching process
is running. Each real state change creates and hash-verifies a timestamped
backup, stages the candidate beside the target, verifies the exact allowed
57 changed bytes and expected SHA-256, replaces atomically, and attempts an
automatic restore from the backup if installation verification fails.

```powershell
.\tools\PatchClientFashionShowAutoCheck.ps1 -Mode Apply
.\tools\PatchClientFashionShowAutoCheck.ps1 -Mode Revert
```

The patched SHA-256 is:

`9354BDB00376E16F5C2D1E682637790D90C3930B8F3655456F8F49F3314C6728`

Run fixture-copy verification with:

```powershell
.\tools\TestClientFashionShowAutoCheckPatch.ps1
```

The test never modifies the source fixture. It covers Status, exact apply,
idempotent apply, exact bytes and branch targets, PE allocation ownership,
register/flag preservation patterns, local/slot/transition gates, tampered and
partial-state rejection, foreign-hash rejection, the running-process guard,
backup hashes, exact revert, and idempotent revert.
