# SQL metadata + lineage extension (v2)

## Storage layout

Each configured SQL database gets its own `sourceId`, source snapshot and CodeMap.

- `sources/{sourceId}/manifest.json` — `SourceType=sql`, snapshot revision and file list.
- `sources/{sourceId}/snapshots/{revision}/schema.json` — tables, columns, FK, indexes, dependencies and object catalog.
- `sources/{sourceId}/snapshots/{revision}/objects/{schema}/{name}_{type}.sql` — stored module definitions when visible.
- `codemaps/{sourceId}/{revision}/map.json` — same `CodeMapDocument` and `CodeMapEntry` structure as C#.
- `codemaps/{sourceId}/latest.json` — latest pointer.

Source files contain SQL definitions and should remain in a **private** container. The SQL connection string is never copied into the snapshot.

## Configuration

`appsettings.json` enables `SqlSources:Databases[0]` with `DiscoverDatabases=true`. Supply the actual connection string in Azure App Service Configuration under `ConnectionStrings:SqlDatabase` (or `ConnectionStrings__SqlDatabase` as an environment variable). SQL discovery reads `sys.databases`; the identity needs sufficient metadata visibility and CONNECT permission to each database. Azure SQL Database may not expose all databases from a normal database connection. In that case use **one configured source per database** with `DiscoverDatabases=false`, and a connection string with that database's `Initial Catalog`.

A discovered database `Accounting` from source `azure-sql` is saved as `azure-sql-Accounting`. Database names are normalized to source IDs; avoid collisions in normalized names.

Call `POST /sync` with header `X-Sync-Key` and no `sourceId` to sync everything; pass `?sourceId=azure-sql` for SQL group or `?sourceId=azure-sql-Accounting` for one discovered DB. The periodic worker also invokes full sync.

## Relationships

- `foreign_key`: table -> referenced table, metadata-confirmed.
- `sql_references`: SQL module -> object, catalog-confirmed (does **not** establish read vs write).
- `sql_calls`: stored procedure -> stored procedure, catalog dependency.
- `sql_reads`, `sql_writes`: procedure -> table, candidate from statement-level static analysis.
- `data_flows_to`: source table -> destination table, candidate from statement-level SQL lineage.
- `joined_with`: source table -> another source table that contributes to the **same target write**, candidate.

The analyzer tracks simple `INSERT INTO ... SELECT ... FROM/JOIN`, `UPDATE ... FROM/JOIN`, `MERGE ... USING`, and `SELECT ... INTO #temp ... FROM/JOIN` patterns, propagating temporary-table sources across subsequent statements. It does not execute SQL. These are **static candidates** and should not be reported as runtime facts. Dynamic SQL, branches, aliases, recursive CTEs, multiple scopes, nested subqueries, table variables, indirect SP calls, `INSERT EXEC`, cross-database names and complex control flow need deeper parsing. For production-grade lineage, replace the conservative tokenizer with a T-SQL AST parser (ScriptDom) and scope-aware dataflow analysis.

Example:

```sql
SELECT a.Id, b.Name INTO #stage FROM dbo.Table1 a JOIN dbo.Table2 b ON b.Id=a.Id;
INSERT INTO dbo.Target(Id, Name)
SELECT s.Id, s.Name FROM #stage s JOIN dbo.Table3 c ON c.Id=s.Id;
```

Expected candidate edges: `Table1 -> Target`, `Table2 -> Target`, `Table3 -> Target`, plus `joined_with` pairs among `Table1`, `Table2`, `Table3` scoped to Target. A separate `foreign_key` edge is retained if declared in SQL Server.

## Validation

1. `dotnet restore && dotnet build` (requires .NET 10 SDK; not available in the packaging environment).
2. Check `/sync` response for `azure-sql-<database>` results; inspect App Service logs for errors.
3. Verify both `sources/.../manifest.json` and `codemaps/.../latest.json` are created.
4. Test a procedure with temp-table and JOIN lineage; review `candidate` status and evidence.
5. Confirm the Agent/MCP tools index the new SQL source IDs; MCP server may require adjustments if it lists only repository sources.

**Known limitations:** A syntactic heuristic is not equivalent to exact column-level lineage. This release deliberately marks inferred lineage as `candidate` and avoids executing business queries or changing database objects.
