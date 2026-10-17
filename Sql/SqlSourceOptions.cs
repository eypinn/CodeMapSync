namespace CodeMapSync.Sql;

public sealed class SqlSourceOptions
{
    public List<SqlDatabaseSource> Databases { get; set; } = [];
}
public sealed class SqlDatabaseSource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    // Connection string must be supplied via ConnectionStrings:KEY, not copied into source metadata.
    public string ConnectionStringName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    // When true, query sys.databases and create a separate SQL source per accessible DB.
    // Requires server-level visibility; Azure SQL may expose only accessible databases.
    public bool DiscoverDatabases { get; set; } = false;
    public List<string> IncludeDatabases { get; set; } = ["*"];
    public List<string> ExcludeDatabases { get; set; } = ["master", "model", "msdb", "tempdb"];
}
