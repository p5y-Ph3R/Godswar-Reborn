# 15. Security and abuse prevention

## 15.1 Trust boundaries

```text
Untrusted client and Internet
        |
        | hostile bytes, credentials, replay, timing
        v
Protected L3/L4 edge (future provider) ---- administration/metrics remain private
        |
        v
TLS/UDP transport and bounded admission
        |
        v
Authenticated session + ownership + command validation
        |
        v
Authoritative application/ECS boundary
        |
        v
PostgreSQL trust boundary ---- optional Redis contains no player value
        |
        v
Backups/audit/operations boundary
```

The client remains untrusted after TLS. It may request an action; it may not provide authoritative position, damage, reward, item, price, balance, ownership, cooldown, or transaction outcome.

## 15.2 Priority findings and controls

| Risk | Current evidence | Required control |
| --- | --- | --- |
| Raw authentication/account takeover | Raw login calls `LoginOrCreateAccountAsync`; raw game bind accepts username; secure path is optional | Require TLS/hardened authentication and one-time game binding in production; disable registration/plaintext migration by policy; retire raw path |
| Session hijack/duplicate ownership | Process-local registry; no cross-process fence | TLS-bound principal, opaque tickets, session generation, optional Redis lease after scale-out, PG fencing |
| Packet replay/forgery | Secure UDP replay protection exists; valuable TCP commands lack durable IDs | Preserve AEAD/replay window; add business operation IDs/inbox and authorization |
| Inventory/currency duplication | Transactions exist but retries can re-execute; audit incomplete | Inbox, versions, ledger/audit, constraints, commit-before-ack, reconciliation |
| Race conditions | Mutable `GameCharacter`, multiple locks, process-local semaphores | Single-owner mailboxes, deterministic transaction lock order, optimistic versions/fences, concurrency tests |
| Trade/auction abuse | Features missing | Before implementation require escrow, deterministic locks, inbox/outbox, server prices/fees, audit, rate limits |
| Authorization | Handler-specific checks | Central session principal and command policy; scope every query/mutation to authenticated account/character |
| SQL injection | Current Npgsql paths generally parameterized | Keep parameterized SQL; no dynamic identifiers from clients; code review/analyzers |
| Redis key manipulation | Redis absent | Typed opaque key builder; no raw username/IP; Lua validates versions/tokens |
| Sensitive data exposure | Hundreds of `Console.WriteLine` sites; packet hex/endpoint/name logging | Structured redacted sampled logs; disable raw payload diagnostics in production; never log credentials/tickets/cookies/keys |
| Administrative abuse | Developer commands and grants exist | Disabled by default, strong operator authentication/authorization, allowlist not sufficient alone, same-transaction durable GM audit |
| Resource exhaustion | Good bounded network/KDF/UDP structures; DB/background work less bounded | Bound application/persistence queues, timeouts, pool budgets, log sampling, authenticated priority, readiness/load shedding |

## 15.3 Durable audits

Retain durable PG audit/ledger records for:

- credential, account status, role/permission, ban, and administrative changes;
- character create/delete/restore/purge;
- item acquisition, deletion, transfer, forge/enhance and GM grants;
- all currency deltas and balance corrections;
- pet ownership/progression/rebirth/merge once implemented;
- reward grants and claim IDs;
- future purchase/refund, trade, auction settlement, guild ownership/permission;
- ownership conflicts and security-sensitive recovery actions.

Audit records use internal opaque actor/operation IDs, bounded structured reason codes, before/after values needed for investigation, UTC timestamps, build/content versions, and tamper-evident access controls. Do not copy credentials, raw packets, tickets, session keys, or unnecessary PII.

## 15.4 Abuse controls

Apply layered limits by global service, message cost, connection/session, account, endpoint, and IP/prefix. Account for shared NAT: IP throttling is one signal, not automatic mass banning. Expensive KDF, DB, decompression, content lookup, and logging occur only after cheap bounds/auth checks. New unauthenticated work sheds before established authenticated sessions. Upstream L3/L4 protection remains necessary for public arbitrary TCP/UDP; application limits do not absorb volumetric traffic.
