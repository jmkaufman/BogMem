# Coliseum cross-palace recall

Coliseum recall queries registered palaces without merging their databases or
creating cross-palace edges. Every hit carries the stable palace ID and name
that supplied it.

## Drawer recall

```bash
bogmem recall "where was token rotation decided?" \
  --registry /data/bogmem/registry.json \
  --limit 20 \
  --per-palace-limit 5
```

Omit `--palaces` to query every registered palace, or select exact IDs and
names:

```bash
bogmem recall "coordinated endpoint activity" \
  --registry /data/bogmem/registry.json \
  --palaces social-signals,artifact-memory
```

The result contains:

- `palacesRequested` and `palacesSucceeded`.
- One source row per palace, including its retrieval mode and embedding model.
- Hits with `palaceId`, `palaceName`, `federatedRank`, `localRank`, and the
  unmodified local distance and hybrid scores.
- Sourced failures for palaces that were missing, unavailable, or failed an
  identity check.

One unavailable palace does not discard healthy results. The CLI exits with
code 1 only when at least one palace was requested and none could be queried.
Unknown explicit selectors are request errors rather than partial failures.

## Ranking

Coliseum results use `local-rank-interleave-v1`. The first local hit from each
palace is considered before second local hits, then third local hits, and so
on. Local score breaks ties within a rank tier.

BogDB hybrid scores include palace-local BM25 normalization, and registered
palaces may use different embedding models. Treating those values as one
globally calibrated score would imply precision BogMem does not have. The
response therefore retains the raw local score and model identity instead of
manufacturing a global relevance value.

## Graph-neighbor recall

```bash
bogmem recall-neighbors account-42 \
  --window-start 2026-07-23T12:00:00Z \
  --window-end 2026-07-23T13:00:00Z \
  --registry /data/bogmem/registry.json
```

Each returned neighbor is a local palace edge with its own evidence count,
weight, and first/last-seen timestamps. If the same actors occur in multiple
palaces, the response contains separately sourced hits. BogMem does not sum
their weights or materialize a cross-palace relationship.

## Coliseum MCP

Start a read-only registry-bound MCP process:

```bash
bogmem mcp --registry /data/bogmem/registry.json
```

The same read-only surface can be supervised as a Streamable HTTP service:

```bash
BOGMEM_MCP_TOKEN=replace-me bogmem mcp \
  --registry /data/bogmem/registry.json \
  --transport http \
  --listen http://127.0.0.1:7080
```

The registry-bound service advertises only:

- `bogmem_recall`
- `bogmem_graph_recall_neighbors`

This service is distinct from `bogmem mcp --palace PATH`, which owns one palace
and includes mutation tools unless started with `--read-only`. The Coliseum MCP
surface is always read-only.

The current implementation opens registered palaces sequentially in-process.
A process supervisor can later issue the same read-only requests concurrently
to one long-lived MCP process per palace while preserving this response
contract.
