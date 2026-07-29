# Application boundary

Application code owns game use cases and feature-specific persistence
contracts. New features should be grouped by capability:

```text
Application/
  Commands/
    CommandEnvelope.cs
  World/
    IWorldContentReader.cs
  Characters/
    ICharacterSnapshotReader.cs
  Talents/
    TalentUpgradeCommandEnvelope.cs
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
