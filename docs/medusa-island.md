# Medusa Island

Medusa Island is an on-demand dungeon entered through the stock **Instance
Caller** in either capital. The repository now preserves the source evidence
and a separate authored encounter contract. The NPC still fails closed: it
does not yet transfer a party, consume a daily entry, or settle rewards.

## Reviewed contract

- Minimum fighter level: 90.
- Native admission evidence permits 1-5 players. Encounter health and attack
  never scale with roster size: balance targets a five-player party, and solo
  play receives no compensating reduction.
- Entry allowance: the shared limit across all Medusa difficulties is read from
  `medusa_instance_settings`; it is seeded to three entries and can be changed
  in PostgreSQL without recompiling. The day is the startup-pinned realm
  calendar's civil day at the server-trusted request receipt time.
- Time limit: 40 minutes.
- Death revives at the dungeon entrance.
- Voluntary departure forfeits the run reward.
- Score awards are 1 for an ordinary monster, 50 for an elite or first-island
  boss, 1,000 for Stheno, and 1,100 for Medusa. The complete roster totals
  3,802.
- Map-200 Enhanced/Mythic content includes the capturable Baby Rock Elf.
  Successful capture consumes one Mysterious Tuck Net and creates a Rock Elf
  egg whose aptitude is rolled from database-owned difficulty weights.

The public guide calls the harder stock mode Enhanced. The shipped English
client labels it Advanced, so evidence code retains `Advanced` while authored
gameplay maps that legacy selection explicitly to `Enhanced`.

## Authored difficulties

The gameplay contract has three fixed profiles:

| Difficulty | Scene content | HP multiplier | Total encounter HP |
| --- | --- | ---: | ---: |
| Normal | map 204 / `Medusa_Island2` | 1x | 160,150,000 |
| Enhanced | map 200 / `Medusa_Island` | 2x | 320,300,000 |
| Mythic Medusa Island | map 200 / `Medusa_Island` | 5x | 800,750,000 |

Enhanced and Mythic deliberately share map 200. Runtime code must carry the
explicit difficulty with the world-instance identity; deriving difficulty
from map 200 is forbidden and fails closed.

### Baby Rock Elf capture rarity

Normal has no capturable Baby Rock Elf. Advanced (the client's label for
Enhanced) and Mythic use `medusa_pet_capture_rarity_weights`; each distribution
is stored in basis points and must total exactly 10,000.

| Aptitude | Advanced | Mythic |
| --- | ---: | ---: |
| Weak | 20% | 4% |
| Fool | 18% | 6% |
| Cowish | 16% | 8% |
| Moderate | 14% | 11% |
| Rational | 11% | 13% |
| Grumpy | 8% | 14% |
| Brave | 5% | 14% |
| Zealous | 4% | 12% |
| Smart | 2% | 9% |
| Ferocious | 1.5% | 6% |
| Godly | 0.5% | 3% |

The server rolls once inside the durable capture transaction and stores the
selected aptitude as the egg's item quality. Exact command replay returns the
already committed egg and never rerolls it.

Every profile contains the same 136 enemies. Their awards total 3,802. Normal
uses the external capture's per-template maximum HP; Enhanced and Mythic apply
exact 2x and 5x multipliers. The external test server advertised Stheno and
Medusa as 1/1 HP, so those two test sentinels retain their prior authored Normal
baselines instead of making the final encounter a one-hit kill:

| Role | Count | Score each | Normal HP each | Enhanced HP each | Mythic HP each |
| --- | ---: | ---: | ---: | ---: | ---: |
| Ordinary monsters | 102 | 1 | 800,000 | 1,600,000 | 4,000,000 |
| Elites | 30 | 50 | 250,000-8,000,000 | 500,000-16,000,000 | 1,250,000-40,000,000 |
| Euryale | 1 | 50 | 5,000,000 | 10,000,000 | 25,000,000 |
| Chrysaor | 1 | 50 | 2,000,000 | 4,000,000 | 10,000,000 |
| Stheno | 1 | 1,000 | 3,000,000 | 6,000,000 | 15,000,000 |
| Medusa | 1 | 1,100 | 3,500,000 | 7,000,000 | 17,500,000 |

