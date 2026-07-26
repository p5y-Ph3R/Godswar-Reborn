# Phase 4: first authoritative hybrid movement slice

## Status and safety boundary

Phase 4 moves only the meaning of the original 20-byte opcode `10194`
movement sample onto the secure realtime path. It does not install the client
shim, enable checked-in TLS or UDP settings, change Windows trust, alter the
host firewall, or modify a live network interface.

The original client still renders its local actor and other players through
the legacy protocol:

- The shim consumes UDP position snapshots for acknowledgement and
  reconciliation state.
- A rejected movement receives a reliable legacy `10194` correction over the
  existing TLS bridge so the stock client can render it safely.
- Other players continue to receive canonical 20-byte legacy movement over
  TLS.

Injecting UDP plaintext into the stock client's proprietary encrypted stream
is forbidden. Packet loss would desynchronise that stream.

Source/offline completion was verified on `2026-07-26`:

- managed Release build: zero warnings/errors;
- focused Phase 4 checks: `5/5`;
- full managed protocol checks: `126/126`;
- native Win32 Release: `/W4 /WX`, two identical clean builds;
- native wrapper: five consecutive offline passes;
- candidate shim SHA-256:
  `1069EC944B64DE7AD3DFBBB07C9D2E42E9840173682F1F145F0AFD371D45F6A2`.

These are local compatibility and security results, not controlled-host or
production-capacity acceptance. The candidate was not installed and every
checked-in secure/gameplay option remains disabled.

## Evidence-based local defaults

The starting policy is based on 12,666 captured client-to-server movement
samples from nine local logs:

| Measurement | Observed value |
| --- | ---: |
| Median sample cadence | 82.30 ms |
| 99th-percentile cadence | 98.95 ms |
| 99th-percentile position step | 0.44643 units |
| 99th-percentile speed | 6.88 units/s |

The conservative development policy is therefore:

- 20 Hz fixed processing tick (50 ms);
- changed-position snapshots at no more than 10 Hz;
- a forced full keyframe at least once per second;
- at most one newest pending input and one newest pending snapshot per secure
  session;
- 8 units/s base movement ceiling multiplied only by the server-owned runtime
  movement multiplier;
- 0.75 units of scheduling/quantisation tolerance;
- at most one second of server-measured movement credit;
- 20 ms minimum accepted input cadence.

These are local compatibility defaults, not production capacity guarantees.
The original client supplies absolute position samples rather than genuine
keyboard/control inputs. Collision and navigation authority are not yet
available. Deterministic control-input replay and authoritative wall
collision remain Phase 5 work.

## Capability and cutover

The authenticated 72-byte UDP grant uses its formerly reserved big-endian
word at offset 10 as a capability mask. Bit `0x0001` means authoritative
movement is supported. Unknown capability bits are rejected.

The shim suppresses a stock movement send only when all of the following are
true:

1. the secure game connection supplied the authenticated capability;
2. UDP address validation and protected-session binding completed;
3. an authenticated keyframe established the current map/world baseline.

Before that cutover, the exact original packet continues through the existing
legacy TLS path. A network that blocks UDP before the first authenticated
baseline therefore retains byte-identical compatibility inside TLS without
claiming the authoritative cutover. If UDP becomes unavailable after cutover,
the worker changes transport epoch once and sends movement through the
dedicated TLS realtime frame. It does not silently return to an untagged
legacy movement packet.
Once that TLS frame is written, it is not placed under another gameplay-ACK
deadline: TLS already supplies ordered reliable delivery. An authoritative
rejection is rendered through the separate reliable legacy `10194` correction
described above.

Checked-in `Secure.Enabled`, `Secure.Udp.Enabled`, and
`Secure.Udp.GameplayMovementEnabled` remain false until a controlled-host
activation is approved.

The environment override is
`GODSWAR_SECURE_UDP_GAMEPLAY_MOVEMENT_ENABLED`. Validation rejects it unless
both the secure TLS profile and UDP runtime are also enabled, and rejects a
configured datagram budget too small for the protected 64-byte snapshot.

## Shared movement envelope

UDP protected message type `4` and TLS frame type `0x0300` carry the same
52-byte, big-endian payload.

| Offset | Bytes | Field | Rule |
| ---: | ---: | --- | --- |
| 0 | 1 | Version | exactly `1` |
| 1 | 1 | Flags | bit 0 is `CurrentWorld`, TLS only |
| 2 | 2 | Payload length | exactly `52` |
| 4 | 4 | Transport epoch | nonzero |
| 8 | 8 | Global input ID | nonzero and monotonic |
| 16 | 8 | Client monotonic ms | nonzero; telemetry, never movement credit |
| 24 | 4 | World generation | exact baseline for UDP |
| 28 | 4 | Legacy movement state | opaque high bits preserved |
| 32 | 4 | X | finite IEEE-754 binary32 |
| 36 | 4 | Z | finite IEEE-754 binary32 |
| 40 | 4 | Auxiliary/heading | finite IEEE-754 binary32 |
| 44 | 1 | Map ID | exact baseline for UDP |
| 45 | 3 | Reserved | zero |
| 48 | 2 | Legacy opcode | exactly `10194` |
| 50 | 2 | Legacy length | exactly `20` |

`CurrentWorld` is valid only on the ordered authenticated TLS fallback. It
instructs the server to bind the sample to the handler's current map and world
generation. UDP must carry the exact keyframe baseline and cannot initiate a
map change.

The client monotonic timestamp is useful for diagnostics only. Cadence and
distance allowance use server time, preventing a client from minting movement
credit.

## Position snapshot

Protected UDP message type `5` carries a fixed 64-byte, big-endian
authoritative snapshot.

