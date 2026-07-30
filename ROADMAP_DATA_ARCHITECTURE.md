# Godswar Server Data Architecture Roadmap

Status: implementation-ready, modular planning document.

Repository originally assessed: `C:\Reborn` at Git HEAD `54f2d4b`, including
the preserved working tree on 2026-07-29.

The roadmap is split into linked modules to comply with `AGENTS.md`'s 20 KB
file limit. This file remains the stable entry point. Splitting changed only
document organization; the assessment and recommendations remain intact.

## Implementation evidence

- [B01A schema/build/backup inventory](docs/data-architecture-b01a-schema-build-backup-inventory-20260729.md)
- [B01B coherent schema release](docs/data-architecture-b01b-schema-release-20260729.md)
- [B02 data-boundary architecture ratchet](docs/data-architecture-b02-boundary-ratchet-20260729.md)
- [B03 mandatory disposable PostgreSQL CI](docs/data-architecture-b03-postgres-ci-20260729.md)
- [B04 fail-closed storage and security profiles](docs/data-architecture-b04-fail-closed-profiles-20260729.md)
- [B05 pinned world-content reader](docs/data-architecture-b05-world-content-reader-20260729.md)
- [B05B database-authoritative NPC content](docs/data-architecture-b05b-database-npc-authority-20260729.md)
- [B05C database-authoritative NPC dialogue](docs/data-architecture-b05c-database-npc-dialogue-authority-20260729.md)
- [B06 consistent character snapshot reader](docs/data-architecture-b06-character-snapshot-reader-20260729.md)
- [B07 legacy operation identity and command envelope](docs/data-architecture-b07-command-envelope-20260729.md)
- [B08 PostgreSQL command inbox/outbox foundation](docs/data-architecture-b08-command-inbox-outbox-20260729.md)
- [B09 economy ledger foundation and first durable inventory command](docs/data-architecture-b09-economy-ledger-increment-20260729.md)
- [B09 secure native Make Attribute Stone increment](docs/data-architecture-b09-native-make-attribute-stone-20260729.md)
- [B09 secure native Transform/Combine increment](docs/data-architecture-b09-native-material-conversions-20260729.md)
- [B09 secure native Gear Mentor Decompose increment](docs/data-architecture-b09-native-decompose-20260729.md)
- [B09 secure native Gear Enhancement increment](docs/data-architecture-b09-native-gear-enhancement-20260730.md)
- [B09 secure native Equipment Forge increment](docs/data-architecture-b09-native-equipment-forge-20260730.md)
- [B09 secure native kit-bag item-delete increment](docs/data-architecture-b09-native-kit-bag-delete-20260730.md)
- [B09 secure native kit-bag move/swap increment](docs/data-architecture-b09-native-kit-bag-move-20260730.md)
- [B09 secure native equipment/bag transfer increment](docs/data-architecture-b09-native-equipment-bag-transfer-20260730.md)
- [B09 secure native Holy Stone increment](docs/data-architecture-b09-native-holy-stone-20260730.md)
- [B09 durable Zodiac skill-grid activation increment](docs/data-architecture-b09-zodiac-grid-activation-20260730.md)

## Roadmap sections

1. [Executive summary](docs/data-architecture-roadmap/01-executive-summary.md)
2. [Current-state architecture](docs/data-architecture-roadmap/02-current-state-architecture.md)
3. [Target architecture](docs/data-architecture-roadmap/03-target-architecture.md)
4. [Data ownership matrix](docs/data-architecture-roadmap/04-data-ownership-matrix.md)
5. [ECS persistence strategy](docs/data-architecture-roadmap/05-ecs-persistence-strategy.md)
6. [PostgreSQL design](docs/data-architecture-roadmap/06-postgresql-design.md)
7. [Redis design](docs/data-architecture-roadmap/07-redis-design.md)
8. [MongoDB justification](docs/data-architecture-roadmap/08-mongodb-justification.md)
9. [UDP and TCP integration](docs/data-architecture-roadmap/09-udp-tcp-integration.md)
10. [Consistency and messaging strategy](docs/data-architecture-roadmap/10-consistency-messaging-strategy.md)
11. [Future-feature placement playbook](docs/data-architecture-roadmap/11-future-feature-placement-playbook.md)
12. [Extension conventions for new features](docs/data-architecture-roadmap/12-extension-conventions.md)
13. Migration strategy:
    - [Phases 0-8](docs/data-architecture-roadmap/13a-migration-strategy-phases-00-08.md)
    - [Phases 9-16](docs/data-architecture-roadmap/13b-migration-strategy-phases-09-16.md)
14. [Testing strategy](docs/data-architecture-roadmap/14-testing-strategy.md)
15. [Security and abuse prevention](docs/data-architecture-roadmap/15-security-abuse-prevention.md)
16. [Deployment and operations](docs/data-architecture-roadmap/16-deployment-operations.md)
17. [Risks, decisions, and unresolved questions](docs/data-architecture-roadmap/17-risks-decisions-questions.md)
18. [Implementation backlog](docs/data-architecture-roadmap/18-implementation-backlog.md)

## Evidence and status convention

- **Existing** means the symbol and behavior are present in the repository.
- **Partially implemented** means a real implementation exists but is
  incomplete, optional, development-only, or lacks an end-to-end production
  boundary.
- **Planned or inferred** means repository direction supports the idea, but
  no complete feature exists.
- **Missing** means no implementation was found.
- **Requires clarification** means a product, capacity, reliability, or
  operational decision materially affects the design.

The roadmap records the repository state at assessment time. Later completion
evidence belongs in the B-series reports linked above rather than silently
rewriting historical findings.
