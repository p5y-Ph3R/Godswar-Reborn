# Character Back to Realm Selection

## Outcome

The character-selection **Back** action returns to the realm list without dereferencing an unavailable UI root during the reconnect transition. Login now stops at realm selection; choosing a realm manually still uses the stock client path.

The stock Back flow does not send a dedicated server opcode. It disconnects the current `CNetClient`, creates a new login connection, sends the normal opcode `1` login packet, and marks the role page as returning from character selection.

## Root cause and guard

The crash was at VA `0x005F58BC`: the Back routine loaded the UI root at `0x015760A0` and dereferenced it while it was null. A second root at `0x0157608C` could be unavailable during the same lifecycle state.

The patch changes only these reserved locations:

- Hook: file `0x1F58B6`, VA `0x005F58B6`, 6 bytes.
- Executable cave: file `0x53E3E0`, VA `0x0093E3E0`, 61 code bytes inside an audited 112-byte zero reserve.

When lifecycle state `2` is active and either root is missing, the guard preserves the required stock state writes and skips the two unsafe virtual calls. All other paths replay the displaced instruction and continue through stock code.

## Composable client states

The patcher recognizes exact SHA-256 hashes and exact bytes for all eight supported composites:

| Octagram visual | Manual realm selection | Back guard | SHA-256 |
|---|---|---|---|
| reverted | original | original | `74ADEEC986C7005CE1A986027AFB8AAAEEC8E4DA58CA3A28F3794E3DC14C442C` |
| reverted | patched | original | `9896D740DB9FC3A82478DFB696A70E3BB3D9F8619E4575069F1BA311B39AD4CA` |
| reverted | original | patched | `C22D932A70A037B0983DE7DAB3D3A9DA44DD3A56DB143C6D31FBCA8913EF50F9` |
| reverted | patched | patched | `318BA84B9F7720E827D91F658387D6FA2C9F61E8E05D5901647F54EE525208DF` |
| applied | original | original | `8D15E202D8178927E69F06909659EA14DD7FD0EE8BE853BD3394E5EEE684D31F` |
| applied | patched | original | `4EF7A3A5F62BB739081CD76425D4AF14BEFDB03D1F36DABECF66624B1C4BA2DB` |
| applied | original | patched | `FE01690D51B5A6C1FAEE48627372F35FFE9E110966E01F7D1EA96163EE8DEF61` |
| applied | patched | patched | `FFCC3508FA48DCCEF1135BD92194BD46A95872B4CED914FE5B025801C9C5AFD5` |

Apply and Revert preserve both the independent manual-realm-selection patch
and the pet-owner merge octagram selector/scaler visual. Status and mutation
results report the latter as `PetOwnerMergeOctagram = Applied` or `Reverted`.
Unknown hashes, partial patches, unexpected cave references, or modified
surrounding bytes are refused.

## Operation

Close `Origin.exe` before Apply or Revert. Status is read-only.

```powershell
.\tools\PatchClientCharacterBackRealmReturn.ps1 -Mode Status
.\tools\PatchClientCharacterBackRealmReturn.ps1 -Mode Apply
.\tools\PatchClientCharacterBackRealmReturn.ps1 -Mode Revert
```

Each mutation creates a verified backup under `C:\Reborn\backups`, stages and validates the complete output, then replaces the executable transactionally. A failed replacement is restored automatically.

Run the isolated fixture suite with:

```powershell
.\tools\TestClientCharacterBackRealmReturnPatch.ps1 -FixtureExe 'C:\Godswar Origin\Origin.exe'
```

The suite synthesizes all four manual-selection/octagram input planes in
temporary fixture copies. It covers all eight exact hashes, Apply/Status/Revert
and idempotence, exact preservation of both peer patches, exact mutation
ranges, branch targets, cave ownership, tamper refusal, rollback inputs, and
root-ready/root-missing path models. The source fixture and live client are
never modified.
