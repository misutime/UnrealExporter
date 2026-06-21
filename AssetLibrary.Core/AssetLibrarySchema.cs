namespace AssetLibrary.Core;

public static class AssetLibrarySchema
{
    public const int Version = 1;
    public const string ManifestFileName = "asset_library.json";
    public const string IndexFileName = "library_index.db";

    public static class Tables
    {
        public const string Metadata = "metadata";
        public const string Assets = "assets";
        public const string ModelValidation = "model_validation";
        public const string TextureLinks = "texture_links";
        public const string MaterialSidecars = "material_sidecars";
        public const string ModelAnimationRelations = "model_animation_relations";
        public const string RelationAnimations = "relation_animations";
        public const string LibraryReports = "library_reports";
    }

    public static readonly string[] RequiredTables =
    [
        Tables.Metadata,
        Tables.Assets,
        Tables.ModelValidation,
        Tables.TextureLinks,
        Tables.MaterialSidecars,
        Tables.LibraryReports
    ];
}
