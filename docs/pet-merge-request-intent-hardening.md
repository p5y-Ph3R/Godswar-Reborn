# Pet Merge request-intent hardening backlog

Status: deferred hardening; current server authority is intact.

## Current guarantee

The pet-to-pet Merge request contains only the main pet ID, deputy pet ID,
material ID, and material quantity. The authenticated character identity comes
from the server session. The durable executor locks both pets and the material
from that character, reloads all pet values from PostgreSQL, and performs every
Savvy and rank roll on the server. A client cannot provide species, stats,
bounds, a random seed, or a roll result.

## Deferred gap

The request does not bind the player's confirmation to the exact pet revisions
shown by the Merge UI. A delayed but otherwise valid request therefore operates
on the latest locked state of the selected IDs. This is not a cross-account or
client-supplied-roll vulnerability, but it is weaker than the desired
irreversible-action intent guarantee because the deputy is consumed.

## Required future contract

- The server issues a short-lived, single-use Merge confirmation token after it
  resolves the selected main pet, deputy pet, material, and preview bounds.
- The token binds account ID, character ID, ordered main/deputy IDs, both pet
  revisions, material ID and quantity, pinned content revision, and expiry.
- The final request carries the opaque token; it does not carry trusted stats,
  bounds, or rolls.
- Commit rechecks the token and every bound revision under the existing row
  locks before generating the server-side rolls.
- A stale, swapped, replayed, expired, or altered request fails without RNG,
  inventory consumption, deputy deletion, or pet mutation.
- The first terminal result and later replays remain durable and auditable by
  operation identity.

## Acceptance tests

- Changed main or deputy revision after preview rejects the confirmation.
- Swapping main and deputy rejects the confirmation.
- Changing material or quantity rejects the confirmation.
- Cross-character pet IDs and tokens reject without information disclosure.
- Concurrent confirmation attempts produce one commit at most.
- A successful commit still records all six Savvy bounds/rolls and the rank
  bounds/roll, and all randomness remains server-generated.
