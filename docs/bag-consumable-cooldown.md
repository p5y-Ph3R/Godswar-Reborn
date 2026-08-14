# Bag-consumable cooldowns

Bag-item activation enforces the stock client cooldown metadata on the
server. The client clock is presentation only: changing or suppressing it
cannot make an item activate before the authoritative deadline.

## Stock metadata

The policy uses the checked-in English client data:

- `ItemBaseAttribute.xml` SHA-256
  `F6BFC99191134B79E0EEAF5A56C9AB14EF0C58E23C4574F03C3AD9C13BCE1366`
- `Magic.ini` SHA-256
  `08AEC7E0453C24ECD3C8C697AFAFA85F6DD812E83C91EC2F145B2A7A73F7C0AF`

An item participates only when its template is a `consume item`, has
`Use=1`, and links to a positive `Skill` group. The linked Magic section's
`CoolingTime` is the duration. The reviewed data contains 244 one-second
groups and 67 two-second groups. A stock value of zero, or a group with no
Magic timing, does not start a clock.

Morning Dew templates 10130 through 10144 share Skill group 4721, whose
stock `CoolingTime` is one second. Because the deadline is keyed by group,
different Morning Dew tiers cannot bypass one another's cooldown.

## Durable state and transaction boundary

Migration `20260813_090_bag_consumable_cooldown_state` creates
`character_bag_consumable_cooldowns`, keyed by character and cooldown group.
Each row stores `ready_at` and `updated_at` as PostgreSQL `timestamptz`
values.

Activation runs inside the existing durable command transaction:

1. Lock the character and bag item.
2. Seed and lock the character/group cooldown row.
3. Reject with durable status 96 (`ConsumableCooldownActive`) when
   `ready_at` is later than PostgreSQL `transaction_timestamp()`.
4. Run the authoritative item transition when the group is ready.
5. Advance the deadline only if that transition succeeds, using
   `GREATEST(ready_at, transaction_timestamp()) + duration`.
6. Commit the item mutation, cooldown, audit, inbox receipt, inventory
   ledger, and outbox work together.

The server never accepts a client timestamp. PostgreSQL owns both the clock
and row serialization. Failed item rules do not start a cooldown. A replayed
operation is resolved from `command_inbox` before activation, so replay
cannot recheck or extend the deadline. Different operation IDs serialize on
the character/group locks.

The wrapper covers every consumable transition currently supported by the
bag activation executor: pet eggs, Special Pet Shed, Pet Enhance Spring and
Golden Apple Juice, and Morning Dew pet-experience items. Items without
stock cooldown metadata follow the same transition without acquiring a
deadline. Unsupported consumables remain unsupported and cannot mutate
state.

## Native client clock

The stock client already has a generic request-side clock. At VA `0x574232`
it reads the selected item's template, checks `Use=1` at template offset
`+0x2E`, reads the runtime cooldown group at `+0x74`, and calls
`ItemBagsUI::StartCooling` at VA `0x573D80`. `StartCooling` resolves the
linked Magic record and reads its `CoolingTime` at `+0x60`; it is not a
Morning Dew special case.

The composed client patch preserves exactly one request-side call and
captures that runtime group. After the server's ordered eight-packet opcode
10033 bag projection reaches page 3, half 12, it calls the normal bag refresh
first and reapplies the same stock clock second. This reanchors short stock
clocks after the authoritative projection. Opcode 10056 slot-index packets
are ignored by this client dispatcher and do not replace the final detail
state. A two-entry, zero-initialized queue handles two activations awaiting
their ordered projections; no item or group ID is hardcoded.

The exact offline composition is:

- S1 predecessor SHA-256
  `00ED99F0EADB605059CB7A0FA476922EC6EA9E3EAE9218710C20299992706BDB`
- S2 successor SHA-256
  `7D1F17A21B0D34DA8BE61C639D72BFB4A518A2F3B0B3B0001699ADC560FA0021`
- request capture hook file range `0x17428E..0x174293`
- response refresh hook file range `0x0EB968..0x0EB96D`
- audited executable cave `0x51BF67..0x51C000`, with capture code in
  `0x51BF67..0x51BF85`, response code in `0x51BF90..0x51BFD6`, and the
  remainder still zero
- runtime-only queue `0x00A40010..0x00A40018` in the PE's writable,
  zero-initialized `.data` extent

The patch changes 91 file bytes. It does not overlap S1's gender-refresh
ranges `0x5C341F..0x5C347F` and `0x5C3480..0x5C3485`, or the existing
appearance-refresh code from `0x5C3485` onward. Use
`PatchClientBagConsumableCooldown.ps1` for guarded status/apply/revert and
`TestClientBagConsumableCooldownPatch.ps1` for the disposable byte-exact
round trip. The patcher accepts only the exact S1 or S2 whole-file hash.

The native stack and register contract is byte-guarded. At the request hook,
the original caller has pushed `group` and then `bagUI`, so the capture
wrapper enters with `[ret, bagUI, group]`. It reads `[esp+8]` without moving
the stack and tail-jumps to the stock callee, whose audited `ret 8` removes
both original arguments. At the response hook, saving `EBP` and `ESI` makes
wrapper `[esp+0x38]` resolve exactly to the dispatcher's original
`[esp+0x2C]` packet local. The wrapper restores both registers; it does not
touch `EBX` or `EDI`, and only the same volatile registers and flags as the
replaced native call may change.

The cave occupies the tail of the final executable `.text` page and ends
exactly at VA `0x0091C000`, where `.rdata` begins. S1 has no inbound relative
branch or absolute pointer-shaped reference into the cave; S2 has exactly
the two hook branches listed above and no absolute cave reference. The queue
is outside file-backed `.data`, is loader-zeroed and writable, and S1 has no
direct references to either queue word.

## Verification

Focused checks cover:

- exact Morning Dew, egg, shed, one-second, and two-second policy mapping;
- zero/missing timing and non-consumable exclusions;
- additive, durable, non-destructive migration SQL;
- an exact one-second committed Morning Dew deadline;
- active-group rejection before pet or inventory mutation;
- unchanged deadline on rejection;
- duplicate replay without deadline extension; and
- explicit fixture expiry before later integration scenarios.

The PostgreSQL integration check requires a disposable database whose name
starts with `godswar_b12_`. Set
`GODSWAR_TEST_POSTGRES_CONNECTION_STRING`, then run:

```powershell
dotnet run --project tests/Godswar.Server.ProtocolChecks/Godswar.Server.ProtocolChecks.csproj --no-build -- "PostgreSQL authoritative bag-consumable cooldown"
```

The clean-schema scenario applies migrations through 088 Soul, 089 Pet
Manager utility, and 090 cooldown state, publishes the reviewed item and pet
content, and proves active rejection, replay safety, deadline stability,
expiry, and the later commit. The client patch check runs independently:

```powershell
& tools/TestClientBagConsumableCooldownPatch.ps1
```
