# Changelog

All notable changes to BogMem will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
While BogMem is in preview, its public APIs and persisted formats may change
between releases.

## [Unreleased]

### Added

- Weighted temporal graph memory with durable evidence, neighborhood and
  traversal recall, and deterministic community detection.
- A palace runtime and durable Coliseum registry for discovering and routing
  requests to isolated memory stores.
- Read-only Coliseum recall across palaces for drawers and graph neighbors,
  with provenance and partial-failure reporting.
- A stateless MCP Streamable HTTP transport that shares the stdio MCP
  dispatcher, including health checks, bearer authentication, Origin and CORS
  controls, and protocol-version validation.
- A `BogMem.Slices` NuGet package for applications that embed palace runtime,
  storage, recall, registry, and MCP services directly.
- An actor-graph sample and service-integration guidance for real-time memory
  ingestion and retrieval.

### Changed

- Upgraded `BogDB.Core` to 1.4.0 for repaired graph merge behavior and the
  accompanying storage and query fixes.
- Removed the standalone parity harness from the product dependency graph while
  retaining golden-corpus replay in the CLI and test suite.
- Extended MCP graph observations with routing guards and lineage and
  idempotency metadata.
- Added the ASP.NET Core shared framework to the CLI package for the HTTP MCP
  server.

### Fixed

- Relationship merges now use BogDB's repaired native Cypher path and are
  covered by same-runtime graph-visibility regression tests.

## [0.1.0-preview.2] - 2026-07-23

### Changed

- Moved NuGet publication to trusted publishing.
- Improved release validation and documentation for tag-driven and manually
  dispatched package publication.

## [0.1.0-preview.1] - 2026-07-23

### Added

- Persistent BogDB-backed drawer storage exposed through the CLI and stdio MCP
  server.
- Hybrid recall using native HNSW semantic search, BM25 full-text search, local
  `all-MiniLM-L6-v2` embeddings, and persisted model metadata.
- Git-aware project mining, configuration routing, and safe project sync.
- Quick-start and biological-molecule API samples.
- NuGet library packages and a .NET CLI tool package.
- A frozen MemPalace compatibility corpus and deterministic cross-platform
  parity test harness.
- An embedded FreeBSD `web2` spelling corpus with source and ownership
  attribution.
- Apache-2.0 licensing and initial release automation.

[Unreleased]: https://github.com/BeyondOrdinary/BogMem/compare/v0.1.0-preview.2...HEAD
[0.1.0-preview.2]: https://github.com/BeyondOrdinary/BogMem/compare/v0.1.0-preview.1...v0.1.0-preview.2
[0.1.0-preview.1]: https://github.com/BeyondOrdinary/BogMem/releases/tag/v0.1.0-preview.1
