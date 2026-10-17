# Build fixes

- RepositorySyncService.cs: use ToList() for both branches of selectedSources conditional, fixing CS0173 and likely downstream CS8130.
- CodeMapSync.csproj: add System.Security.Cryptography.Pkcs 10.0.13 explicit reference.

Run: dotnet restore && dotnet build -c Release

Not compiled in this environment (dotnet SDK unavailable).
