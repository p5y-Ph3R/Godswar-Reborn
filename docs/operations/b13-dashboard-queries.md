# B13 dashboard and query contract

## Status and use

These panels use the implemented Prometheus-normalized B13 instrument names
and finite labels. The B13 focused checks ratchet their source/export
contract.

The queries contain no player, account, character, session, operation,
network-address, packet, exception or arbitrary-text labels.

This dashboard is an application view, not a production capacity model or an
upstream DDoS console.

## Service and admission

| Panel | Query |
| --- | --- |
| Target scrape health | `up{job="godswar-server"}` |
| Process liveness | `godswar_server_operations_liveness` |
| Instance readiness | `godswar_server_operations_readiness` |
| Readiness by finite reason | `max by (instance,phase,reason) (godswar_server_operations_readiness)` |
| Management requests | `sum by (route,outcome) (rate(godswar_server_operations_management_requests[5m]))` |
| Active TCP admissions | `godswar_server_operational_admission{operational_state="connections_active"}` |
| Unauthenticated admissions | `godswar_server_operational_admission{operational_state="connections_unauthenticated"}` |
| Bound UDP sessions | `godswar_server_operational_udp{operational_state="sessions_bound"}` |
| UDP capacity | `godswar_server_operational_udp{operational_state="sessions_capacity"}` |

Probe `/ready` separately through the trusted collector or container
healthcheck. The gauge and HTTP probe read the same bounded cached operational
state; neither performs dependency I/O while serving the request or scrape.

Management rejection view:

```promql
sum by (route, outcome) (
  increase(
    godswar_server_operations_management_requests{
      outcome=~"unauthorized|rejected|bad_request|headers_too_large|overloaded"
    }[5m]
  )
)
```

Display the current build, schema and content revision from deployment metadata,
not high-cardinality application labels.

## Critical runtime tasks

```promql
godswar_server_operations_critical_tasks
```

Display this as a finite table grouped only by `task` and `state`. The task
catalog is code-defined; never substitute exception text or a runtime task
name.

```promql
sum by (simulation_loop) (
  rate(godswar_server_simulation_tick_missed_deadlines[5m])
)
```

```promql
histogram_quantile(
  0.99,
  sum by (le, simulation_loop) (
    rate(godswar_server_simulation_tick_duration_bucket[5m])
  )
)
```

```promql
histogram_quantile(
  0.99,
  sum by (le, simulation_loop) (
    rate(godswar_server_simulation_tick_schedule_drift_bucket[5m])
  )
)
```

The bounded collector exports cumulative millisecond buckets at `0.5`, `1`,
`2.5`, `5`, `10`, `25`, `50`, `100`, `250`, `500`, `1000`, `2500`, `5000`,
`10000`, `30000`, and `+Inf`, followed by `_sum` and `_count`. Quantiles are
bucket-resolution estimates, not exact samples.

## PostgreSQL commands and outbox

| Panel | Query |
| --- | --- |
| Inbox transaction rate | `sum by (family,outcome) (rate(godswar_command_inbox_transactions_total[5m]))` |
| Inbox mean duration | `sum by (family) (rate(godswar_command_inbox_transaction_duration_ms_sum[5m])) / sum by (family) (rate(godswar_command_inbox_transaction_duration_ms_count[5m]))` |
| Inbox p95 duration | `histogram_quantile(0.95, sum by (le,family) (rate(godswar_command_inbox_transaction_duration_ms_bucket[5m])))` |
| Outbox dispatch p95 duration | `histogram_quantile(0.95, sum by (le) (rate(godswar_outbox_dispatch_duration_ms_bucket[5m])))` |
| Outbox backlog | `godswar_outbox_backlog` |
| Oldest outbox age | `godswar_outbox_oldest_age_seconds` |
| Outbox worker state | `godswar_outbox_dispatcher_state` |
| Outbox heartbeat age | `godswar_outbox_heartbeat_age_seconds` |
| Outbox retries | `sum by (consumer,reason) (rate(godswar_outbox_retries_total[5m]))` |
| Outbox poison | `sum by (consumer,reason) (increase(godswar_outbox_poison_total[1h]))` |
| Strict sequence gaps | `sum by (consumer) (increase(godswar_outbox_sequence_gaps_total[1h]))` |

Add standard Npgsql connection/pool panels only after their exact exported
names and dimensions have been verified. Database, user and connection-string
values must not become labels.

## Checkpoints and progression retry

```promql
godswar_server_checkpoints_ready
```

```promql
godswar_server_checkpoints_queue_depth
```

```promql
godswar_server_checkpoints_dirty_age
```

```promql
godswar_server_checkpoints_heartbeat_age
```

```promql
godswar_progression_retry_queue_depth
```

```promql
godswar_progression_retry_oldest_age_seconds
```

```promql
godswar_progression_retry_heartbeat_age_seconds
```

```promql
godswar_progression_retry_worker_state
```

Show queue depth beside its configured capacity from deployment metadata.
Never infer a capacity guarantee from one local test.

## Durable gameplay commands

```promql
sum by (family, outcome) (
  rate(godswar_commands_total[5m])
)
```

Suggested finite family filters:

```text
monster_reward_settlement
progression_interval_settlement
bag_item_activation
pet_level_upgrade
pet_presence_transition
equipment_bag_transfer
equipment_forge
zodiac_skill_grid_activation
zodiac_skill_grid_upgrade
```

Panels should emphasize provider-unavailable, request-hash-conflict,
precondition-failed and duplicate outcomes. A duplicate is not automatically
an incident; correlate it with retry behavior and committed receipts.

## Logs, traces and exporter pressure

| Panel | Query |
| --- | --- |
| Structured events | `sum by (log_event,log_outcome) (rate(godswar_server_logs_events[5m]))` |
| Trace activities | `sum by (trace_outcome) (rate(godswar_server_traces_spans[5m]))` |
| Collector bounds | `godswar_server_metrics_collector` |

Do not add log message, trace ID, operation ID, exception type, endpoint or
player identity as a dashboard grouping.

## Host and upstream panels

Collect CPU, working set, allocation/GC rate, thread-pool pressure, open
handles/file descriptors, socket counts, container restarts and log-driver
disk use through the trusted host/collector.

The upstream panel is empty until a provider is selected. A production
dashboard must eventually include:

- arbitrary TCP/UDP mitigation state;
- edge and clean-origin bytes/PPS;
- discarded attack traffic;
- origin health;
- region/failover state; and
- mitigation alerts and provider incident identifier.

Application packet/drop counters cannot measure traffic that never reached the
origin.

## Query verification

The B13 focused checks verify the fixed histogram rendering and finite
operational families. For each deployment:

1. scrape a running raw-development profile over loopback;
2. scrape the secure Docker profile through only its private management path;
3. compare deployed instruments and labels to this file;
4. generate one bounded failure for each finite alert family;
5. verify no prohibited label/value appears; and
6. retain the output with the deployment evidence.
