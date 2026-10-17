# RDL / SSRS static lineage support (v10)

The sync analyzer now recognizes `.rdl` and `.rdlc` in any configured Git, GitHub-discovered, TFVC or filesystem source. This does **not** connect to the SSRS ReportServer database or automatically download deployed reports. To index reports, store them in a repository already indexed by Sync, or configure a separate `filesystem` source accessible to the Sync service:

```json
"Sync": {
  "Sources": [
    { "Id": "ssrs-reports", "Name": "SSRS Reports", "Type": "filesystem", "Location": "/app/rdl", "Enabled": true }
  ]
}
```

Use a different path on Windows; the folder must exist on the machine/container running CodeMapSync. The provided `RdlSamples/` directory can be copied there. SQL databases must also be configured and scanned to resolve stored procedure and table/view IDs. Run `/sync` with no `sourceId` for a full pass, or synchronize the SQL source first, then RDL source. Analyzer version was incremented to force reindexing unchanged repositories.

## Extracted nodes
- `rdl_report`, `rdl_datasource`, `rdl_dataset`, `rdl_field`, `rdl_parameter`, `rdl_variable`, `rdl_report_item`.
- Query `CommandType`, `CommandText`, query parameter expressions, DataField mapping, calculated fields, report/group filters, sorting, visibility, `Fields!`, `Parameters!`, `Variables!`, `ReportItems!`, `Lookup/LookupSet` dataset references and custom code warnings.
- Static `FROM`/`JOIN`/`EXEC` names and stored procedure `CommandText` are resolved against SQL CodeMap entries, with candidate status when ambiguous. Incoming links are generated from original directed relations.

## Important limitations
- RDL/SQL analysis is **static**. SQL result-column aliases are not yet mapped to underlying table columns by a full SQL AST parser. Dataset field -> SQL object links are evidence of dependency, not proof of exact column-level lineage.
- The RDL `Lookup` expression is analyzed as a dataset dependency; this is **not** proof that SSRS filtered rows at SQL execution time. `Hidden` is display visibility, not a dataset filter.
- Dynamic SQL, dynamic `CommandText`, expressions using custom VB/assemblies, shared `.rsd` datasets, external `.rds` data sources, subreport resolution and ambiguous field scopes require further inspection; do not assert confirmed lineage from a heuristic.
- Report definitions are XML and may contain sensitive connection information. Connection credentials are **not** copied into CodeMap attributes, but raw source snapshots remain sensitive. Keep Blob storage private and avoid embedding passwords in RDL files.
- RDL elements across RDL 2008/01 and 2016/01 are located by local XML name; full XSD validation is not performed.

## Example impact queries
1. `codemap_search_rdl("ForeignLargeCities")`, inspect the report/datasets and their outgoing relations.
2. `codemap_search_sql_objects("CitiesIstisna")`, `codemap_find_incoming_relations(id)` or `codemap_trace_dependencies(id, direction="incoming")`.
3. `codemap_search_rdl("Population")` to find fields and expressions. `codemap_read_source` can read RDL excerpts for Git/filesystem/TFVC snapshots.

Microsoft specification: https://learn.microsoft.com/en-us/openspecs/sql_server_protocols/ms-rdl/1d2b1998-e078-435f-8c03-a3d894a9843e

## v11: RDL dataset fields and SQL column search keys

`RdlSqlColumnResolver` runs **after** RDL-to-SQL object matching and **before** rebuilding incoming relations.

- Every RDL field with `DataField` receives `rdl_field_from_sql_result` to its matched SQL procedure/query target. This is a result-set provenance edge, **not** a claim that the output is a physical table column.
- For direct table/view queries, catalog columns (`column:Name` and `SearchKeys` containing `schema.object.Name`) produce `rdl_field_from_sql_column` edges.
- For procedures with a stored definition, the resolver inspects SELECT projections, resolves output aliases to source column references and SQL catalog search keys, and creates `rdl_field_depends_on_sql_column` edges. `DiscoveryMethod` records the exact qualified SQL column and projection expression; no standalone `sql_column` nodes are introduced.
- Incoming relations are indexed automatically, enabling `SQL table -> RDL field -> RDL report item` impact traversal. Follow `incomingRelations` to report items, then the report.
- Multiple result sets, dynamic SQL, complex CTEs, UNION branches and ambiguous aliases can require deeper parsing. Such cases should not be reported as certain column lineage. `rdl_field_from_sql_result` remains available as the reliable object-level path.

The SQL database's column search keys are **identifiers**, not proof of the column's contribution to a procedure result. The resolver only marks column lineage matched when it can find an output expression and a matching SQL catalog column in the referenced FROM/JOIN source.
