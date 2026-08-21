# Max-combat local fixture

These fragments are one transaction owned by
`tools/ProvisionLocalDevelopmentMaxCombatFixture.ps1`; do not execute a
fragment directly. The four dummy accounts use non-user credentials and are
reserved for the development combat-dummy host. The playable account is
`test25` with password `test25`.

| ID | Account | Character | Map | Camp | Build |
|---:|---|---|---|---|---|
| 7001 | `dummy_ares_bulwark` | AresBulwark | Sparta | Athens | Warrior, no Dodge gear |
| 7002 | `dummy_ares_mirage` | AresMirage | Sparta | Athens | Champion, Dodge |
| 7003 | `dummy_athena_bulwark` | AthenaBulwark | Athens | Sparta | Warrior, no Dodge gear |
| 7004 | `dummy_athena_mirage` | AthenaMirage | Athens | Sparta | Champion, Dodge |
| 7005 | `test25` | AresTempest | Sparta | Sparta | Champion, glass cannon |

The four dummy names describe their capital-map placement. Their camp is
deliberately the opposing faction so the stock client exposes hostile target
selection; the server still admits combat only for each exact configured
dummy tuple.

All five are level 160 with audited Q20/G25 equipment, Adamantium level 10,
rank-100 talents, max legal class skills, and a max Cupid. Each pet has 20,000
effective Savvy in all six attributes and the five reviewed rank-6 passive
skills. Their Owner-Merge Damage Rebound derives from Luck only; the Agility
curve is disabled. The four dummy pets are pinned in perpetual owner Merge.
AresTempest's pet uses the normal player-controlled Merge lifecycle, so
login/logout and manual toggles remain safe. Zodiac data is deliberately
absent.

Read-only status:

```powershell
.\tools\ProvisionLocalDevelopmentMaxCombatFixture.ps1 -Mode Status
```

Apply only after stopping the development server and closing Origin:

```powershell
.\tools\ProvisionLocalDevelopmentMaxCombatFixture.ps1 -Mode Apply -Confirm
```

The wrapper refuses non-development topology, checks the fixed-ID target
Redis leases even before the rows exist,
creates and validates a custom-format backup under
`artifacts/development-backups`, and performs exhaustive readback before the
transaction commits. Status compares the canonical identity, equipment,
progression, pet, loadout, and no-Zodiac invariants; `Applied` is fail-closed
and includes `DriftDomains` when any owned field differs. For AresTempest,
both a legitimate inactive Merge and a runtime-active Merge with the exact
derived bonus set are accepted. Its configured capital map and camp remain
exact, while its position may change through ordinary player movement.
