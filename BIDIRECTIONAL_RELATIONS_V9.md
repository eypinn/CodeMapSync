# Bidirectional CodeMap relations (v9)

- `relations` stays the canonical directed outgoing edge list.
- New `incomingRelations` stores reverse references with original `relationType`, `status`, `discoveryMethod`, `matchStrength`, `matchScore` and `sourceId` (the original entry's UniqueId).
- `IncomingRelationIndexer` rebuilds the reverse index after cross-project HTTP and C#/SQL matching, using all loaded maps; it does not fabricate missing relationships.
- Reverse indices are persisted into each affected CodeMap snapshot, including when outgoing counts are unchanged. Index generation is idempotent and sorted.
- Analyzer version `orm-v9-incoming` causes C# sources to be reanalyzed once.
- Existing map readers ignoring unknown JSON fields remain compatible.
- For a caller not detected by existing analyzers, reverse edges cannot be generated. Razor/MVC view-to-method discovery is a separate enhancement.
