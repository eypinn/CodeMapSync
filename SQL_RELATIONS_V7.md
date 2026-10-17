# SQL Relations v7

- SQL match uses both `Key` and `SearchKeys`, with the SQL `Kind` determining whether an EF mapping can target table/view, a procedure call targets SP, or a function reference targets function.
- `efTable:Cities` matches SQL `SearchKeys=["Cities","dbo.Cities"]` even if `Key` is `dbo.Cities`.
- C# raw SQL `EXEC dbo.Proc` and Dapper `CommandType.StoredProcedure` produce `calls` relations; SQL functions `dbo.Func(...)` produce `references` relations. SQL query FROM/JOIN references target table/view/function candidates.
- Ambiguous names in multiple databases produce separate *candidate* links and evidence, not silent suppression.
- SQL-MATCH summary logs count references, matches, missing names, ambiguities, and SQL objects. Missing/ambiguous evidence is persisted even if no links are created.
- SQL table columns stay in parent table attributes; indexes/FKs/identity are not standalone map entries.
- SQL is not executed; dynamic SQL and non-literal SQL arguments are not fully resolved.
- `orm-v7` forces C# reanalysis; run full sync after deployment.
