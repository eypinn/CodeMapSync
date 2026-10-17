# CodeMapSync (independent of MCP)

.NET 8 ASP.NET Core background worker + HTTP trigger. Synchronizes source into Azure Blob and produces project JSON maps. It does NOT execute code or call an LLM/Elasticsearch.

## Run

`dotnet restore && dotnet run`

Configure `Sync__StorageServiceUri=https://ACCOUNT.blob.core.windows.net` or `Sync__StorageConnectionString` and `Sync__TriggerApiKey` via App Service settings. Set sources enabled in appsettings or configuration overrides. POST `/sync?sourceId=customer-web` with `X-Sync-Key` header. `Sync:IntervalMinutes=0` disables periodic worker. Health: GET `/health`.

Git provider uses libgit2 locally, remote ref SHA check before clone. No commands on remote server. PAT supplied through named environment variable; SSH/NTLM/integrated enterprise authentication may require a custom provider. TFVC uses REST `CollectionUrl` + `$/Project/path`, downloads via HTTP and falls back to file content hash; test compatibility with your TFS version/API. Filesystem sources always copy+hash (no remote version).

## Blob paths

`sources/{sourceId}/manifest.json`, `sources/{sourceId}/snapshots/{contentHash}/{filePath}`, `maps/{sourceId}/{contentHash}/{projectId}.json`, `maps/{sourceId}/current.json`.

## Current analysis scope / accuracy

- C#: Roslyn syntax AST for types, methods, properties, HTTP method attributes; method invocation names are recorded as hints, NOT resolved call edges.
- HTML/CSHTML/JS/TS: simple regex discovery for literal `<label>` and `fetch`/`axios` URLs. This is not a full JavaScript/Razor parser.
- Same-file label->HTTP link is a *candidate*, NOT verified data flow.
- HTTP->endpoint match is candidate and only for literal route equality within the same source; does not yet handle controller route prefixes, parameters, host, gateways or cross-source linking.
- No service->entity->table lineage, no semantic model or control/data-flow tracing, no Python/Java/RDL analysis yet. No AI fallback.
- The sync manifest is published before map analysis. If analysis fails, `maps/.../current.json` remains at its previous successful revision, making stale map visible.
- Do not assume untrusted repositories are safe to execute; this service only reads source.

## Azure

Enable managed identity on App Service and assign `Storage Blob Data Contributor` scoped to storage/container. Ensure network route to TFS/Git/file shares (VNet integration or self-hosted worker), outbound firewall, DNS and permissions. Windows file shares may not be reachable from Azure App Service without suitable networking and credentials. Store tokens in Key Vault-backed App Settings, never in repo JSON. Use Always On or preferably an Azure Function timer / WebJob / Container Apps Job for reliable scheduling. Single-instance semaphore does not coordinate multiple App Service instances; add distributed lease before production scaling. Secure POST /sync with Entra auth or private ingress; X-Sync-Key is a starter only.

## Build verification

Not compiled here because dotnet SDK is unavailable in this runtime. Run `dotnet build` and integration tests with representative repositories before production.
ts, Razor Tag Helpers, or dynamic URLs. Labels are discovered; precise label-to-HTTP data lineage is not yet proven. Only C#, HTML/Razor, JS/TS patterns, and XML/RDL nodes are implemented. SQL, Java, Python and global cross-repository resolution are not yet implemented. Unmatched nodes are preserved, rather than hallucinated links. No LLM or Elasticsearch connection is necessary for this prototype.

Git remote branch revision check skips downloads when SHA matches. TFVC changeset check uses root item changesetId and may miss descendant-only updates on some servers: for correctness, do not rely on it as a complete tree revision without validating server behavior. File-system sources use full tree hashing.

`CodeMapDocument` includes `ProjectId`, `ProjectName`, `SourceId`, `SourceRevision`, `Entries`. Each entry includes `UniqueId`, `Key`, `SearchKeys`, `Label`, `FilePath`, 0-based `StartLineIndex`/`EndLineIndex`, `EndpointName`, `Route`, `HttpMethod`, database fields (unpopulated in phase 1), `RelatedMapObjectIds`, `Relations`, `ResolutionStatus`, `Evidence`, and `Attributes`.

The repository sync and CodeMap JSON are separate from the future MCP reader. Source snapshots are uploaded before the active manifest is switched; CodeMap version pointer is written before source manifest, so readers should validate both versions match.

**Validation status:** The environment used to assemble this starter has no .NET SDK. Build and runtime behavior have not been verified. Please build in a .NET 8 development environment and run integration tests before production deployment.
