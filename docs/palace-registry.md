# Palace registry

`PalaceRegistry` is the durable routing catalog for a Coliseum. It maps a
palace's stable manifest ID and unique name to its current BogDB path. It does
not combine palace databases or own MCP processes.

```bash
bogmem registry register \
  --palace /data/palaces/undertow \
  --registry /data/coliseum/registry.json

bogmem registry register \
  --palace /data/palaces/artifacts \
  --name artifacts \
  --registry /data/coliseum/registry.json

bogmem registry list --registry /data/coliseum/registry.json
bogmem registry resolve undertow --registry /data/coliseum/registry.json
bogmem registry unregister artifacts --registry /data/coliseum/registry.json
```

The registry path resolves from `--registry`, then
`BOGMEM_REGISTRY_PATH`, then `~/.bogmem/registry.json`.

`register` opens the palace and copies identity, schema, creation time, and
capabilities from its manifest. Registering a path without a manifest creates
one in the same way as `bogmem init`; pass `--name` when creating a palace this
way. Existing palace names remain immutable.

## Routing guarantees

- Palace IDs and names are unique in one registry.
- Database paths are absolute and unique in one registry.
- Re-registering a known palace refreshes its metadata and path while
  preserving its original registration time. This is how a moved palace is
  repaired.
- `OpenPalace` resolves by exact ID or exact name, opens the independent
  runtime, and verifies that its manifest ID matches the registered ID.
- Registry mutations use an exclusive writer lock and replace the JSON file
  atomically. Readers see either the previous complete catalog or the next
  complete catalog.
- Removing an entry never removes or edits its palace database.

```csharp
using Bogmem.Slices.Runtime;

var registry = new PalaceRegistry("/data/coliseum/registry.json");
var route = registry.Resolve("undertow");

using var palace = registry.OpenPalace(route.PalaceId);
var status = palace.Status();
```

The JSON file is deliberately operational metadata rather than memory. It can
be backed up, inspected, and rebuilt from palace manifests.

## Coliseum boundary

The registry is the first Coliseum primitive. A later supervisor can use it to:

1. Start one MCP process per registered palace.
2. Health-check and restart each process independently.
3. Route one FTT observation envelope to one or more palace IDs.
4. Aggregate read-only results while preserving the source palace ID.

Cross-palace edges and a shared multi-palace BogDB remain out of scope. A
registry entry identifies a routing destination; it does not weaken the
one-runtime/one-palace isolation boundary.
