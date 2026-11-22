# Observability

[← xrmverse documentation](../README.md#documentation)

## Introduction

Observability covers how you check that a server is alive, read its logs, collect its metrics and trace its requests. In
Dataverse Microsoft monitors the service for you; with xrmverse you run the server, so these signals are exposed for your own
monitoring tools.

| Signal | Endpoint or tool | Who can use it |
|---|---|---|
| Liveness | `GET /healthz` | Anyone (anonymous) |
| Health and self-checks | `GET /admin/health`, `GET /admin/doctor` | System Administrators |
| Metrics | `GET /metrics` (Prometheus) | Anyone (anonymous), unless switched off |
| Traces | OTLP export | Your collector, when configured |
| Logs | `xrmd logs`, `GET /admin/logs` | System Administrators |

## Health checks

- `GET /healthz` answers `ok` without authentication. Use it for load balancers and container health checks (the Docker
  health check does).
- `GET /admin/health` and `GET /admin/doctor` give the detail and need the System Administrator role (see
  [Admin API](admin-api.md)).

## Metrics

`GET /metrics` returns the Prometheus text format. It is **anonymous**, because a scraper has no token: do not expose it to
untrusted networks.

| Instrument (meter `Xrmd`) | Tags | Meaning |
|---|---|---|
| `xrmd.requests` | `route`, `status` | Requests served |
| `xrmd.async.queue` | `state`: `waiting` or `inProgress` | System jobs in the asynchronous queue |
| `xrmd.pipeline.stage.duration` | | Time spent in pipeline stages |

Metrics are on by default. To switch them off, set `Observability:Metrics:Enabled=false` (environment variable
`Observability__Metrics__Enabled=false` on the `xrmd` service); `/metrics` then answers **404**.

## Tracing

Traces of ASP.NET Core and of the `Xrmd` activity source (including the `pipeline.*` activities) are exported over OTLP when
`Observability:Otlp:Endpoint` (`Observability__Otlp__Endpoint`) is set. Otherwise nothing is exported.

## Logs

`xrmd logs` (add `-f` to follow) reads the server's recent log entries without shell access to the container (see
[CLI xrmd](cli.md)). The same entries are served by `GET /admin/logs`.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Observability:Metrics:Enabled` | `true` | Serve `/metrics` |
| `Observability:Otlp:Endpoint` | not set | OTLP endpoint for traces; nothing is exported when empty |

See [Configuration](configuration.md).