Attack ratings are explicit encounter overrides, not inflated monster tiers.
Normal and Enhanced use the approved 4,700-10,000 and 5,000-13,000 role
ladders. Mythic rises conservatively to 5,500-16,000; its 5x difficulty is
primarily the health budget so basic attacks do not become unavoidable
one-shots. Every authored appearance uses tier 120 for the generic defense,
hit, and dodge curve; attack continues to come from the explicit role and
difficulty override.

Stheno takes full physical damage and only 10% final magical damage. Medusa
takes full magical damage and only 10% final physical damage. The modifier is
applied after ordinary combat calculation, independently of the generic 80%
typed-reduction cap.

## Native Instance Caller protocol

The active modular client routes the Instance Caller through dialog index `9`:

1. Clicking Athens `Athens_060` (`5199`) or Sparta `Sparta_060` (`5057`)
   advertises dialog `9` with the matching scene key.
2. The initial function request returns sub-ID `11` (Medusa Island).
3. Selecting `11` returns `[206, 204, 205, 207]`: the description, Advanced,
   Normal, and patched Mythic buttons.
4. Sub-ID `204` selects Advanced, `205` selects Normal, and `207` selects
   Mythic.

The checked-in actor source contains a stale Sparta interaction ID `5059`.
The published runtime baseline and stock capital data agree on `5057`, which
is the authoritative endpoint.

## Variant maps

| Native choice | Authored difficulty | Map | Scene |
| --- | --- | ---: | --- |
| 204 | Enhanced (legacy client label: Advanced) | 200 | `Medusa_Island` |
| 205 | Normal | 204 | `Medusa_Island2` |
| 207 | Mythic Medusa Island | 200 | `Medusa_Island` |

This mapping is supported by the shipped content rather than the numeric menu
IDs alone. Map 200 contains the Baby Rock Elf pet template and
uses the non-`D` monster keys. Map 204 uses the corresponding `D`-suffixed
normal-mode keys. Both maps use the same 128-by-128 terrain geometry.

## Honor reward markers

The guide publishes discrete score/time markers. These are preserved as exact
markers; the server must not invent interpolation, cumulative additions, or
rounding between them.

| Outcome marker | Normal Honor | Enhanced Honor |
| --- | ---: | ---: |
| Incomplete, score 0 | 300 | 300 |
| Incomplete, score 950 | 375 | 600 |
| Incomplete, score 1,200 | 450 | 750 |
| Incomplete, score 1,500 | 525 | 900 |
| Incomplete, score 1,700 | 600 | 1,050 |
| Incomplete, score 1,900 | 675 | 1,200 |
| Incomplete, score 2,200 | 750 | 1,350 |
| Complete, under 40 minutes | 975 | 1,800 |
| Complete, under 30 minutes | 1,050 | 1,950 |
| Complete, under 25 minutes | 1,125 | 2,025 |
| Complete, under 20 minutes | 1,200 | 2,100 |
| Complete, under 15 minutes | 1,275 | 2,175 |
| Complete, under 10 minutes | 1,350 | 2,250 |

The source conflict is retained as evidence, but the authored schedule follows
the confirmed Enhanced progression. Thresholds are inclusive, require the full
3,000 points, and select only the best qualifying title:

| Difficulty | At most 20 minutes | At most 15 minutes | At most 10 minutes |
| --- | --- | --- | --- |
| Enhanced | Medusa Executioners | Medusa Slayers | Medusa Challengers |
| Mythic Medusa Island | Gorgon Breaker | Bane of the Three Sisters | Heir of Perseus |

Normal awards no speed title. The live completion settlement grants the best
qualifying title to every
admitted character, records ownership, and selects it for world appearance.
Enhanced keeps its authored HardPoint schedule. Mythic title settlement grants
no HardPoints until a Mythic point schedule is authored. A newly applied
completion also sends the exact result through the stock faction-wide server
notice packet; replayed settlements do not repeat the announcement.

## Authored combat mechanics

- First-island lanes contain E1-E4 on the left with stun, E5-E8 in the centre
  with freeze, and E9-E12 on the right with bleed. Every one of those twelve
  elite groups has three ordinary escorts.
- Euryale is paired with E14 and uses Shackle; Chrysaor is paired with E15 and
  inflicts bleed. Those boss pairs have no ordinary escorts. Their remembered
  placement intent is retained without coordinates: Euryale is top-left and
  Chrysaor is top-right on the first island.
