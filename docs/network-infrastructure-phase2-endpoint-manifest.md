# Phase 2 signed endpoint manifest

## Status and ownership

- Format version: `1.0`
- Specification revision: `1.1`
- Last updated: `2026-07-25`
- Runtime status: Slice 6 parser, verifier, and one-shot loader are
  implemented and tested; no production key material, registry provider,
  installer, or live route activation is enabled
- Parent protocol:
  [`network-infrastructure-phase2-protocol.md`](network-infrastructure-phase2-protocol.md)

This is the normative bounded format, signature, rollback-protection, loader,
and key-rotation contract for `RebornNetwork.gwem`.

All multibyte integers inherit the parent protocol's unsigned big-endian
network byte order.

## Slice 6 implementation checkpoint

The native x86 candidate now contains:

- a bounded version 1 parser in
  `client/network-shim/src/EndpointManifest.cpp`;
- ECDSA P-256/SHA-256 IEEE P1363 verification through Windows CNG in
  `client/network-shim/src/EndpointManifestCrypto.cpp`;
- an injected current/next public-key lookup, compiled and installed sequence
  floors, UTC clock, and expected environment;
- a one-shot, module-relative loader in
  `client/network-shim/src/EndpointManifestLoader.cpp` that opens without
  write sharing, rejects a manifest-file reparse point or a final parent
  outside the resolved module directory, reads once into a fixed 4096-byte
  buffer, and publishes only after complete validation;
- focused golden-vector, boundary, truncation, signature, environment,
  rollback, validity, DNS, audience, server-ID, active-writer, no-hot-reload,
  oversized-file, tamper, and reparse checks under
  `client/network-shim/tests/EndpointManifest*`.

The candidate tests use an ephemeral test-only P-256 private key. No private
key or development credential is shipped.

## Binary format

The manifest filename is module-relative `RebornNetwork.gwem`. It is at most
4096 bytes. The 72-byte header is:

| Offset | Size | Field | Version 1 rule |
| ---: | ---: | --- | --- |
| `0` | `4` | Magic | ASCII `GWEM` |
| `4` | `4` | Total bytes | `146..3258` |
| `8` | `2` | Header bytes | `72` |
| `10` | `2` | Format major | `1` |
| `12` | `2` | Format minor | `0` |
| `14` | `1` | Environment | `1=dev`, `2=staging`, `3=production` |
| `15` | `1` | Flags | Bit 0 is dev-only legacy passthrough |
| `16` | `2` | Signature algorithm | `1=ECDSA-P256-SHA256-P1363` |
| `18` | `2` | Public-key ID | Embedded current or next key |
| `20` | `4` | Reserved | Zero |
| `24` | `8` | Manifest sequence | Nonzero, monotonically increasing |
| `32` | `8` | Not-before | Unix seconds |
| `40` | `8` | Not-after | Unix seconds |
| `48` | `2` | Minimum protocol major | `1` |
| `50` | `2` | Minimum protocol minor | `0` initially |
| `52` | `2` | Logical login port | Nonzero |
| `54` | `2` | TLS login port | Nonzero |
| `56` | `2` | Logical-host bytes | `1..253` |
| `58` | `2` | TLS-host bytes | `1..253` |
| `60` | `1` | Game-suffix count | `1..8` |
| `61` | `1` | Audience count | `1..8` |
| `62` | `1` | Server-ID count | `1..16` |
| `63` | `1` | Reserved | Zero |
| `64` | `4` | Signed bytes | `total - 64` |
| `68` | `4` | Reserved | Zero |

The body immediately follows:

1. Exact logical-login host bytes from the header length.
2. Exact TLS-login DNS host bytes from the header length.
3. Each game DNS suffix as one length byte (`1..253`) then bytes.
4. Each audience as one length byte (`1..64`) then bytes.
5. Each permitted nonzero, unique server ID as a four-byte integer.

Hosts and suffixes are canonical lower-case ASCII DNS names without a trailing
dot; the logical host may instead be canonical dotted-decimal IPv4. Audiences
match `[A-Za-z0-9._-]`. Duplicates, NULs, empty labels, unknown flags, and
trailing body bytes are rejected. Production rejects the legacy-passthrough
flag and a manifest for any other environment.

Each suffix entry represents both its apex and its subdomains. A grant TLS host
matches only when it is byte-for-byte equal to the suffix or ends with
`"." + suffix`; raw `EndsWith(suffix)` is forbidden. Thus `game.example.com`
and `example.com` match `example.com`, while `evil-example.com` does not.

## Signature and validity

The final 64 bytes are the IEEE P1363 `r || s` ECDSA signature. SHA-256 and
signature verification cover bytes `0..SignedBytes-1` exactly; DER signatures
are not accepted. `SignedBytes + 64` must equal `TotalBytes`. The validity
interval must contain current UTC, have `not-after > not-before`, and be no
longer than 31 days.

## Rollback protection and key rotation

The shim embeds current and next verification public keys plus a minimum
sequence per environment. The guarded installer maintains the highest accepted
sequence in an administrators/SYSTEM-write, users-read registry value under
`HKLM\Software\Reborn\NetworkManifest`; missing or corrupt state fails closed
in `SecureRequired`. A manifest sequence below either compiled or installed
minimum is rejected. Key rotation first ships a shim that trusts current and
next IDs, then signs a higher-sequence manifest with the next key, and a later
shim removes the old key and advances its compiled minimum.

## Loader and installer

The loader opens the module-relative file without write sharing, rejects a
reparse point or a final path outside the module directory, reads once into a
fixed 4096-byte buffer, strictly parses, verifies the signature, and only then
copies endpoints into runtime state. It never hot-reloads.

The installer verifies a candidate with the same parser, atomically
writes and flushes the higher registry minimum first, then atomically replaces
and flushes the manifest from the same directory. An interruption between
those steps fails closed and is repaired by installer recovery; it cannot
reactivate the lower sequence.

## Deliberately unconfigured production inputs

Slice 6 does not invent security material that the project does not yet own:

- production current/next public keys and their key IDs;
- per-environment compiled sequence floors;
- the hardened HKLM highest-sequence reader and installer/writer;
- a signed production or staging `RebornNetwork.gwem`;
- live non-pass-through route-policy activation.

Those inputs remain required before `SecureRequired` can be enabled. Missing
or corrupt installed floor state must fail closed in that mode, as specified
above. The candidate is not installed into the original game client and does
not hot-reload a changed manifest.
