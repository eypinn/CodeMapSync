using Microsoft.Data.SqlClient;
using System.Data;

namespace CodeMapSync.Sql;

// Catalog-only access: no business table rows and no SQL execution of stored modules.
public sealed class SqlMetadataReader
{
    public async Task<IReadOnlyList<string>> ListDatabasesAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var rows=await QueryAsync(connection, "SELECT name FROM sys.databases WHERE state = 0 ORDER BY name", ct);
        return rows.Select(r=>Convert.ToString(r["name"]) ?? "").Where(n=>n.Length>0).ToArray();
    }

    public async Task<SqlCatalog> ReadAsync(string connectionString, CancellationToken ct)
    {
        var result = new SqlCatalog();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        result.DatabaseName = connection.Database;
        result.ServerName = connection.DataSource;
        result.Objects = await QueryAsync(connection, """
            SELECT s.name AS schema_name, o.name AS object_name, o.type AS object_type,
                   o.object_id, o.modify_date, m.definition AS sql_definition
            FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
            LEFT JOIN sys.sql_modules m ON m.object_id=o.object_id
            WHERE o.is_ms_shipped=0 AND o.type IN ('U','V','P','FN','IF','TF','TR')
            ORDER BY s.name,o.name
            """, ct);
        result.Columns = await QueryAsync(connection, """
            SELECT c.object_id,s.name AS schema_name,o.name AS object_name,c.name AS column_name,
                   c.column_id,t.name AS data_type,c.max_length,c.precision,c.scale,c.is_nullable,
                   c.is_identity,c.is_computed
            FROM sys.columns c JOIN sys.objects o ON o.object_id=c.object_id
            JOIN sys.schemas s ON s.schema_id=o.schema_id
            JOIN sys.types t ON t.user_type_id=c.user_type_id
            WHERE o.is_ms_shipped=0 AND o.type IN ('U','V')
            ORDER BY c.object_id,c.column_id
            """, ct);
        result.ForeignKeys = await QueryAsync(connection, """
            SELECT fk.name AS constraint_name,ps.name AS source_schema,pt.name AS source_table,
                   pc.name AS source_column,rs.name AS target_schema,rt.name AS target_table,
                   rc.name AS target_column,fkc.constraint_column_id AS ordinal,
                   fk.delete_referential_action_desc AS delete_action
            FROM sys.foreign_key_columns fkc
            JOIN sys.foreign_keys fk ON fk.object_id=fkc.constraint_object_id
            JOIN sys.tables pt ON pt.object_id=fkc.parent_object_id
            JOIN sys.schemas ps ON ps.schema_id=pt.schema_id
            JOIN sys.columns pc ON pc.object_id=pt.object_id AND pc.column_id=fkc.parent_column_id
            JOIN sys.tables rt ON rt.object_id=fkc.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id=rt.schema_id
            JOIN sys.columns rc ON rc.object_id=rt.object_id AND rc.column_id=fkc.referenced_column_id
            ORDER BY fk.name,fkc.constraint_column_id
            """, ct);
        result.Dependencies = await QueryAsync(connection, """
            SELECT d.referencing_id,rs.name AS referencing_schema,ro.name AS referencing_object,
                   d.referenced_id,d.referenced_schema_name,d.referenced_entity_name,
                   d.referenced_database_name,d.is_ambiguous
            FROM sys.sql_expression_dependencies d
            JOIN sys.objects ro ON ro.object_id=d.referencing_id
            JOIN sys.schemas rs ON rs.schema_id=ro.schema_id
            WHERE ro.is_ms_shipped=0
            """, ct);
        result.Indexes = await QueryAsync(connection, """
            SELECT o.object_id,s.name AS schema_name,o.name AS object_name,i.name AS index_name,
                   i.is_primary_key,i.is_unique,i.type_desc AS index_type,c.name AS column_name,
                   ic.key_ordinal,ic.is_included_column
            FROM sys.indexes i JOIN sys.objects o ON o.object_id=i.object_id
            JOIN sys.schemas s ON s.schema_id=o.schema_id
            LEFT JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            LEFT JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE o.is_ms_shipped=0 AND o.type IN ('U','V') AND i.index_id>0
            ORDER BY o.object_id,i.index_id,ic.key_ordinal,ic.index_column_id
            """, ct);
        return result;
    }

    private static async Task<List<Dictionary<string, object?>>> QueryAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 90 };
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i=0;i<reader.FieldCount;i++)
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
}

public sealed class SqlCatalog
{
    public string DatabaseName { get; set; } = "";
    public string ServerName { get; set; } = "";
    public List<Dictionary<string, object?>> Objects { get; set; } = [];
    public List<Dictionary<string, object?>> Columns { get; set; } = [];
    public List<Dictionary<string, object?>> ForeignKeys { get; set; } = [];
    public List<Dictionary<string, object?>> Dependencies { get; set; } = [];
    public List<Dictionary<string, object?>> Indexes { get; set; } = [];
}
