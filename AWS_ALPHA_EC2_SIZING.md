# AWS EC2 sizing for the Reborn alpha

Last reviewed: 2026-07-31

## Decision

For one **Tempest** realm capped at **300 concurrently connected players in
total**, start the alpha with:

| Resource | Recommendation |
| --- | --- |
| Game host | **1 x `c8i.4xlarge`**, Linux, x86-64, On-Demand |
| Game-host capacity | 16 vCPU, 32 GiB RAM, EBS-only, up to 15 Gbps network and up to 10 Gbps EBS bandwidth |
| Root/application disk | **100 GiB encrypted `gp3`**, initially using the included 3,000 IOPS and 125 MiB/s |
| Durable database | Separate managed PostgreSQL 17, preferably Amazon RDS; do not put the authoritative alpha database on the game EC2 host |
| Public entry | Network Load Balancer (NLB) in front of the game host |
| Region | Test `ap-southeast-6` (New Zealand) and `ap-southeast-2` (Sydney) with actual alpha players; choose by measured latency and required service availability |
| Purchase model | On-Demand during alpha; consider a Savings Plan only after a stable measured baseline |

If buying only one game-server EC2 instance now, buy the
**non-Flex `c8i.4xlarge`**.

This is a conservative starting configuration, **not a guarantee that the
current server supports 300 players**. Admission to 300 players requires the
load-test gates in this document. In particular, the test must cover players
concentrated around a world boss, not merely 300 idle players spread across
many maps.

