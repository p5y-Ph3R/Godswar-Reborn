# 9. UDP and TCP integration

## 9.1 Transport semantics

Use TLS-protected TCP for:

- authentication and one-time session/game binding;
- character list/create/delete;
- inventory, equipment, wallet, forging, pets, skills/talents/zodiac;
- purchases, trades, auction, mail, guild/social changes when implemented;
- configuration/control, reliable chat, map/zone handoff, keyframes and UDP fallback.

Use authenticated encrypted UDP for:

- high-frequency player movement input;
- authoritative local movement snapshots/deltas;
- later, only measured transient events for which newer state supersedes older state.

Do not move valuable commands to UDP. Do not rebuild general reliable ordered transport over UDP. If a future time-sensitive event needs selective reliability, give it an event ID, acknowledgement, bounded retry count, expiry, and a narrow logical channel.

The current implementation already models TCP/UDP independence with transport epoch, logical input ID, world generation, tick, snapshot sequence, replay window, and keyframes. Preserve those rules.

| Message family | Transport | Reliability/ordering | Loss, duplicate, and replay policy |
| --- | --- | --- | --- |
| Authentication, ticket issue/consume, character control | TLS/TCP | Reliable and ordered within the control stream | Disconnect/retry with a new or still-valid one-time flow; business operation ID where state changes |
| Inventory, currency, forge, pet, progression, future trade/auction | TLS/TCP | Reliable stream delivery, but delivery is not proof of execution | Durable inbox deduplicates retries; committed result is returned after reconnect |
| Chat and notifications | TLS/TCP initially | Ordered only within the relevant logical channel | Deduplicate message/event ID if persisted/rerouted; slow recipients do not block gameplay |
| Player movement input | Authenticated UDP, one-way fallback to TLS | Sequenced, not reliable; newer input supersedes stale input | Drop stale/duplicate/replayed inputs; optional bounded recent-input resend only if measurements require it |
| Authoritative movement snapshots/deltas | Authenticated UDP | Unreliable-sequenced | Discard stale snapshots; periodic full keyframes recover baseline loss |
| Corrections, map changes, inventory/economy results | TLS/TCP | Reliable and ordered per control stream | Version/world generation prevents applying a delayed result to a newer world |
| Future critical transient event | UDP only after evidence | Selectively reliable inside one narrow event channel | Event ID, ACK, bounded retry/expiry, dedupe; otherwise keep it on TLS |

## 9.2 Authentication and association

Target flow:

1. TLS verifies the server and protects credential exchange.
2. `AccountAuthenticationService` verifies the normalized account credential through an account application contract.
3. The login service issues an opaque, short-lived, audience/server/protocol-scoped ticket.
4. The game TLS endpoint atomically consumes it and establishes `SecureBoundGamePrincipal`.
5. The game session registers a UDP binding offer.
6. UDP performs stateless address-cookie validation before allocating meaningful state, then authenticates proof derived from the TLS session.
7. The server creates transport keys/replay windows and associates them with the authenticated connection ID, never only IP/port.
8. NAT rebinding is authenticated and rate limited.

The raw username-only game bind and `LoginOrCreateAccountAsync` compatibility behavior must be development-only or removed before production.

## 9.3 Command envelope

Every decoded command should carry:

- protocol and message version;
- authenticated account and character IDs supplied by the session, not the payload;
- connection/session generation and server ownership fence;
- command/message type;
- logical command/input ID and transport epoch;
- client-observed world generation/tick where relevant;
- server receive timestamp;
- bounded payload;
- idempotency scope and expected aggregate version for valuable commands.

Packet sequence/replay acceptance is transport security. Command idempotency is business correctness. Both are required.

## 9.4 Processing modes

| Command type | Synchronous steps | Asynchronous steps | Ack rule |
| --- | --- | --- | --- |
| Movement input | Validate auth/sequence/world generation; enqueue to map owner; ECS accepts/rejects intent | Replicate sequenced snapshots; coalesced position checkpoint | No reliable business ack; snapshot/correction conveys result |
| Basic attack/skill intent | Validate session/cooldown/target generation; ECS resolves combat | Replicate visual/damage; durable reward command on kill | Combat result may replicate from ECS; reward/value success only after PG commit |
| Inventory/equipment/forge/pet command | Validate; durable inbox check; PG transaction; project committed result | Outbox dispatch/cache invalidation/telemetry | Success only after commit; duplicate returns stored result |
| Character create/delete | Validate; PG lifecycle transaction + audit/inbox/outbox | Refresh login summary | Success only after commit; deletion should use tombstone workflow |
| Map transfer | Validate target; persist transfer/safe checkpoint; switch local ownership/world generation | Destination bootstrap/keyframe | Reliable acknowledgement after destination is committed/owned |
| Chat | Validate/rate-limit; route reliably; persist only if product requires history/moderation | Fanout/moderation archive | Delivery acceptance, not guarantee all recipients saw it |

## 9.5 Valuable operation sequence

For item acquisition/consumption, currency changes, purchases, trades, auction operations, rewards, character creation, and character deletion:

1. **Validate command:** synchronous; strict decoder, authentication, authorization, rate/cost limits, session ownership, server-derived item/cost/reward.
2. **Update ECS state:** for player value, normally after PG commit. A short-lived in-memory reservation may prevent concurrent local attempts but is never the committed result. Runtime combat may update ECS first, but the reward remains pending.
3. **Commit durable state:** synchronous before success; one PG transaction includes authoritative rows, operation inbox, audit/ledger, and outbox.
4. **Update/invalidate Redis:** asynchronous after commit through outbox; omitted entirely in the initial PG-only architecture.
5. **Publish events:** outbox-driven at least once for external/projection work; immediate local immutable event may update ECS only from the committed result.
6. **Acknowledge:** success after commit. If the connection drops after commit, retrying the same operation ID returns the original result.

If the PG deadline is exceeded, do not guess whether a write committed. Requery the inbox by operation ID before retrying or reporting a terminal result.

## 9.6 Backpressure and failure behavior

- Decode/validate at ingress, then offer to a bounded per-session/per-map queue.
- Runtime input can replace stale input or be rejected cheaply according to its semantics.
- Valuable commands cannot be silently dropped; return busy/retry only before execution starts.
- Database workers use bounded concurrency and explicit deadlines; they never execute on the fixed-step map loop.
- Replication uses per-map/per-recipient budgets and fairness. `BroadcastToMapAsync` should no longer await every recipient serially.
- Slow clients lose noncritical snapshots first, then are disconnected if reliable queues remain saturated.
- PG outage: reject new valuable mutations, keep bounded runtime play only where product accepts unsaved checkpoint risk, expose not-ready/degraded, and never acknowledge value.
- Shutdown follows section 5.6. Crash recovery uses inbox/outbox/fences, not TCP delivery assumptions.
