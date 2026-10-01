// Events/ClashTaskQueue.cs  — v7.0 DEPRECATED
//
// FIX v7.0 (Plan Phase 6): ClashTaskQueue has been REMOVED.
// The queue was designed for producer/consumer async processing but:
//   - No code ever called EnqueueTargeted() / EnqueueSelection()
//   - No background thread ever called TryDequeue()
//   - It was dead code causing architectural confusion
//
// EventListener now runs targeted scans inline in OnIdling() which is
// already on Revit's main thread (required for Revit API calls).
// The ClashEngine is cached per session (LiveMonitorService._engine)
// so the per-call creation overhead is eliminated.
//
// This file is kept as a tombstone to prevent "file not found" if any
// external reference still imports this namespace.

namespace ClashResolveAI.Events
{
    // Intentionally empty — ClashTaskQueue removed in v7.0.
    // If you see a compile error referencing ClashTaskQueue, remove that reference.
}
