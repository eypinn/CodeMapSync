# Cross-project HTTP relationship resolution (.NET 10)

- CSharpAnalyzer recognizes literal `HttpClient.GetFromJsonAsync`, `GetAsync`, `PostAsync`, etc. as `http_request` entries, preserving caller ID.
- It recognizes direct Minimal API `MapGet/MapPost/MapPut/MapDelete/MapPatch` with literal paths as `endpoint` entries.
- CrossProjectRelationshipResolver connects the calling `method` to endpoints in other projects using the existing `Relations` and `RelatedMapObjectIds` fields; no Edges model.
- After each sync, all discovered sources' current CodeMaps (including `unchanged`) are read from Blob and cross-linked.
- HTTP method and path are matched. Multiple matches are allowed; links have `candidate` status.

## Limitations
- Static extraction currently supports literal routes only; no variable URLs, `SendAsync`, or `MapGroup` prefixes.
- Existing previously indexed unchanged CodeMaps will not contain the newly discovered C# HTTP requests or Minimal API endpoints until the repositories are re-analyzed. To force one-time reanalysis, remove the relevant `sources/<id>/manifest.json` blobs (back them up first), then sync again. Do not remove source snapshots.
- SourceRevision and source map IDs are preserved.
- No SDK is installed in the build environment; run `dotnet build` locally to validate.