- The second island contains E13 and E16-E19, each with two ordinary escorts,
  plus the standalone E20 Elite Gorgon Guardian at the centre. The literal
  Elite Cyclops Swordsman is assigned to E13.
- The final island contains Stheno, Medusa, two ordinary Gorgon Pikemen, and
  two ordinary Gorgon Axemen.
- Pikemen refresh the attacked player's outgoing physical-damage boost;
  Axemen refresh the outgoing magical-damage boost. The authored effects are
  10x and last 30 seconds.

The exact native bindings are skill/status `2002/330` for stun, `2018/402`
for freeze, `2041/18` for bleed, `2017/401` for Euryale's Shackle,
`2082/236` for the Pikeman physical amplifier, and `2080/235` for the Axeman
magical amplifier. Status application is authored as guaranteed on a committed
hit; client `StatusOdds` ratings are retained as evidence and are not treated
as percentages.

Stock statuses 235 and 236 are specifically compatible with both Medusa
scenes. `Status.ini` restricts `AffectMap` to client-scene IDs 209 and 223;
`MapIdToNameConfig.ini` maps server content map 200 to client scene 209 and
content map 204 to client scene 223. These two namespaces must not be
conflated. The server still owns the 10x calculation and committed-hit refresh,
while the unmodified native status IDs can provide their 30-second client
projection on both Medusa maps.

The client supplies status, animation, and visual definitions, but no
monster-to-skill bindings. These ownership choices are authored
reconstruction. Exact ECS monster hits commit HP and record every authored
effect in the instance owner, including Bleed. Stun, freeze, and Shackle are
enforced by
server-authoritative action gates for legacy and secure-realtime movement,
common basic attacks, initial and pending-completion skill casts, and item
activation. The five visible effects are projected through exact,
complete opcode 10167 snapshots to the current target and same-instance
observers. Applying stun, freeze, or Shackle also interrupts an already
pending cast through an intentional duplicate-status FIFO. Self delivery is
impact, damage, the initial committed application 10167 (with an initial
self-only 10166 when applicable), then one atomic corrective batch containing
the complete current 10167 and self-only 10166 immediately before exactly one
10171. Observer delivery uses the same order without 10166: impact, damage,
initial 10167, then an atomic current 10167 plus exactly one 10171. The initial
snapshot does not wait behind cast-start publication; the corrective snapshot
closes refresh, expiry, and run-terminal races and can therefore be current or
empty when the control has already changed. Bleed remains server-only: its
committed hit uses the generic impact/direct-hit pair, and its seven later
200-HP ticks publish only vitals updates, plus death when lethal. No status 18
or skill 2041 packet is emitted.

## Implemented runtime behavior

- A Medusa run is bound once to an exact Creating dungeon instance. The bound
  identity includes the explicit difficulty, admitted character IDs, and all
  74 object-ID/generation/template/role bindings; map 200 can never be used to
  infer Enhanced versus Mythic.
- The run clock and encounter-mechanics clock advance as one owner-controlled
  boundary. Invalid identities do not advance either clock, and the unresolved
  exact 40-minute boundary gates effects without splitting their state.
- Outgoing 10x damage is a side-effect-free preview over the currently
  observed mechanics snapshot. It cannot expire effects or consume bleed ticks
  before a later health mutation commits.
- Prepared instance creation completes while the descriptor is still
  `Creating`. Preparation receives a short-lived, thread-affine capability;
  retained capabilities are revoked before publication and registry callers
  receive only immutable instance identity.
- Monster runtimes now support a typed `Never` respawn policy in both the
  legacy and ECS implementations. Lethal Medusa flow is `Died`, then corpse
  `Despawned`, then permanently absent in generation 1. A nonlethal leash
  return settles the same living generation at home.
- The client HMP block plane is decoded and cross-validated against 381/381
  named client coordinates. All 74 reconstructed positions now occupy their
  intended component, sample the decoded clear value, retain at least 4.03
  units of local clearance, and remain at least 7.81 units apart. They are
  still candidates because height, collision, and transport-trigger behavior
  require an in-client acceptance pass.
