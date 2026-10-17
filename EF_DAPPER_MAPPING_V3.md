# EF Core / Dapper / SQL cross-source mapping v3

- Entity class -> SQL table or view through `[Table]`, fluent `.ToTable`, `DbSet<T>` or `Set<T>`.
- SQL name is matched against SQL snapshot `schema.object` or uniquely by object name.
- Dapper literal SQL `FROM`, `JOIN`, `INTO`, `UPDATE` links to table/view; literal `EXEC` and `CommandType.StoredProcedure` link to stored procedure.
- EF Core raw SQL `EXEC` references are also resolved to stored procedures.
- Existing CodeMap `Relations` and `RelatedMapObjectIds` are used; no new JSON property is required.
- Names occurring in multiple databases/schemas are intentionally NOT linked until a database-specific mapping is available.
- Links are `candidate` and do not imply execution or proven data lineage.
- Does not execute stored procedures or read business rows; analyzes C# source and SQL system metadata snapshots only.
- `orm-v3` forces a rebuild of C# maps on the next sync; sync all sources to update both sides.
- Limitations: semantic EF conventions, complex fluent configuration, interpolated/dynamic SQL, variable SQL, and per-project connection-string routing are not fully resolved.
- Build: `dotnet restore; dotnet build -c Release` (not tested in this environment).
