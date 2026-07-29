# Secure legacy command results

## Scope

`LegacyCommandResult` is the authenticated, bounded server-to-client outcome
for a valuable legacy command carrying a `LegacyCommandOperation` ID. It is
frame type `0x0102`, is accepted only on a bound game TLS channel, and has an
exact 32-byte payload.

This is a terminal result, not a receipt for parsed bytes and not a claim that
an ordinary stock-client response is an acknowledgement. `Applied` and
`Replayed` prove a durable inbox outcome. `Rejected` may also describe a
deterministic pre-mutation routing or validation failure when the server can
prove no authoritative mutation started. A legacy response alone never
settles a pending client operation.

## Wire format

All integer fields and UUID bytes use network order.

| Offset | Size | Field | Rule |
| ---: | ---: | --- | --- |
| `0` | `1` | Format version | exactly `1` |
| `1` | `1` | Disposition | finite value below |
| `2` | `2` | Command family | nonzero |
| `4` | `4` | Result code | family-owned finite result |
| `8` | `8` | Inventory revision | revision at the recorded outcome |
| `16` | `16` | Client operation ID | nonzero canonical UUID bytes |

Disposition values are:

- `1=Applied`: the command committed for the first time;
- `2=Replayed`: the durable inbox returned a previously stored outcome;
- `3=Rejected`: a stored business rejection or proven pre-mutation terminal
  rejection;
- `4=Conflict`: the operation ID was already bound to different canonical
  request content.

An applied result must have a nonzero inventory revision. Revision zero is
permitted when no inventory revision exists for a terminal rejection,
conflict, or replayed stored rejection. A nonzero revision may still accompany
those outcomes when the authoritative transaction recorded one.

Unknown versions, dispositions, zero command families, zero UUIDs, non-exact
payload sizes, wrong endpoint roles, and wrong directions fail closed.

## Emission ordering

The gameplay caller may emit this result only when all applicable conditions
are true:

1. Any command that entered its authoritative executor has committed the
   terminal PostgreSQL inbox outcome and associated inventory, ledger, audit,
   and outbox changes.
2. A rejection without an inbox is limited to a deterministic pre-dispatch or
   validation path where no authoritative mutation could have started.
3. Every preceding stock-client legacy response and bag refresh for that
   outcome has completed its physical reliable write.
4. The result is written afterward through the same serialized TLS outer-frame
   write gate.

The caller must not emit a command result for a transient database error,
cancelled or unknown transaction outcome, queue failure, or any response that
may still be retried internally. Disconnecting without a terminal result leaves
the client operation pending so the same operation ID can be retried after
reconnect.

The result does not replace the stock-client response. It settles operation
identity for the native shim while the legacy response and refresh continue to
drive the original UI.

## Bounded behavior

The payload is fixed at 32 bytes and is encoded into a temporary bounded
buffer. `ClientSession.SendLegacyCommandResultAsync` rejects raw legacy
transports explicitly. The TLS mux accepts it only after bound game
authentication and serializes it with legacy and secure control writes using
the existing single outbound write gate.

The wired families and stable stock-client result codes are:

| Family | Command | Success | Rejections |
| ---: | --- | ---: | --- |
| `6` | Gear Mentor Make Attribute Stone | `1017` | `1016`, `1022`, `1020`, `1002` |
| `7` | Gear Mentor Transform Crystal | `1823` | `1822`, `1020` |
| `8` | Gear Mentor Combine Gem Pieces | `304` | `301`, `302`, `303` |
| `9` | Gear Mentor Decompose Gear | `1005` | `1024`, `1015`, `1003`, `1014`, `1004`, `1032`, `1020`, `1002`, `1019` |

Every additional family must define stable finite result codes, keep retry
identity isolated from other families, and demonstrate durable inbox replay
semantics before wiring a terminal result.
