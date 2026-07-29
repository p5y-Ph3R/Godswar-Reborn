# Gear Enhancement authoritative transaction

This is the durable server contract shared by the physical Gear Mentor and
Origin Enhancer flows documented in
[Origin Enhancer and Gear Mentor UI](gear-enhancement-ui.md).

The client only selects three bag references and displays a result. It never
decides eligibility, cost, success, or the resulting item data.

For every Gear Mentor or Origin Enhancer confirmation, the server transaction
must:

1. Revalidate the resolved, currently visible physical NPC/dialog pair
   (`*_070`/`4` or `*_143`/`118`) plus the requested operation. There is no
   virtual-NPC enhancer exception.
2. Lock and reload the character and all selected bag slots from persistence.
3. Require three distinct, owned, unchanged bag selections.
4. Revalidate that the first item is eligible equipment.
5. Revalidate the exact Attribute Stone, catalyst kind, stack quantity, allowed
   attribute family, duplicate/presence rule, and Quartz level.
6. Build the new equipment state and material decrements in memory.
7. Persist all three slot mutations atomically, or persist none of them.
8. Refresh the authoritative character/bag state returned to the client.

The staged slot snapshot prevents a stale or replayed confirmation from
consuming a different item that later occupied the same bag slot. Concurrent
or repeated confirmations are serialized by the persistence transaction and
must not consume materials twice.

Secure families `10` (Enhance), `11` (Add), and `12` (Delete) place this
transaction behind the permanent PostgreSQL command inbox. The originating
NPC/dialog, operation, and exact role-ordered item snapshots form the canonical
request. Audit, inbox receipt, all three item mutations, one inventory revision,
three immutable ledger entries, and one strict outbox event commit atomically.

Planner rejections persist a replayable receipt without changing inventory,
revision, ledger, or outbox state. Exact retries return that receipt; a reused
UUID with different request content conflicts. A database error or uncertain
commit emits neither a stock terminal result nor authenticated `0x0102`, so the
native UUID remains recoverable.

The server resolves the permanent inbox before building a new canonical
request from current items. A reconnect can therefore replay a committed
receipt even if login has already loaded the post-mutation gear and material
stacks; those newer snapshots are never misreported as a request conflict.

After commit, the server sends the stock result on the receipt's original
dialog, any required deletion acknowledgements, a complete authoritative bag
refresh, and the family-specific authenticated command result last. Tokenless
clients retain the older compatibility transaction and do not gain durable
cross-reconnect idempotency.

The application, persistence, native identity, verification, and rollback
evidence for this increment is in
[B09 secure native Gear Enhancement](data-architecture-b09-native-gear-enhancement-20260730.md).
