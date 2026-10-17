# EF Core and Dapper -> SQL CodeMap candidate links

This build adds syntax-only C# -> SQL references to the existing snapshot pipeline. No business-table SELECT or stored-procedure execution is performed by CodeMapSync. It reads C# source files and existing SQL metadata snapshots.

## EF Core

- Discovers `DbSet<TEntity>` properties in C# source files.
- Uses `[Table("TableName", Schema="dbo")]` where present.
- Recognizes fluent `modelBuilder.Entity<TEntity>().ToTable("TableName", "schema")` mappings.
- Otherwise assumes the DbSet property name as a **candidate** table name (conventions may differ).
- Adds `efTable:dbo.Cities` or `efTable:Cities` attributes on C# methods that access a recognized DbSet or `context.Set<TEntity>()`.
- Links to `sql_table` / `sql_view` entries with `efcore_maps_to` candidate relations.

## Dapper

- Recognizes common `Query*`, `Execute*`, `ExecuteScalar*`, `ExecuteReader*` method invocations.
- Supports direct string literal SQL and local variable SQL initialized by a string literal in the same method.
- Extracts object references from `FROM`, `JOIN`, `INTO`, `UPDATE`, `MERGE INTO`, `EXEC` statements into `dapperSql:*` attributes.
- Recognizes a literal procedure name with `CommandType.StoredProcedure` and emits `dapperProcedure:*`.
- Links to SQL CodeMap entries with `dapper_references` / `dapper_calls_procedure` candidate relations.

## Important limits

This is syntax-only analysis, not Roslyn semantic compilation or a full T-SQL parser. It does not resolve dynamic SQL, interpolated SQL, `CommandDefinition` construction, fluent mapping split across arbitrary helper methods, `IEntityTypeConfiguration<TEntity>` helper classes, `HasDefaultSchema`, complex EF inheritance, or database names from runtime connection strings. Matching SQL object names in multiple databases results in multiple **candidate** links; never treat these as confirmed. The existing SQL lineage analyzer remains regex-based.

## Run

1. Deploy the .NET 10 project and confirm Azure SQL metadata snapshots exist.
2. Trigger `/sync` **without** a `sourceId` to rebuild all C# maps (the analyzer version is now `orm-v2`) and create cross-source relations.
3. Later SQL-only syncs can load the existing C# maps from Blob `codemaps/*/latest.json` without GitHub discovery.
4. Search CodeMap entries for `efcore_maps_to`, `dapper_references`, and `dapper_calls_procedure` relations.

The existing `System.Security.Cryptography.Pkcs` package reference remains pinned at `10.0.13`.

No `dotnet build` was possible in the artifact creation environment; build and test before deployment.
