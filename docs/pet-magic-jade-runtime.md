# Magic Jade and pet Bind runtime

Magic Jade appearance change and summoned-pet Bind are authoritative durable
Pet Manager commands. They do not trust a client species or pet ID.

## Appearance change

The exact stock request is Pet Manager dialog `31`, sub-ID `8`, with the
selected bag page/index in argument 6. The server converts that coordinate to
one absolute slot and, in one PostgreSQL transaction:

1. fences the authenticated character and locks its one summoned pet;
2. locks the exact selected bag item after the pet (the shared lock order);
3. requires the pet to be bound, carried, summoned, owned, and not owner-merged;
4. resolves the target species only from the pinned Magic Jade mapping;
5. changes only species, pet revision, and update time;
6. consumes exactly one Jade, advances inventory revision, and writes command,
   pet-operation, inventory-ledger, inbox, and outbox evidence.

Results are `130` success, `137` missing Jade, `138` incompatible/unavailable,
`139` no summoned pet, and `140` unbound pet. A duplicate operation ID returns
the stored receipt and cannot consume or reapply. Migration 086 admits
`change_appearance` evidence.

## Bind

The stock nested confirmation is dialog `31`, sub-ID `7`, argument 0 `112`.
It targets the authenticated summoned, carried, owned, non-merged pet. The
transaction sets only `bound = true`, pet revision, and update time; it never
mutates inventory or Soul Contract state. Results are `1073` success, `1072`
already bound, and `1075` no available summoned pet. Migration 087 admits
`bind` evidence.

## Client projection

Successful/current reconciliation uses the patched 72-byte form of opcode
`10286`: the established 68-byte level/Basic/Added payload, species at byte
68, bound at byte 69, and zero reserved bytes 70-71. When the receipt pet is
still the current summoned companion, presentation order is Recall result,
10286 refresh, Call Out result, then world presence. Delayed retries refresh
the receipt pet from current state and never cycle a different newly summoned
pet. Opcode `10237` is not used because its native collection rebuild can emit
an unintended Recall.

Raw TCP requests receive server-scoped operation IDs only in the validated
LocalDevelopment profile. Production requires the secure client operation ID
and fails closed otherwise.
