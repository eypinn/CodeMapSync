# SQL metadata integration (CodeMapSync)

## Overview

This extension imports Azure SQL / SQL Server **catalog metadata** into the existing CodeMap JSON format and Blob layout. It does not expose direct SQL connectivity to the Foundry Agent. The existing CodeMapMcp may discover these snapshots through its `codemaps/{sourceId}/latest.json` discovery mechanism; confirm MCP search and list behavior after deployment.

Modules:
- `Sql/SqlSourceOptions.cs`: independently configured SQL database sources.
- `Sql/SqlMetadataReader.cs`: read-only catalog queries (`sys.objects`, `sys.columns`, `sys.foreign_key_columns`, `sys.sql_expression_dependencies`, `sys.indexes`, `sys.sql_modules`).
- `Sql/SqlCodeMapBuilder.cs`: maps SQL tables, views, procedures, functions, triggers, columns, indexes, and dependencies to `CodeMapDocument`.
- `Sql/SqlSyncService.cs`: database snapshot synchronization to Blob.
- `Sql/CSharpSqlReferenceEnricher.cs`: conservative C# literal SQL references for supported EF Core methods.
- `Sql/CSharpSqlRelationshipResolver.cs`: cross-source candidate links for explicit C# SQL references.

## Configure

Add a `SqlSources` section, following `sqlsources.example.json`, to App Service configuration or appsettings. Use **ConnectionStrings:SqlDatabase** in Azure App Service Configuration (not source control). Connection string can use Microsoft Entra authentication or SQL authentication, but do not commit passwords.

Assign SQL account the minimum metadata visibility required (`VIEW DEFINITION` where applicable) and catalog read access; no write permissions. Blob identity needs Storage Blob Data Contributor. Private Blob storage is recommended because SQL module definitions can contain sensitive logic or literals.

`POST /sync` with the existing `X-Sync-Key` synchronizes both repositories and configured SQL databases. `POST /sync?sourceId=azure-sql-main` synchronizes the selected SQL source; it still executes the cross-source relation pass. Background periodic synchronization also covers SQL sources.

## Important behavior and limitations

- Database source IDs must be unique across repository and SQL sources.
- Existing repository manifests are automatically reanalyzed once when analyzer version changes to `sql-v1`, even if Git revision is unchanged.
- SQL snapshots use `codemaps/{sourceId}/{revision}/map.json` and `codemaps/{sourceId}/latest.json` like existing C# snapshots. SQL snapshot content is **metadata**, not table rows.
- SQL definitions are stored in `attributes.definition` in **private** Blob. The MCP server should filter this field out of normal search responses to reduce token consumption; reveal it only in a targeted detail query.
- Cross-source SQL links are **candidates** because matching a name alone does not prove which database is used. Schema-less names may be ambiguous; all plausible candidates are retained.
- Only direct catalog dependencies are considered verified metadata relationships. Dynamic SQL, temporary objects, cross-database references and runtime data flow are not fully resolved.
- C# SQL extraction initially recognizes literal SQL in common EF Core `FromSql*` / `ExecuteSql*` methods. ADO.NET commands, interpolated SQL, DbSet-to-table mappings and ORM expression trees need dedicated follow-up analyzers.
- SQL module definitions may be unavailable due to encryption or metadata permissions.
- **Not build-verified here**: .NET SDK is not available in this environment. Run `dotnet restore && dotnet build` on .NET 10, then perform an integration test against an Azure SQL test database.
