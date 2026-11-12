# Changelog

## 0.3.0 — 2026-11-12

- Added `ResourceStore.DeferNotifications` with independent copy-safe synchronous scopes. Nested reservations use the live ledger; final notifications, revision and history are coalesced after all scopes close.
- Added typed full `ResourceReservation.TryCommit(out failure)` and owner-checked `TryGetReservation(id, ownerId, out reservation)`, retaining existing overloads.
- Capture/replication rejects an open notification boundary. Authoritative restore invalidates old holds and scopes so late completion cannot affect restored balances.
- Hardened cross-store callback publication, mutation reentry and container destruction. Added cancellation, nested-payment, restore and standalone profiling coverage without new mandatory UI or gameplay dependencies.

## 0.2.0 — modular resource economy

- Exact Int64 quantities and fixed-point resource units; explicit cost arithmetic and rounding.
- Atomic transfers, exchanges, multi-store plans, capacity/weight/volume limits, filters and partial acceptance.
- Typed failures, owned/expiring/resizable/partial reservations, cancellation scopes and persistent active operations.
- Version 2 snapshots, version 1 loading, atomic multi-store restore and ID/precision migrations.
- Bounded diagnostics and frame-budgeted queues of complete transactions.
- Independent production/logistics, trading/credit, inventory, progression/research/prestige, networking and BigInteger assemblies.
- Server-owned command validation, replay watermarks, bounded full/delta replication and read-only client stores.
- Event-driven UI Toolkit resource/cost/capacity panel with automatic setup.
- Simplified inspectors, definition validation, 18-scenario Workflows sample and expanded player benchmarks.
- Breaking source change: quantity getters and ResourceAmount.Amount now return long. Existing serialized IDs and fields are preserved; migrate saved precision explicitly.

## 0.1.0 — first working version

- Stable ScriptableObject resource identities and duplicate ID validation.
- Independent stores and inspector-ready containers/costs.
- Atomic spending, copy-safe idempotent reservations and consistent events.
- Versioned snapshots with explicit unknown-ID and active-reservation policies.
- Basics sample, development-player benchmark, EditMode and PlayMode tests.
