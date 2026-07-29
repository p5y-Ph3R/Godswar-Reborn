# Application boundary

Application code owns game use cases and feature-specific persistence
contracts. New features should be grouped by capability:

```text
Application/
  Commands/
    CommandEnvelope.cs
  Messaging/
    OutboxEventMessage.cs
    IOutboxEventConsumer.cs
    OutboxOrdering.cs
  World/
    IWorldContentReader.cs
  Characters/
    ICharacterSnapshotReader.cs
  Talents/
    TalentUpgradeCommandEnvelope.cs
    ITalentUpgradeCommandExecutor.cs
    TalentUpgradeExecutionResult.cs
  Inventory/
    DeveloperItemGrantCommandEnvelope.cs
    DeveloperBagClearCommandEnvelope.cs
    IDeveloperItemGrantCommandExecutor.cs
    IDeveloperBagClearCommandExecutor.cs
```

Contracts must describe intent and transaction semantics. Do not add a
universal repository, empty marker interface, provider client, packet DTO,
socket, or service-locator dependency here.

Allowed dependency direction:

```text
Networking/Game -> Application -> Domain/ECS
Infrastructure ---------------> Application + Domain
```

B05 added the first real contract: `IWorldContentReader`. The composition root
loads one revision-pinned world catalog before opening listeners; gameplay
reads that application contract instead of the broad store. PostgreSQL loading
lives under `Infrastructure/WorldContent`, while pure spawn definitions live
under `Domain/World/Content`.

B07 adds the first transport-neutral valuable-command envelope. Operation
identity and canonical request hashing live under `Application/Commands`;
the bounded process-local attempt correlation is explicitly non-authoritative;
and the talent-specific intent lives under `Application/Talents`. Legacy packet
decoding remains in `Game`, authenticated account and character IDs are
supplied by the server session, and persistence remains behind the existing
compatibility store until B08 introduces the PostgreSQL inbox/outbox
transaction.

B08 application contracts keep that transaction provider-neutral.
`ITalentUpgradeCommandExecutor` returns a bounded disposition and the same
canonical durable receipt for a new commit or an exact duplicate.
`OutboxEventMessage` is a bounded, consumer-targeted immutable event envelope,
while
`OutboxOrderingRules` distinguishes stale, deliverable, and strict-sequence
gap events without owning a database checkpoint. Concrete transaction,
polling, retry, and checkpoint implementations belong under `Infrastructure`;
consumers must tolerate at-least-once delivery.

B09 applies the same boundary to explicit developer inventory operations.
Tokenized grants and bag clearing have separate intent/result contracts,
canonical receipts, permanent terminal-precondition results, and PostgreSQL
executors. The original tokenless paths remain compatibility-only; native
inventory and crafting commands do not gain a durable retry guarantee until
the secure shim preserves one operation ID through acknowledgement uncertainty
and reconnect.
