# GitHub public account discovery

Configure a source with `Type: github` and `Discovery.Owner`. This discovers **all public** repositories under the owner (user or organization), then syncs each separately through the existing Git source provider. No GitHub credential is needed for public repositories. Private repositories are excluded even if GitHub ever returns them.

- `IncludeRepositories`: wildcard name patterns (`*`, `CodeMap*`); `ExcludeRepositories`: exclusion patterns.
- `OwnerType`: `user` or `organization`.
- `MaxRepositories`: upper limit to avoid accidentally indexing too many repos. A warning is logged when reached.
- `sourceId=eypinn-public` syncs all discovered repositories in the group; omitting `sourceId` syncs all configured sources.
- Each repository receives a deterministic `sourceId` based on GitHub `full_name`, so it has its own Blob manifest, snapshot and CodeMap.
- GitHub API discovery is unauthenticated, so it is subject to low rate limits (normally 60 requests/hour/IP). For many repos or frequent polling, add authenticated GitHub API support later.
- Git remote commit check and clone are performed on the CodeMapSync worker, not on GitHub servers.

## Run

1. Create the private `codemap-source` Blob container.
2. Give the running identity `Storage Blob Data Contributor` on that container. For local use, sign in using `az login` and grant the same role to that account.
3. Configure `Sync:StorageServiceUri` and `Sync:TriggerApiKey` (set secret via environment variable `Sync__TriggerApiKey`).
4. Run `dotnet restore`, `dotnet build`, `dotnet run`.
5. `POST /sync?sourceId=eypinn-public` with header `X-Sync-Key: <secret>`.

## Limitations

This implementation discovers public repositories at the account root, not folders inside repositories. GitHub does not provide a folder containing multiple independent repositories. The existing Roslyn/Web analyzers remain as in v2; full cross-repository graph resolution is not yet implemented. A local semaphore prevents duplicate runs **within one process only**, not across App Service instances. GitHub API/network/Storage calls require live integration testing.
