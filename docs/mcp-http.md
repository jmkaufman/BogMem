# MCP Streamable HTTP

BogMem exposes the same palace and Coliseum recall MCP tools over stdio and
HTTP. The HTTP transport is for independently supervised services such as the
File Transfer Tool (FTT), where one process cannot own another process's stdin
and stdout.

BogMem implements the finalized MCP Streamable HTTP transport through protocol
version `2025-11-25`. It uses stateless JSON response mode:

- Every JSON-RPC message is a separate `POST` to one MCP endpoint.
- Requests receive one `application/json` response.
- Notifications and client responses receive `202 Accepted` with no body.
- `GET` and `DELETE` on the MCP endpoint return `405 Method Not Allowed`.
- BogMem does not allocate an `MCP-Session-Id` or open an SSE stream because it
  does not initiate messages to clients.

This is Streamable HTTP, not the deprecated HTTP+SSE transport.

## Start one palace service

Loopback is the default and is appropriate when FTT and BogMem run on the same
host:

```bash
export BOGMEM_MCP_TOKEN="$(openssl rand -hex 32)"

bogmem mcp \
  --palace /data/palaces/social-signals \
  --transport http \
  --listen http://127.0.0.1:7079 \
  --endpoint /mcp
```

The endpoints are:

- `POST http://127.0.0.1:7079/mcp` — MCP messages.
- `GET http://127.0.0.1:7079/healthz` — process liveness and transport mode.

The token can be passed with `--token`, but `BOGMEM_MCP_TOKEN` is preferable
because command-line arguments may be visible to other processes.
When a token is configured it protects both endpoints, so FTT should attach the
same bearer header to its health check.

## FTT client exchange

FTT should create one named `HttpClient` per palace route and apply its bearer
token. Initialize once when the route becomes healthy:

```http
POST /mcp HTTP/1.1
Authorization: Bearer <token>
Content-Type: application/json
Accept: application/json, text/event-stream

{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"file-transfer-tool","version":"1.0"}}}
```

BogMem returns its negotiated version, server identity, and tool capability in
the JSON response. Subsequent requests should include the negotiated version:

```http
POST /mcp HTTP/1.1
Authorization: Bearer <token>
Content-Type: application/json
Accept: application/json, text/event-stream
MCP-Protocol-Version: 2025-11-25

{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"bogmem_graph_observe","arguments":{"palace_id":"0198...","observation_id":"signals:42","occurred_at":"2026-07-23T12:05:00Z","actor_ids":["account-a","account-b"],"weight":2.0,"context":"shared-endpoint","source":"ftt","workflow_id":"social-ingest","run_id":"run-42","artifact_id":"artifact-7","signal_type":"co-activity"}}}
```

The observation ID and lineage form BogMem's idempotency boundary. FTT can
retry a timed-out call with the same normalized envelope. BogMem returns
`created` on the first call and `already_exists` for an equivalent replay; it
rejects reuse of the ID with different evidence.

The transport is stateless, but the palace is not: the long-lived process owns
one `PalaceRuntime` and its BogDB connections until shutdown. FTT should retain
the `palace_id` returned by `bogmem_palace_status` and send it as a routing
guard on graph calls.

## Configuration

| CLI option | Environment variable | Default |
| --- | --- | --- |
| `--transport http` | `BOGMEM_MCP_TRANSPORT=http` | `stdio` |
| `--listen URL` | `BOGMEM_MCP_LISTEN` | `http://127.0.0.1:7079` |
| `--endpoint PATH` | `BOGMEM_MCP_ENDPOINT` | `/mcp` |
| `--token VALUE` | `BOGMEM_MCP_TOKEN` | none on loopback |
| `--allowed-origin LIST` | `BOGMEM_MCP_ALLOWED_ORIGINS` | no browser origins |

`--allowed-origin` is a comma-separated list of exact HTTP origins, such as
`https://operations.example`. It is needed only for browser-based clients.
Ordinary FTT `HttpClient` requests do not send an `Origin` header.

## Network boundary

BogMem rejects an `Origin` header unless its exact origin was allowed. It also
refuses to bind beyond loopback without a bearer token. For containers or
separate hosts:

```bash
export BOGMEM_MCP_TOKEN="$(openssl rand -hex 32)"
bogmem mcp \
  --palace /data/palaces/social-signals \
  --transport http \
  --listen http://0.0.0.0:7079
```

Put that listener behind the deployment's authenticated TLS reverse proxy or
private service mesh; do not expose plaintext port 7079 directly to an
untrusted network. The current bearer token is a service credential, not an
end-user authorization model. FTT remains responsible for deciding which
tenant, workflow, or artifact may route to which palace.
