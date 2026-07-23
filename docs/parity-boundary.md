# Parity is evidence, not the product specification

BogMem preserves the MemPalace golden corpus and compatibility slices so we can
measure what the port retained. Exact parity does not mean every inherited
behavior is desirable.

The repository therefore has two explicit layers:

- **Compatibility layer:** frozen vectors and focused ports reproduce the
  pinned MemPalace behavior, including awkward edge cases where changing the
  result would invalidate the oracle.
- **Product layer:** `IMemoryStore`, BogDB persistence, CLI commands, and the
  live MCP server expose only implemented behavior. They may deliberately
  correct unsafe or misleading legacy contracts.

Every product divergence should have a test and user-facing documentation.
Sync is the first concrete example:

- MemPalace could infer roots for destructive cleanup; BogMem requires an
  explicit project root for apply.
- A `source_file` string alone could make a drawer eligible for deletion;
  BogMem records ownership and protects `manual` and migrated `legacy` drawers.
- MemPalace carried a custom partial Git-ignore matcher; BogMem asks Git itself
  and refuses deletion when ignore status cannot be verified.
- BogMem deletes the exact previewed drawer IDs atomically rather than deleting
  every record that happens to share source metadata.

Project room routing is another:

- BogMem accepts both current `mempalace.yaml` and legacy `mempal.yaml` names.
- Explicit API/CLI wing and room values override configuration.
- Path classification uses token boundaries, so a `view` keyword does not
  classify an `interviews` directory.
- MemPalace documentation said filename keywords were considered while its
  implementation checked only room names. BogMem implements the documented
  behavior and has a regression test for a filename keyword such as
  `service-api.cs`.

Semantic embedding is also a product integration rather than an exact-vector
parity claim:

- The frozen model inventory records Chroma's pinned MiniLM ONNX export
  (`sha256:4f148b...`), whose bytes are intentionally not vendored.
- The product uses the maintained `ElBruno.LocalEmbeddings` MiniLM export
  (`sha256:6fd5d7...`) and pins that hash before inference.
- Both produce normalized 384-dimensional `all-MiniLM-L6-v2` sentence
  embeddings, but BogMem records the product model identity on every drawer
  and re-embeds on a mismatch instead of claiming byte-identical legacy
  vectors.

When a legacy bug is found, add a regression test at the product boundary. Keep
the oracle unchanged unless the project intentionally chooses a new upstream
pin and regenerates the corpus.