The AWS specifications above come from the current
[compute-optimized instance table](https://aws.amazon.com/ec2/instance-types/compute-optimized/).
AWS lists C8i in the
[Asia Pacific (New Zealand) Region](https://docs.aws.amazon.com/ec2/latest/instancetypes/ec2-instance-regions.html),
but an individual size can still differ by Availability Zone. Verify
`c8i.4xlarge` in the chosen AZ before provisioning.

## What “300 players per server” means here

For this recommendation:

- `server` means one logical Tempest realm;
- the limit is 300 simultaneous connections across all Tempest maps,
  dungeons, and battlefields combined;
- it does **not** mean 300 players per map;
- observers, reconnecting sessions, and login bursts still consume resources;
- the worst supported event is assumed to be up to 300 players in one
  high-interest area around a world boss.

If the intended limit later becomes 300 players per map or thousands per
realm, this sizing decision must be replaced rather than extrapolated.

## Why this size fits the current repository

This repository keeps gameplay and durable authority in
`Godswar.Server` workers. B18C1 can place an opt-in opaque relay in front of
one combined worker. Completed B18C2 can instead run a loopback-only
unchanged-client semantic gateway that authenticates locally and opens an
mTLS private backhaul to an exact statically routed worker. B18C2 is a
local-first boundary, not an internet-edge deployment:

- [`Godswar.Server.csproj`](src/Godswar.Server/Godswar.Server.csproj) targets
  .NET 10 and currently has x86-64 performance evidence only.
- [`Program.cs`](src/Godswar.Server/Program.cs) composes login, game sessions,
  all map instances, persistence workers, networking, and management in that
  process.
- [`AuthoritativePlayerMovementPolicy.cs`](src/Godswar.Server/World/Systems/Players/AuthoritativePlayerMovementPolicy.cs)
  runs authoritative movement at 20 Hz, giving a 50 ms simulation interval.
- [`MonsterMapRuntime.cs`](src/Godswar.Server/Game/MonsterMapRuntime.cs) runs
  monsters at 12 Hz, giving an approximately 83.3 ms interval.
- The current monster-world loop advances all local world runtimes
  sequentially before its bounded network fan-out. Simultaneous busy boss maps
  can therefore threaten one shared deadline.
- The configured 512-connection ceiling is only an administrative bound. It is
  not measured proof of 300-player capacity.
- The accepted baseline currently covers 64 in-process synthetic bots for ten
  seconds. It excludes real sockets, TLS, UDP encryption, PostgreSQL, AOI
  fan-out, and world-boss concentration, so it cannot be used to calculate
  production capacity.
- Password verification uses CPU-heavy PBKDF2 and can compete for CPU during a
  reconnect or login surge.

A compute-optimized fixed-performance host gives the simulation and login work
CPU headroom while retaining 32 GiB for the ECS world, connection state,
queues, runtime content, and .NET GC. C8i is powered by Intel Xeon 6 and AWS
states that it improves price performance over C7i. The x86 choice also avoids
making the untested ARM/Graviton transition part of the first alpha launch.

Do not select `c8i-flex`, `c7i-flex`, or a burstable T-family host for the
authoritative world. AWS documents that Flex instances have a 40% CPU
baseline, can provide 100% for 95% of a 24-hour window, and may gradually
reduce maximum burst throughput under prolonged high CPU. Fixed-performance
instances can sustain full CPU performance. See
[AWS instance performance](https://docs.aws.amazon.com/ec2/latest/instancetypes/instance-types.html#instance-performance).

## Smaller and fallback choices

| Choice | Valid use | Restriction |
| --- | --- | --- |
| `c8i.2xlarge` (8 vCPU, 16 GiB) | Development, staging, and a measured small closed-alpha cohort | A test candidate only. Do not admit 300 players based on this document. |
| `c7i.4xlarge` (16 vCPU, 32 GiB) | Fallback if the chosen AZ cannot supply C8i | Re-run the complete load gate; do not assume C8i results transfer exactly. |
| `m8i.4xlarge` class | Memory fallback if measurement shows 32 GiB or GC pressure is the limiting resource | Use only after profiling demonstrates memory, rather than tick CPU, is the bottleneck. |
| `c8i.8xlarge` class | Temporary vertical escape hatch | More cores will not automatically fix a sequential hot-map loop. Profile before paying for it. |

Spot is acceptable for disposable, capped load generators and CI workers, but
not for an active authoritative realm. AWS Spot capacity can be reclaimed and
the interruption notice is only best effort, normally two minutes before the
interruption. See
[AWS Spot interruption notices](https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/spot-instance-termination-notices.html).

## Recommended alpha topology

```text
Players
   |
   v
Internet-facing Network Load Balancer
   |
   v
Private game target: c8i.4xlarge
  - approved client-facing ingress (still a launch gate)
       `----> Godswar.Server worker
  - worker owns gameplay, map-instance mailboxes, and persistence
  - host-local metrics/log collector
   |
   +---- private connection ----> RDS PostgreSQL 17

Administration ----> AWS Systems Manager/private management path

Local B18C2 validation only:
unchanged client -> loopback semantic gateway
                 -> TLS 1.3 mTLS private worker backhaul
```

The NLB can forward the current TCP compatibility ports and the future secure
TCP plus authenticated UDP profile. AWS NLB target groups support TCP, UDP,
TCP_UDP, TLS, QUIC, and TCP_QUIC. Its UDP flow affinity does not replace the
game's authenticated connection ID, ticket, replay protection, or
single-owner routing. See the
[AWS NLB protocol documentation](https://docs.aws.amazon.com/elasticloadbalancing/latest/network/introduction.html).

For pre-alpha local measurement, B18C2's semantic gateway and worker may run
as separate processes on the same host. The legacy client edge must remain
loopback-only; never publish it behind the NLB. The worker hop uses mutual
TLS and exact `RealmId`/`MapId`/`WorldInstanceId`/`ServerNodeId` routing, but
co-location remains one failure domain. B18C2 does not route UDP, preserve a
session across workers, or prove remote production placement.

For alpha, an Auto Scaling Group with desired/minimum/maximum capacity of one
can replace an unhealthy host, but it does not make the realm active-active
and it does not prevent disconnects during replacement. B18C2's local
semantic backhaul exists, but remote worker placement, cross-worker
reconnect/transfer, and live map migration do not.

PostgreSQL remains the authoritative owner of player value. B17 now provides
opt-in Redis adapters for disposable tickets/admissions, routes, presence,
and PostgreSQL-fenced player leases. The checked-in default is still local
coordination, and no managed Redis service, HA tier, provider SLA, or
production cost is approved. Public activation waits for measured latency,
outage, region, provider, and cost approval. Never store the only copy of
inventory, currency, equipment, progression, pets, or mounts in Redis.

## Host configuration

- Use a current Amazon Linux x86-64 image or another supported minimal Linux
  distribution and run the existing Linux container.
- Encrypt EBS with a customer-managed or AWS-managed KMS key.
- Start with 100 GiB `gp3`. AWS documents that `gp3` includes a consistent
  3,000 IOPS and 125 MiB/s baseline; provision more only when metrics show
  storage pressure. See
  [Amazon EBS gp3 performance](https://docs.aws.amazon.com/ebs/latest/userguide/general-purpose.html).
- Keep authoritative state off local disk. Send bounded logs and metrics
  off-host and configure retention.
- Give the instance an IAM role with only the required logging, monitoring,
  secret-reading, image-pull, and Systems Manager permissions.
- Do not expose SSH, RDP, the PostgreSQL port, or the management listener to
  the internet.
- Keep `/live`, `/ready`, `/metrics`, and `/traces` host-local or on an
  authenticated private monitoring path.
- Benchmark .NET Server GC against the current GC mode before launch; do not
  enable it blindly without the 300-player comparison.

## Region selection

New Zealand is `ap-southeast-6`, has three Availability Zones, and is an
opt-in Region. Sydney is `ap-southeast-2`. See the
[AWS Region list](https://docs.aws.amazon.com/global-infrastructure/latest/regions/aws-regions.html).

Use New Zealand when the alpha population is predominantly in New Zealand and
its measured client RTT is best. Compare Sydney when players are distributed
across Australia, New Zealand, and Southeast Asia. Run the actual original
client through the intended NLB path; ICMP ping alone is not the acceptance
test.

Before launch, confirm the exact instance offering:

```powershell
aws ec2 describe-instance-type-offerings `
  --region ap-southeast-6 `
  --location-type availability-zone `
  --filters Name=instance-type,Values=c8i.4xlarge `
  --query "InstanceTypeOfferings[].Location"
```

AWS documents this command in
[`describe-instance-type-offerings`](https://docs.aws.amazon.com/cli/latest/reference/ec2/describe-instance-type-offerings.html).

## Mandatory 300-player capacity gate

Generate load from separate hosts so load bots do not steal CPU from the game
host. Use real sockets and the exact alpha transport. Exercise real AOI
fan-out, combat, persistence, login verification, and item/progression paths.

Run at least:

1. 100 active players distributed across open-world maps.
2. 200 active players distributed across open-world maps.
3. 300 active players distributed across open-world maps.
4. 300 active players concentrated in one world-boss area.
5. A boss crowd while other parties create and play dungeon instances.
6. A bounded login/reconnect surge.
7. An autosave burst plus injected PostgreSQL latency.
8. Network latency, jitter, loss, duplication, reordering, and 1,200-byte MTU
   constraints for the secure UDP profile.
9. A two-hour peak soak, including at least one 30-minute boss-load period.
10. Graceful shutdown, process crash, EC2 replacement, and database recovery.

The `c8i.4xlarge` passes the 300-player gate only if all of these hold:

- movement tick p99 is at most 40 ms, 80% of its 50 ms interval;
- monster-world tick p99 is at most 66 ms, about 80% of its 83.3 ms interval;
- no three consecutive simulation deadlines are missed;
- missed deadlines remain below 0.1% over the test;
- sustained host CPU stays at or below 65%, with short peaks below 80%;
- memory stays at or below 70% of 32 GiB with no unbounded growth;
- no supported-load mailbox or network-admission operation is rejected;
- queue depth and PostgreSQL checkpoint/outbox backlog do not grow
  continuously;
- no player disconnect is caused by server overload;
- no durable player value is lost or duplicated;
- the host recovers to normal latency and queue depth after each injected
  fault.

These are proposed alpha acceptance targets, not current benchmark results.
Record the AMI, AZ, EC2 type, .NET and GC settings, transport profile, database
class, bot behavior, player distribution, duration, and build commit with
every result.

Per-world-instance tick and mailbox metrics should be added before the final
boss test. The current aggregate metrics can reveal overall overload but
cannot identify which exact map instance caused it.

## When to resize or split

Act when the 300-player test breaches any gate or live alpha repeatedly
reaches:

- tick p99 above 80% of its deadline;
- CPU above 65% for five minutes;
- memory above 75% or steadily growing;
- any supported-load admission rejection;
- sustained queue depth above 25% of capacity;
- database pool saturation or a growing save backlog;
- a boss map degrading unrelated maps.

Use this order:

1. Profile and remove the measured code or allocation bottleneck.
2. Add per-instance observability and isolate the heavy map in the planned
   worker-process architecture.
3. Add a second game-worker host only after B17 is staging-qualified with a
   remote-production ingress, reconnect/transfer, and failure-isolation gate.
4. Vertically resize only when the workload cannot yet be partitioned or the
   measurement proves that more host capacity helps.

The future split can place a small gateway/control process, open-world workers,
and a dungeon/battlefield worker pool on separate EC2 instances. Do not buy
that fleet yet: B18C2 verifies semantic admission and mTLS backhaul locally,
but not remote production placement or shared coordination. Co-locating
gateway and worker is measurement, not failure isolation. Connected
open-world map groups must remain on one worker until controlled transfer
exists. A future battlefield worker may be started before a scheduled event,
while dungeon workers remain available independently.

## Availability and cost boundaries

One active game host is a deliberate alpha cost choice and a single failure
domain. RDS backups, point-in-time recovery, tested restores, an EC2 launch
template, and a reproducible container deployment are required before
valuable alpha progression is accepted. Multi-AZ database deployment is
preferred; if Single-AZ RDS is chosen to reduce alpha cost, record the
additional downtime risk explicitly.

Use current regional On-Demand prices at purchase time rather than copying a
stale hourly figure into this repository. The monthly worksheet must include:

```text
730 * EC2 hourly rate
+ 100 GiB gp3 and snapshots
+ NLB hours and LCUs
+ RDS instance, storage, I/O, backups, and cross-AZ traffic
+ internet data transfer
+ CloudWatch logs, metrics, alarms, and traces
+ NAT Gateway or VPC endpoint costs
+ public IPv4 charges
```

On-Demand avoids a long-term commitment while the workload is still being
measured. Use the
[AWS Pricing Calculator](https://calculator.aws/) with the selected Region,
currency, and review date before approval.

## Final purchase checklist

- [ ] Confirm 300 means realm-wide concurrent players.
- [ ] Compare real-client latency in New Zealand and Sydney.
- [ ] Confirm `c8i.4xlarge` is offered in the chosen AZ.
- [ ] Price EC2, RDS, NLB, EBS, egress, monitoring, and networking together.
- [ ] Provision `c8i.4xlarge` On-Demand with encrypted 100 GiB `gp3`.
- [ ] Keep PostgreSQL separate and private.
- [ ] Put public game traffic through NLB; keep management private.
- [ ] Run 100-, 200-, and 300-player tests, including the concentrated boss
  scenario.
- [ ] Do not raise the public cap to 300 until every capacity gate passes.
