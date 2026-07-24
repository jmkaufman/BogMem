# Palace runtime

A palace is the unit of storage, identity, lifecycle, and MCP isolation in
BogMem. `PalaceRuntime` owns one BogDB database and composes drawer recall and
temporal actor-graph memory over that shared database.

## Manifest

The first open creates a `BogMemPalace` manifest containing:

- A UUIDv7 palace ID.
- An immutable human-readable name.
- The palace schema version.
- Creation time.
- Advertised capabilities.

The manifest lives inside BogDB, so moving the palace directory does not change
its identity. Opening an existing palace with a conflicting requested name is
rejected instead of silently changing a routing identity.

```bash
bogmem init --palace /data/palaces/social-signals --name social-signals
bogmem status --palace /data/palaces/social-signals
```

Existing pre-runtime palaces are upgraded in place by adding the manifest and
graph tables. Drawer IDs and embeddings are unchanged.

## One runtime, one MCP service

```text
MCP process
  └── PalaceRuntime
      ├── stable manifest
      ├── BogDbMemoryStore
      └── BogDbActorGraphStore
```

Both stores create their own BogDB connection but share the database and its
lifecycle. Disposing the runtime closes the stores before closing BogDB.

The runtime supports newline-delimited MCP JSON-RPC over stdio and stateless
MCP Streamable HTTP. Both transports use the same `McpServer` dispatcher and
therefore expose the same tools, routing guards, read-only rules, and errors.
The HTTP form is intended for FTT and a Coliseum supervisor:

```bash
BOGMEM_MCP_TOKEN=replace-me bogmem mcp \
  --palace /data/palaces/social-signals \
  --transport http \
  --listen http://127.0.0.1:7079
```

A Coliseum service should supervise one process/runtime per palace rather than
sending a palace selector to a single globally shared database. Graph calls
accept an optional `palace_id` routing guard; a mismatch is an MCP
invalid-params error. See [`mcp-http.md`](mcp-http.md) for the wire and security
contract.

## Observation contract

`bogmem_graph_observe` accepts normalized observations:

```json
{
  "palace_id": "0198...",
  "observation_id": "signals:42",
  "occurred_at": "2026-07-23T12:05:00Z",
  "actor_ids": ["account-a", "account-b"],
  "weight": 2.0,
  "context": "shared-endpoint",
  "source": "ftt",
  "workflow_id": "social-ingest",
  "run_id": "run-42",
  "artifact_id": "artifact-7",
  "signal_type": "co-activity"
}
```

Lineage participates in the observation fingerprint. Replaying the same
normalized envelope is a no-op; reusing its ID with different actors, time,
weight, context, or lineage is rejected.

FTT owns extraction, normalization, routing, retry, and workflow semantics.
BogMem owns validation, idempotent persistence, temporal projection, and recall.

## Palace registry and Coliseum boundary

BogMem calls the multi-palace layer a Coliseum. It begins outside the palace
runtime:

1. `PalaceRegistry` maintains an atomic catalog from palace ID or unique name
   to its current path.
2. Routed opens verify that the manifest at that path still owns the expected
   palace ID.

The cross-palace recall service uses that catalog to aggregate read-only drawer
and graph-neighbor results with palace provenance. Its registry-bound MCP
service is always read-only. A process supervisor can:

1. Start, stop, and health-check each palace independently.
2. Route an FTT observation envelope to one or more palace IDs.
3. Execute the existing Coliseum recall contract concurrently against
   long-lived palace processes.

BogMem should not introduce cross-palace edges inside an individual database.
Coliseum orchestration belongs outside an individual palace because it must
retain which palace supplied each result. See
[`palace-registry.md`](palace-registry.md) for the registry contract and
[`cross-palace-recall.md`](cross-palace-recall.md) for Coliseum recall.
