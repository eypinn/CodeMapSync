# v6 – SQL CodeMap compactness and relationship diagnostics

- `efTable:Cities` is resolved using SQL `SearchKeys` and fallback `Key`.
- Unresolved or ambiguous mappings add `Evidence` to the C# entry for debugging.
- SQL table/view columns are stored as `Attributes["column:<name>"]` and qualified column search keys; there are no separate `sql_column` entries.
- No `sql_index` entries, and no FK or identity entries. FK metadata is retained only as concise table evidence. Identity is retained as a column attribute.
- SQL schema tokens alone are not search keys.
- SQL analyzer version `sql-compact-v6` and C# analyzer `orm-v6` force snapshot refresh.
- Cross-source pass logs map, SQL object, C# entry and EF candidate counts.

After deployment, run full sync and check the `Evidence` array on the City class if no SQL relation is generated. If SQL candidate count is zero, SQL snapshot loading is the issue, not string matching.
