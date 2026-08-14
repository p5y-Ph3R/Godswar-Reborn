# Pet Soul Contract

Soul Contract is a stock Pet Manager operation for the currently summoned
pet. It is independent of pet-to-pet Merge and is a prerequisite for Rebirth.

## Native protocol

- C2S opcode `10270` is exactly 12 bytes.
- Offset 4 contains Contract Spirit template `10105` as a little-endian
  dword, including the zero-spirit selection.
- Offset 8 contains a count from zero through five.
- Offsets 9-11 are zero.
- S2C opcode `10271` is exactly 5 bytes and writes the absolute contract stage
  from offset 4 into the active native pet. Stage 1-6 corresponds to spirit
  count 0-5.

The server resolves one authoritative summoned, owned pet. It locks the pet
and any required inventory stacks, consumes only a positive selected count,
persists the replacement stage, and commits command/audit/outbox evidence in
one transaction. Secure retries reuse the exact request identity; a delayed
duplicate never replays stale `10271`.

## Attribute rule

Installed-client `Pet_Alter.xml` contains `Base_Alter` values
`300,400,500,600,700,800`. Dividing by 100 gives the fixed value added to each
of all six displayed Savvy totals:

| Spirits | Stage | Displayed increase per attribute |
|---:|---:|---:|
| 0 | 1 | +3.00 |
| 1 | 2 | +4.00 |
| 2 | 3 | +5.00 |
| 3 | 4 | +6.00 |
| 4 | 5 | +7.00 |
| 5 | 6 | +8.00 |

Signing again replaces the prior stage; it does not stack another bonus.
Persisted `initial_savvy` (the raw Basic value), Added, Growth Rate, and
Rebirth acceleration are not rewritten. Player-visible total semantics derive
the one fixed bonus from `soul_contract_stage`.

### Rank annotation correction

Stock `PetIndentureUI.xml` also declared red text control `PetPinjie2`
(`ID=871018`) beside Rank. Native redraw `0x005BEDE8` looked up that control
and formatted the Soul value into it, even though signing does not mutate the
persisted pet Rank. That presentation was misleading.

Both installed locale resources now omit only `ID=871018`. Native code tests
the lookup result at `0x005BEDF1` and branches over the complete formatter at
`0x005BEDF3` when it is null, so the omission is supported by the stock
control path. Numeric Rank remains a separate `PetPinjie` control
(`ID=871012`) looked up at `0x005BEC99`. The six dynamic attribute annotations
(`872011`, `872021`, `872031`, `872041`, `872051`, and `872061`) are untouched
and continue through the independent six-entry redraw at `0x005BF7A0`.

- exact predecessor, each locale: `11359` bytes,
  `90C5288452CA1B7B4944DD1FBE799FA3D828CE5C52381006B009607F4393CADD`
- exact successor, each locale: `11219` bytes,
  `E302C6E340D16A1590C329E9E52DA300AF933696C6B945A973098C4A6966CCB4`

Use `PatchClientPetSoulContractRankAnnotation.ps1` for guarded two-locale
status/apply/revert and
`TestClientPetSoulContractRankAnnotationPatch.ps1` for the disposable exact
hash and byte-round-trip checks.

Native effective-Savvy routine `0x006A0790` adds Basic, Added, learned-skill
trait bonuses, and the Soul value returned by `0x006A1E30`. The latter reads
the stage at pet bean `+0xB9` and maps it through `Base_Alter`. Stock owner
Unite calls the effective routine at `0x006ACF74`, so owner-Merge contribution
uses the Soul-adjusted total. The trait-requirement loop also calls it at
`0x006AA09C`. Pet-to-pet Merge is the explicit exception: the installed
Pet Manager guide says Soul Contract status has no effect, and that server
path continues to use raw Basic and Added inputs.

Migration `20260813_088_pet_soul_contract` stores stage 0-6, derives the legacy
boolean contract flag, and admits `pet_soul_contract` to durable evidence.
Owned-pet bootstrap offset `0xA1` carries the exact stage rather than a boolean.

## Client refresh boundary

Stock handler `0x006A11A0` consumes `10271`: instruction `0x006A11B4`
writes the stage byte to active pet bean `+0xB9`. The open Soul Contract
window separately recognizes `10271` in `0x005BE090` and calls its full
six-attribute redraw at `0x005BF670`. Pet Detail also rereads the selected
live bean from its visible update callback (`0x005B6BD0`).

Owner Unite is different. Its preview is calculated only by the open and
selected-pet paths (`0x005C6656` and `0x005C6A24`) through `0x005C6680`.
Soul and Unite are ordinary peer windows, not mutually exclusive modals: the
Soul open path `0x0069B0F0` and Unite open path `0x005C6630` merely focus
their window through the shared UI manager. Consequently stock `10271` can
leave an already visible Unite preview stale.

The composed S3 client patch fixes that narrow presentation gap. It replays
the stock stage store, preserves all registers and flags, loads only an
already existing Unite manager, requires the preview's selected pet to be
the same ID as the active pet bean's ID, and passes that ID to the stock
setter `0x005C6A10`. That setter already checks the window pointer and visible
flag before recalculating, so the patch neither opens nor allocates Unite UI.
It adds no roster projection.

- S2 predecessor SHA-256:
  `7D1F17A21B0D34DA8BE61C639D72BFB4A518A2F3B0B3B0001699ADC560FA0021`
- S3 successor SHA-256:
  `48420B7AE83AD3DE17E33E22D270FC30B7E3656D6F070BADDD52761AAB4418BB`
- hook: file `0x2A11B4..0x2A11BA`
- isolated executable cave: file `0x5C3366..0x5C3400`
- runtime state: none

Use `PatchClientPetSoulOwnerMergeRefresh.ps1` for guarded
status/apply/revert and `TestClientPetSoulOwnerMergeRefreshPatch.ps1` for
the disposable S2-to-S3 byte-exact round trip.

## Player HP boundary

Soul signing does not deduct player HP or MP. The durable transaction changes
the pet contract stage and consumed inventory only. Its committed projection
emits `10271`, bag deletion acknowledgements, and a bag refresh; it emits no
player-vitals (`0x2771`), player-status (`0x27B6`), or extended-status
(`0x27B7`) packet. It also does not run the startup owner-Merge bonus
reconciler. That reconciler reads only pets already marked
`contributes_to_character`; a summoned Soul-contract target is not such a
pet.

The common durable-pet reload preserves the player's absolute current HP and
accepts the authoritative calculated maximum. Consequently, if some separate
derived-stat change raises maximum HP, the bar percentage can become lower
without any HP being removed. The next passive-recovery packets then refill
the difference. This is a maximum-versus-current presentation effect, not a
Soul Contract damage effect.
