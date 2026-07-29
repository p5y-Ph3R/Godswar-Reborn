# Application boundary

Application code owns game use cases and feature-specific persistence
contracts. New features should be grouped by capability:

```text
Application/
  World/
    IWorldContentReader.cs
  Characters/
    ICharacterSnapshotReader.cs
  Inventory/
    Commands/
    IInventoryTransactions.cs
```

Contracts must describe intent and transaction semantics. Do not add a
universal repository, empty marker interface, provider client, packet DTO,
socket, or service-locator dependency here.

Allowed dependency direction:

```text
Networking/Game -> Application -> Domain/ECS
Infrastructure ---------------> Application + Domain
```

The first real contract will be added with B05. B02 deliberately establishes
the boundary and its automated gate without changing runtime behavior.