- Monster bootstrap validates the exact authored tier, HP, template, scene,
  object generation, `Never` lifecycle, and immutable spawn fingerprint. Bind,
  validate, and attach publish as one owner operation; any failure leaves both
  ownership and monster runtime absent. The production path intentionally
  returns `PlacementNotCertified` while the client-acceptance gate is open.
- Bound player-to-monster damage uses one typed transaction under the Medusa
  owner and monster gates. It applies a current 30-second carrier amplifier
  before Stheno/Medusa's final wrong-channel reduction, commits the exact
  generation and health revision, and couples a lethal mutation to the run's
  defeat score. Channelless raw, rebound, and periodic damage cannot bypass
  this boundary. The registry also carries the exact world-instance revision,
  ownership fence, and monster-runtime identity through direct attacks,
  rebound, and Gaia reflection; leaving or moving between two instances of
  the same map makes the captured intent stale instead of retargeting it.
- Player elemental secondary effects are intentionally suppressed against an
  owned Medusa monster until they have an equivalent typed handoff. The
  primary typed hit can still commit, but Burn cannot be installed and later
  lost through the channelless periodic-damage fence.
- Bound monster-to-player basic attacks now use one exact ECS transaction.
  Emitted world revision, ownership, object/life identity and full monster
  runtime/roster identity are revalidated before HP. A positive committed hit
  records its authored owner effect exactly once; zero damage records only its
  final replay/time observation. A committed Chrysaor or elite-lane Bleed is
  retained after the source retires. The world pump drains each due tick before
  monster movement or attacks, applies direct health loss once, and retries
  publication or persistence without applying HP or bytes twice. Bound Legacy
  attacks fail closed. Exact-life server action
  gates consume the recorded control effects and fail closed when a bound
  owner is incomplete, stale, or unavailable. Stun, freeze, Shackle, and the
  two 30-second carrier amplifiers now publish native complete status
  snapshots with exact world, ownership, object, and life fences. Control
  application cancels an in-flight cast only after the initial and corrective
  current-status FIFO is admitted; stale membership suppresses old packets, while
  unresolved current authority or reliable-egress failure terminates the
  affected session instead of replaying an old event.

## Admission and runtime boundary

The authored admission transaction freezes one authoritative party roster,
whose issued lease is a non-revocable party-revision capability through its
expiry; conflicting leader or roster mutations must serialize after expiry.
It binds the realm day to the startup-coordinated timezone-rules fingerprint,
reserves the shared daily allowance, creates one exact dormant world instance,
and prepares an all-or-none hidden transfer. It persists an irreversible
barrier before the exact atomic public roster commit, consumes every member's
attempt only after that commit, then starts the run at the durable consumption
timestamp. A failed create or pre-barrier transfer does not burn an allowance.
Released and terminal rows retain their exact member assignment until durable
abort/release or egress/retire cleanup receipts are committed. Those gateway
ledgers must retain cleanup tombstones across process loss, so stale Prepare,
Ensure, Commit, or Start capabilities can never resurrect a cleaned run.
The contracts and guarded claim-store checks exist, but no production party,
pending-runtime, whole-roster-transfer, cleanup scheduler, reconnect, or
Instance Caller adapter implements them yet; entry therefore remains
fail-closed.
Likewise, reconnecting with only a saved map byte of 200 or 204 is rejected
before a default runtime can be created; recovery must supply the durable exact
world-instance assignment or egress the character to its recorded source.
Maps 200 and 204 are also rejected from static open-world worker route
configuration, so a generic gateway assignment cannot recreate the same
bypass on another node. Legacy map-only portal transfers reject those targets
before checkpoint persistence and before resolving a default runtime.

The development client adds an explicit localized `207` button branch through
`tools/PatchClientMedusaMythicOption.ps1`. The server admits that exact nested
choice and carries Mythic as explicit run metadata even though it shares map
200 with Enhanced. Mythic currently has no published HardPoint schedule, so it
does not invent an Honor award; lack of that schedule does not block terminal
instance egress.

Sources: the current
[GodsArena Medusa Island guide](https://wiki.godsarena.online/books/godsarena-godswar/page/medusa-island),
the shipped English `NpcFunRepetition.lua`/`LuaText.lua`, and the reviewed map,
NPC, and monster-template baselines in this repository.