| Offset | Bytes | Field |
| ---: | ---: | --- |
| 0 | 1 | Version (`1`) |
| 1 | 1 | Flags: bit 0 `Keyframe`, bit 1 `Correction` |
| 2 | 2 | Payload length (`64`) |
| 4 | 4 | Current transport epoch |
| 8 | 8 | Highest processed global input ID |
| 16 | 8 | Server simulation tick |
| 24 | 8 | Authoritative position revision |
| 32 | 8 | Snapshot sequence |
| 40 | 4 | World generation |
| 44 | 4 | Legacy movement state |
| 48 | 4 | Authoritative X |
| 52 | 4 | Authoritative Z |
| 56 | 4 | Authoritative auxiliary/heading |
| 60 | 1 | Map ID |
| 61 | 1 | Rejection reason |
| 62 | 2 | Reserved zero |

The first baseline is a keyframe with input acknowledgement zero and rejection
reason `None`. A nonzero rejection reason requires `Correction`. A keyframe
and correction may coexist.

Rejection values are:

| Value | Meaning |
| ---: | --- |
| 0 | None |
| 1 | Malformed |
| 2 | Not ready |
| 3 | Dead |
| 4 | Invalid coordinates |
| 5 | Map transition |
| 6 | Cadence |
| 7 | Speed |
| 8 | Distance |
| 9 | Stale input |
| 10 | Transport epoch |
| 11 | Transport source |
| 12 | Overloaded |

Unknown flags and rejection values are rejected by both managed and native
decoders.

## Cross-transport ownership and deduplication

The protected packet sequence and key epoch secure an individual UDP
datagram. They are deliberately separate from the gameplay transport epoch
and global input ID.

Per secure connection:

1. The first realtime source owns transport epoch 1.
2. The same epoch must arrive from the same inferred transport.
3. A source switch must use exactly the next epoch.
4. Old epochs and epoch jumps are rejected.
5. Input IDs remain globally increasing across transport epochs.
6. An input ID is projected at most once.
7. A repeated input may establish a valid newer transport epoch, but it cannot
   reapply movement.
8. The only Phase 4 source transition is UDP to TLS; TLS-to-UDP switchback is
   rejected.
9. Map and revive/world re-entry preserve transport epoch, global
   acknowledgement, position revision, and simulation sequence while advancing
   world generation.

Thus a delayed UDP copy cannot overwrite state after TLS fallback, while a TLS
retry of a UDP input can safely preserve the same logical input ID. A processed
old-world input is acknowledged as rejected, preventing both replay and an
unnecessary client fallback timer.

## Server ownership

The UDP receive loop performs only bounded framing, address/session checks,
AEAD validation, application-envelope validation, rate limiting, and a
capacity-one mailbox offer. It never mutates a character, performs AOI work,
or calls persistence.

The game handler owns the fixed-step movement projection:

1. take at most the newest pending sample;
2. validate readiness, life state, map/world generation, cadence, finite
   coordinates, hard distance, and elapsed-time speed;
3. apply an accepted position and advance the authoritative revision;
4. publish a latest-only UDP snapshot;
5. broadcast a canonical 20-byte movement packet to other stock clients;
6. queue persistence outside the simulation decision;
7. send a latest correction snapshot and reliable stock-client correction for
   a rejected sample.

Snapshot egress resolves the currently authenticated bound endpoint at send
time. NAT rebinding therefore cannot leave a queued snapshot targeting a
stale endpoint.

## Overload and security properties

- Movement and snapshot mailboxes are fixed at one replace-stale entry per
  session.
- External decoders have exact lengths and bounded field validation.
- No UDP datagram exceeds the existing 1,200-byte path-MTU ceiling.
- UDP source IP/port alone never identifies a player.
- AEAD, replay windows, key rotation, address validation, and session tickets
  remain the Phase 3 security boundary.
- Raw legacy movement remains available only before the negotiated
  authoritative cutover.
- Invalid movement cannot mutate character state, broadcast, or persist.
- Metrics and logs use bounded outcome labels and never include packet
  payloads, tickets, cookies, or keys.

The default global protected-candidate allowance is a development setting and
is not a scale claim. At 20 packets/s it requires measurement and deliberate
capacity configuration before supporting many concurrent moving players.

## Verification and acceptance

Source completion requires:

- managed and native golden vectors and round trips;
- malformed length, reserved-bit, non-finite, and unknown-enum rejection;
- transport switch, old epoch, epoch jump, source mismatch, and duplicate
  delivery checks;
- capacity-one replacement and shutdown-race checks;
- exact speed/distance boundaries and server-owned mount multipliers;
- deterministic latency, jitter, burst loss, duplication, reordering, and UDP
  blocking profiles;
- byte-identical stock pass-through before cutover and canonical 20-byte
  viewer projection after cutover;
- managed Release build/tests and native x86 Release `/W4 /WX` tests.

All source/offline requirements above pass, including deterministic seeded
latency, jitter, burst loss, duplication, reordering, UDP blocking, mailbox
overload, transition retry schedules, handler projection/correction, and
map/revive rehydration.

Offline and loopback verification cannot accept the final stock-client gate.
Controlled-host acceptance still requires five alternating account 7/13
login/world cycles, mounted and unmounted movement, a map transition, UDP
block/unblock fallback, correct viewer movement/correction, and a ten-minute
movement soak without a blank model, crash, or server-unavailable regression.

One production-readiness gap is explicit: AOI visibility refresh currently
awaits bounded reliable remove/spawn writes while holding the character-state
gate. Transport deadlines prevent an indefinite write, but a slow client can
delay that session's fixed-step loop. Phase 5 must preserve remove/spawn commit
ordering while moving this work behind a bounded single-owner effects queue.
