using AssetLibrary.Core;
using Microsoft.Data.Sqlite;

namespace UE5LibraryBrowser;

internal static class UeLibraryComponentRelationReader
{
    public static List<UeLibraryComponentSummary> LoadSummaries(string root, int limit = 2000)
    {
        using var connection = Open(root);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(source_path, ''),
                   COUNT(*) relation_count,
                   COUNT(DISTINCT owner_object_path) owner_count,
                   COUNT(DISTINCT component_object_path) component_count,
                   SUM(CASE WHEN target_asset_kind='Model' THEN 1 ELSE 0 END) model_refs,
                   SUM(CASE WHEN target_asset_kind='Material' THEN 1 ELSE 0 END) material_refs,
                   SUM(CASE WHEN target_asset_kind='Texture' THEN 1 ELSE 0 END) texture_refs,
                   SUM(CASE WHEN target_asset_kind='Animation' THEN 1 ELSE 0 END) animation_refs,
                   SUM(CASE WHEN match_status!='matched' THEN 1 ELSE 0 END) missing_refs
            FROM component_asset_relations
            WHERE target_asset_kind IN ('Model','Material','Texture','Animation')
            GROUP BY source_path
            ORDER BY relation_count DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<UeLibraryComponentSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new UeLibraryComponentSummary
            {
                SourcePath = ReadString(reader, 0),
                RelationCount = ReadInt32(reader, 1),
                OwnerCount = ReadInt32(reader, 2),
                ComponentCount = ReadInt32(reader, 3),
                ModelReferenceCount = ReadInt32(reader, 4),
                MaterialReferenceCount = ReadInt32(reader, 5),
                TextureReferenceCount = ReadInt32(reader, 6),
                AnimationReferenceCount = ReadInt32(reader, 7),
                MissingReferenceCount = ReadInt32(reader, 8)
            });
        }

        return result;
    }

    public static List<UeLibraryComponentRelation> LoadRelationsForSource(string root, string sourcePath, int limit = 1000)
    {
        using var connection = Open(root);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(owner_object_path, ''),
                   COALESCE(owner_type, ''),
                   COALESCE(component_object_path, ''),
                   COALESCE(component_type, ''),
                   COALESCE(component_name, ''),
                   relation_source,
                   relation_type,
                   COALESCE(target_path, ''),
                   COALESCE(target_name, ''),
                   COALESCE(target_asset_kind, ''),
                   COALESCE(target_asset_output, ''),
                   match_status,
                   COALESCE(match_reason, ''),
                   COALESCE(socket_name, '')
            FROM component_asset_relations
            WHERE source_path = $sourcePath
              AND target_asset_kind IN ('Model','Material','Texture','Animation')
            ORDER BY target_asset_kind COLLATE NOCASE,
                     relation_type COLLATE NOCASE,
                     target_name COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$sourcePath", sourcePath);
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<UeLibraryComponentRelation>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new UeLibraryComponentRelation
            {
                OwnerObjectPath = ReadString(reader, 0),
                OwnerType = ReadString(reader, 1),
                ComponentObjectPath = ReadString(reader, 2),
                ComponentType = ReadString(reader, 3),
                ComponentName = ReadString(reader, 4),
                RelationSource = ReadString(reader, 5),
                RelationType = ReadString(reader, 6),
                TargetPath = ReadString(reader, 7),
                TargetName = ReadString(reader, 8),
                TargetAssetKind = ReadString(reader, 9),
                TargetAssetOutput = AssetLibraryIndexReader.ResolveLibraryPath(root, ReadString(reader, 10)),
                MatchStatus = ReadString(reader, 11),
                MatchReason = ReadString(reader, 12),
                SocketName = ReadString(reader, 13)
            });
        }

        return result;
    }

    private static SqliteConnection Open(string root)
    {
        var dbPath = Path.Combine(root, "library_index.db");
        if (!File.Exists(dbPath))
            throw new FileNotFoundException("没有找到 library_index.db。", dbPath);

        SQLitePCL.Batteries_V2.Init();
        var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        return connection;
    }

    private static string ReadString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal)) ?? "";

    private static int ReadInt32(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));
}
