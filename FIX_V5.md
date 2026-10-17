# v5 fixes

- SQL object SearchKeys now contain `Cities`, `dbo.Cities` (no standalone `dbo`).
- C# → SQL resolver matches SearchKeys as well as Key; schema-qualified names match only fully qualified aliases.
- Existing older SQL snapshots without SearchKeys still match via Key and its unqualified object name.
- Class-to-SQL candidates attach to C# class entries with normalized file paths and unique-name fallback.
- Singular/plural fallback for convention-based class mappings; ambiguous SQL matches are not linked.
- Analyzer version `orm-v5` triggers C# re-analysis. **SQL SearchKeys change requires SQL resync**.
- SQL business data and stored procedures are never executed.
