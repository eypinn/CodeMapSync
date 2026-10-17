# EF Core Fluent API SQL mapping (v8)

## Changes
- Reads literal `modelBuilder.Entity<T>().ToView("View", "schema")` and `.ToTable("Table", "schema")`, including method chains such as `.HasNoKey().ToView(...)`.
- Supports `var entity = modelBuilder.Entity<T>(); entity.ToView(...)` and `modelBuilder.Entity<T>(entity => entity.ToView(...))`.
- Produces `efView:dbo.CitiesVw` or `efTable:dbo.Cities` attributes on entity and EF DbSet-using method entries.
- Only EF entities discovered through DbSet, `[Table]` or Fluent API receive SQL mappings; other classes do not get guessed mappings.
- SQL resolver recognizes `kind=sql_object` with `attributes.objectType` (U/V/P/FN/IF/TF/FS/FT), plus legacy `sql_table`, `sql_view`, etc.
- `efView:` resolves only against SQL views, not tables. Matches remain static-analysis candidates; unique qualified matches have strong match strength.
- Analyzer version bumped to `orm-v8` to force re-analysis on next sync.

## Verification
Build locally with `dotnet restore` and `dotnet build -c Release`. .NET SDK was not available in the authoring environment; no compilation or integration test was run.

## Limitations
- Only literal SQL object names are resolved; dynamic names and external helper configurations may not be discovered.
- This is syntax-based analysis, not a Roslyn semantic compilation. Same-name types across namespaces and same-name DbSets across contexts can be ambiguous.
- The `CityView`/`CitiesVw` change in the API repository must be deployed and synced separately. This ZIP updates only CodeMapSync, not the API or database.
