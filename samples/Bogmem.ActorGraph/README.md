# Weighted actor graph

This sample is the seam for event-processing and social-signal systems.
The upstream workflow decides what constitutes co-activity and emits stable,
normalized observations. `BogMem.Graph` projects those events into a weighted
account-to-account graph for a fixed window, supports neighborhood recall, and
assigns connected communities with deterministic Leiden-style detection.

```bash
dotnet run --project samples/Bogmem.ActorGraph
```

Use `ActorGraphWindow` when the caller deliberately keeps each analysis window
in memory. Write the same `CoActivityObservation` values to
`BogDbActorGraphStore` when the palace must retain and replay evidence across
windows. Reusing an observation ID with identical normalized content is a
no-op; reusing it for different content is rejected.
