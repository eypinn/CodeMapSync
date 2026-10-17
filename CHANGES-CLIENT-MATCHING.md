# HTTP client extraction and confidence matching

- `CodeMapEntry.RequestUri`: outbound HTTP request target; outbound entries now have `Kind = "client"`.
- `CodeMapRelation.MatchStrength`: `strong` or `weak`; `MatchScore`: 1.0 or 0.70. `RelatedMapObjectIds` remains `List<string>` for compatibility.
- Cross-project resolver links client -> endpoint, caller method -> client and caller method -> endpoint.
- Strong: identical path (case-insensitive, optional leading slash, matching HTTP method), including `{id}` template segments.
- Weak: exact path after removing one leading `api` segment from either side; score 0.70.
- Recognizes common HttpClient Get/Post/Put/Delete/Patch methods, JSON extension calls, local constant string variables, `new Uri(...)`, and `SendAsync(new HttpRequestMessage(...))` plus simple local variable declarations.
- Static extraction is not exhaustive: URL composition via configuration, method parameters, helpers, typed clients, custom wrappers and complex expressions requires deeper semantic/dataflow analysis. Unknown URLs are not fabricated.
- IMPORTANT: Deploying only CodeMapSync does not reindex unchanged repositories. Rebuild both repository CodeMaps, or version the analyzer and force a one-time reanalysis.
- Existing persisted `http_request` entries remain readable by the resolver.
- Not compiled here: .NET SDK is not installed in the environment.
