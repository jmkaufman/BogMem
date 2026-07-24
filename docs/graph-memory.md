# Graph memory

The original MemPalace `KnowledgeGraphStore` is a compatibility slice: a
SQLite-backed temporal subject-predicate-object table. It is useful for proving
the port, but it is not the product graph floor. It cannot represent a weighted
co-activity event without flattening evidence, and it has no graph analysis.

`BogMem.Graph` supplies that floor without turning BogMem into an ingest
framework.

## Boundary

The producer owns domain interpretation:

- Which identifiers represent actors.
- What activity makes actors related.
- How event weight is calculated.
- Which source, workflow, or detector provides the stable observation ID.
- Which FTT workflow, run, artifact, and signal type produced the observation.
- Which interval forms an analysis window.

BogMem owns graph memory:

- Replay-safe temporal observations.
- Actor and observation nodes with participation relationships in BogDB.
- Weighted undirected pair projection for a selected window.
- Neighborhood recall with evidence count and first/last timestamps.
- Deterministic connected community detection.

This keeps file parsing, office-document extraction, account normalization, and
transfer-specific policy outside the package.

## Event model

```text
Actor --PARTICIPATED_IN--> Observation <--PARTICIPATED_IN-- Actor
```

An observation involving `n` distinct actors contributes its weight to each of
the `n(n-1)/2` actor pairs. Replaying the same normalized observation ID is a
no-op. Reusing its ID with different actors, time, weight, or context is an
error. Optional provenance is normalized into the idempotency fingerprint. The
default maximum is 1,024 actors per observation and can be changed explicitly
when constructing the window or store.

The persistent representation keeps event evidence instead of overwriting one
aggregate relationship. Consequently a caller can request different time
windows without rebuilding the palace.

## Community detection

`LeidenCommunityDetector` reports the versioned algorithm ID
`leiden-deterministic-v1`. It operates on weighted undirected snapshots and
performs:

1. Modularity-based local moving at a configurable resolution.
2. Connectivity refinement of every proposed community.
3. Weighted graph aggregation and repetition until stable.

Stable node ordering replaces the randomized scheduling used by common Leiden
implementations, making identical windows reproducible in tests and workflow
replays. The result is Leiden-style and prevents disconnected communities, but
community IDs and borderline assignments are not expected to match another
implementation bit for bit.

## Undertow-shaped use

For an in-memory-per-window consumer:

```csharp
var window = new ActorGraphWindow(windowStart, windowEnd);
foreach (var eventRecord in normalizedEvents)
{
    window.Observe(new CoActivityObservation(
        eventRecord.Id,
        eventRecord.OccurredAt,
        eventRecord.AccountIds,
        eventRecord.Weight,
        eventRecord.SignalKind));
}

var snapshot = window.Snapshot(minimumEdgeWeight: 0.25);
var partition = new LeidenCommunityDetector().Detect(
    snapshot,
    new CommunityDetectionOptions(Resolution: 1.0));
```

The producer can omit BogDB entirely for this path. A BogMem service can write
the exact same observations to `BogDbActorGraphStore`, then materialize and
analyze any historical window later.

## Migration

The parity `KnowledgeGraphStore` remains intact so the golden corpus stays
meaningful. New product work should use `BogMem.Graph`. A later migration can
map each temporal triple to an observation and two participating entity actors,
but it should preserve the original predicate as context rather than pretending
that every semantic triple is co-activity.
