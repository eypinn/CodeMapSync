# v4: EF Core relations fix

- `GetTurkishCitiesAsync` using `dbContext.Cities` gets `uses_entity` -> City class and `maps_to` -> SQL table/view.
- Class `City` gets `maps_to` -> SQL table/view through explicit Table/ToTable/DbSet mapping or class-name convention.
- Singular/plural fallback applies only to class-name convention when exact match fails.
- Ambiguous database/schema names are not guessed; they are logged in Evidence.
- Analyzer version bumped to `orm-v4` so unchanged Git repos are reanalyzed.
- No business table data is read; no stored procedures are executed.
- Pkcs 10.0.13 remains in csproj.

Known limitation: syntax-only analysis does not verify DbContext receiver types and cannot fully resolve complex Fluent API.
