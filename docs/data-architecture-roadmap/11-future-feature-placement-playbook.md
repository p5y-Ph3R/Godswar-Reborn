# 11. Future-feature placement playbook

## 11.1 Decision process

Every new feature must answer, in order:

1. Must the data survive a complete service restart?
2. Would losing it affect player ownership, progress, value, or trust?
3. Must it update atomically with other player data?
4. Does it require relational constraints?
5. Is it temporary or safely reconstructable?
6. Does it require TTL behavior?
7. Does it coordinate multiple server instances?
8. Is it accessed frequently enough to require caching?
9. Is it a genuinely independent, deeply nested document?
10. Does its schema vary substantially between documents?
11. Would PostgreSQL JSONB satisfy the same requirement?
12. Is it telemetry, analytics, search, or object data that belongs on another platform?

Default:

- Valuable, authoritative, durable, or transactional -> PostgreSQL.
- Temporary, reconstructable, cached, TTL-based, or coordination state -> Redis, but only after Redis is operationally justified.
- Proven document-oriented requirement not handled well by PostgreSQL/JSONB -> MongoDB after an approved ADR.
- Analytics, high-volume telemetry, search, large files, and event archives -> evaluate a purpose-built platform rather than forcing them into these databases.

## 11.2 Required feature data-design record

Before implementation, add a short ADR/data record containing:

- feature name and responsible module;
- data categories and example fields;
- durable, runtime-only, derived, replicated, and configuration state;
- exactly one authoritative owner per field;
- caches/projections and their reconstruction;
- aggregate and transaction boundaries;
- expected read/write volume;
- consistency requirements;
- retention/TTL;
- failure/recovery;
- schema/versioning and rolling compatibility;
- security/abuse risks;
- migration and rollback;
- why MongoDB is or is not required.

## 11.3 Illustrative examples, not confirmed features

| Example feature | PostgreSQL | Redis after its gate | MongoDB | Other platform/notes |
| --- | --- | --- | --- | --- |
| Player housing | Ownership, plot permissions, placed-object rows or bounded JSONB layout revision | Active edit lease, visitor presence, short preview cache | Only if independently edited layouts become large/highly variable documents and JSONB evidence fails | Object storage for uploaded assets; no such feature exists |
| Pets | Templates, owned pets, growth/level/rebirth/skills, audit | Presence/summary cache only | Not justified | Existing dirty-tree foundation is relational |
| Mounts | Owned item/equipment rows, quality/grade/attributes | Appearance cache/presence only | Not justified | Currently represented as items |
| Crafting | Recipes/content, jobs/results, consumed items/currency, operation ledger | Temporary queue UI/projection | Not justified | Existing forging is the current example |
| Achievements | Definition/version, progress, unlock/reward claims | Leaderboard/summary projection | Not justified; JSONB for bounded criteria | Analytics platform for aggregate product analysis |
| Guild wars | Guild/member/role/war/result/reward durable rows | War-instance routing, invitations, live scoreboard | Not initially justified | Runtime battle state belongs to ECS; event analytics elsewhere |
| Player trading | Offers/escrow/transfers/audit in one PG transaction | Invitation TTL/presence only | Never for transactional core | High-risk feature; require inbox/outbox/ledger first |
| Auction house | Listings, escrow, bids/purchases, settlement/audit | Search/result cache and notifications | Not for ownership/settlement | A search engine may later index listings |
| Instanced dungeons | Durable admission/reward/lockout/checkpoint if required | Instance placement/routing/reconnect lease | Not justified | Runtime instance ECS is disposable |
| World events | Definition, durable phase/reward/control state | Temporary coordination/leaderboard projection | JSONB likely sufficient for definitions | Analytics/event archive if volume grows |
| Leaderboards | Authoritative scores/grants in PG or derived ledger | Sorted-set projection | Not justified | Rebuild and publish version/watermark |
| Friends/blocks | Directed relational edges and privacy settings | Presence/notification routing | Not justified | Strong relational uniqueness |
| Chat | Moderation/account settings if durable; optional recent-message durable store by policy | Short-lived delivery/presence | Only with a proven document archive use case, unlikely | Dedicated chat/log archive/search may be more appropriate |
| Seasonal progression | Season definitions, character progress, claims, ledger | Ranking/cache/expiry projection | Not justified | Partition/archive by season if measured |
| Player-generated content | Ownership, publication state, moderation, version metadata | Edit lease/cache | Possible only after document-shape/access evidence and an ADR | Object storage for assets; search/moderation platforms likely |
| Cosmetic collections | Entitlement/ownership rows, equipped selection | Summary cache | Not justified | Treat as player value |
| Daily/weekly challenges | Definitions, period key, progress, claims | Timer/display cache | Not justified | PG uniqueness prevents double claim |
