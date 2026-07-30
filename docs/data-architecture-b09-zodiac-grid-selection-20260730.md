# B09 durable Zodiac skill-grid selection increment

Date: 2026-07-30
Status: implemented and verified; included in the completed B09 closure

## Scope and evidence

This increment closes the reachable Zodiac `10297` SID `102` mutation. It
persists which learned skill family is assigned to one active Zodiac grid. It
does not implement the later combat-stat or MP-cost effects of that assignment.

The request contract is reverse-derived from the installed
`C:\Godswar Origin\Origin.exe`; no retail client-to-server capture exists in
the repository:

- VA `0x552FD4` calls `ConsEventRequest(255, 102, grid, Kind)`;
- `v1` is the zero-based grid `0..15`;
- `v2` is the `MagicMini.xml` skill-family `Kind`, or `-1` to clear;
- `v3` is zero;
- module `255` is native and module `0` remains compatibility-only;
- the native precheck rejects one `Kind` appearing twice in a four-grid row;
- grids `0..3` and `8..11` accept `10000..19999`;
- grids `4..7` and `12..15` accept `20000..29999`.

The exact native grid-1/Mage-Kind-10057 request is:

```text
1800392800000000FF006600010000004927000000000000
```

`Origin.exe`'s response handler writes response `v2` directly into the grid
record and has no failure branch. Therefore the server emits SID `102` only
for the first committed success. Replays and rejections receive authoritative
full sync instead.

## Authoritative validation

`State/ZodiacSkillGridSelection.cs` owns the pure transition. The server
validates, in order:

1. the grid is active;
2. the selected Kind prefix belongs to that grid row;
3. the exact `MagicMini.xml` Kind belongs to the character profession;
4. the character learned one runtime skill in the Kind's five-rank
   `Magic.ini` family;
5. another grid in the same four-grid row does not already select it;
6. the target value is not already selected.

The exact shipped Kind/profession catalog is explicit. A Kind maps to runtime
IDs `(Kind % 10000) * 10` through `+4`. Client-supplied class, learned state,
grid level, or existing row state is never trusted.

## Durable transaction and replay

Secure legacy transport classifies SID `102` in:

- `SecureZodiacSkillGridSelectionIdentity.*`;
- `SecurePendingOperationRegistry.Zodiac.cpp`.

The operation identity is the native UUID associated with authenticated
principal, authenticated character, grid, and selected Kind. An exact retry
or reconnect reuses the pending UUID until a terminal family-21 result settles
it. Malformed SID `102` packets fail closed without allocating pending state.
Raw legacy TCP remains an explicitly measured compatibility path because it
cannot supply truthful retry identity.

`PostgresZodiacSkillGridSelectionCommandExecutor*`:

1. locks the owned `character_base` row;
2. checks the character-scoped inbox before mutable grid state;
3. reads the complete four-grid row and learned-skill family;
4. derives the transition in server code;
5. writes permanent audit and inbox evidence;
6. on success, updates exactly one grid with an expected-value predicate and
   appends one latest-wins outbox event;
7. commits before any native acknowledgement.

Deterministic rejections are permanent non-mutating receipts. Request-hash
conflicts increment the bounded inbox counter. The outbox aggregate is scoped
to one grid, so unrelated grids have independent latest-wins revisions.
There is no cross-store dual write.

JSON compatibility uses the same pure transition behind its serialized store
gate. The live session uses the existing Zodiac gate shared with energy
accrual, activation, and upgrades, then applies the returned authoritative
projection to the in-memory character.

## Native response behavior

For a newly committed success:

1. send native SID `102` with `v1=grid`, `v2=selected Kind`;
2. send full Zodiac sync;
3. send secure family-21 terminal result.

For an exact replay or stored rejection:

1. suppress SID `102`;
2. send current authoritative full sync;
3. settle the native UUID as replayed or rejected.

Invalid envelope, operation conflict, wrong owner, provider failure, and
cancellation fabricate no projection. Provider/cancellation failures leave
the UUID pending so the client can retry without risking a second mutation.

## Verification

Focused managed verification:

```powershell
dotnet build GodswarServer.sln --no-restore -c Release
dotnet run --project tests/Godswar.Server.ProtocolChecks -c Release `
  --no-build -- "Durable Zodiac skill-grid selection contracts"
```

The focused contract covers UUID/request-hash identity, secure-only envelope
provenance, exact row/class/learned/duplicate rules, clear behavior, immutable
receipt encoding and hashing, and replay projection semantics.

The focused game-handler check proves a canonical secure UUID executes once;
the first commit emits SID `102`, then full sync, then family-21 `Applied`;
an exact replay emits no second SID `102`, repairs with full sync, and settles
`Replayed`. Secure tokenless and nonzero-tail packets never reach either the
durable executor or compatibility store.

Native tests cover the exact request vector, module-zero compatibility,
malformed and wrong-row failure, principal/character binding, reconnect
reuse, intent isolation, bounded capacity, expiry, and terminal tombstones.

## Remaining limitation

Selecting a Kind is now durable and visible. Its passive combat enhancement
and MP-cost behavior remains deliberately unwired: those formulas require
separate authoritative reconstruction and combat tests. This increment does
not infer them from UI text or allow the client to dictate resulting stats.
