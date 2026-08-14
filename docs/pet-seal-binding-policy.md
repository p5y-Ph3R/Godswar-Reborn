# Pet Seal binding policy

Sealing preserves the pet's binding state in its packed jade:

- a bound pet produces a bound, non-tradable packed Seal Jade (`10109`);
- an unbound pet produces an unbound, tradable packed Seal Jade; and
- unsealing restores the same pet and does not change whether it is bound.

The server owns both fields independently. The packed item's native record
projects its durable `bound` value at byte `+26` and the authoritative linked
pet ID at dword `+56`. A valid `sealed_pet_items` row ties the item instance,
pet, and current owner together. Moving a bound packed jade to another owner
must fail; moving an unbound packed jade also moves the sealed pet and its
link atomically.

For a single empty Seal Jade (`10108`), the server changes that item instance
to packed `10109` in place and does not require a free bag slot. For a stack,
it decrements the stack and requires one empty slot for the separate,
non-stackable packed item. An in-place template change still replaces an
existing native bag object: send opcode `10052` to clear the changed occupied
slot before the complete `10033` detail and `10056` slot-index refresh.

The guarded two-locale client resource patch updates the Seal information,
success, and former-rule replay pages. It says that only an unbound/tradable
packed jade transfers its pet. The legacy result `1072` remains decodable for
durable receipts created under the former rejection policy, but now asks the
player to retry instead of claiming that bound pets cannot be sealed.
