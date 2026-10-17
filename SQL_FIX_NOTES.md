# SQL Sync diagnostics and retry improvements

## Scope

SQL inspection is **catalog-only**. `SqlMetadataReader` reads `sys.objects`, `sys.sql_modules`, `sys.columns`, `sys.foreign_key_columns`, `sys.sql_expression_dependencies`, `sys.indexes` and optionally `sys.databases`. It does not execute stored procedures or query business table rows.

## Changes

- Structured SQL logs show source ID, database, stage, object/column/dependency counts, and exception details in server logs. The HTTP result shows the failed stage, but does not leak connection strings or internal exception text.
- A SQL-specific `/sync?sourceId=azure-sql` call skips GitHub discovery and repository synchronization. `azure-sql` must match your configured SQL source ID.
- The SQL Source snapshot is written before the CodeMap latest pointer. If an earlier attempt failed after writing one snapshot, retries can repair missing pieces. This is not an atomic transaction across blobs.
- Temporary directory cleanup errors are logged without masking the main synchronization outcome.

## Run

Set `ConnectionStrings__SqlDatabase` and `SqlSources__Databases__0__...` App Service environment variables. For a first test use `DiscoverDatabases=false` and include `Database=...` in the connection string. Restart the App Service, then POST `/sync?sourceId=azure-sql` with `X-Sync-Key`.

The JSON `error` field now reports stages such as `read-metadata`, `build-codemap`, `read-snapshot`, `upload-source`, or `upload-codemap`.

## Limitations

- SQL lineage is still regex-based, not a full ScriptDom AST data-flow engine. It may miss or incorrectly infer complex T-SQL dependencies.
- Blob updates are not transactional across Source and CodeMap. A failed operation should be retried.
- This package has not been compiled or tested against Azure SQL in this environment; validate with `dotnet build` before deployment.
