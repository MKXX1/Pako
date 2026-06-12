namespace Pako;

public enum ModelKind
{
    StaticMesh,
    SkeletalMesh
}

public enum ModelCategory
{
    Characters,
    Props,
    Other
}

public enum PakoMeshExportFormat
{
    Glb,
    ActorX,
    Fbx
}

public sealed class ModelItem
{
    public required string DisplayName { get; init; }
    public required string PackagePath { get; init; }
    public required string ObjectPath { get; init; }
    public required ModelKind Kind { get; init; }
    public ModelCategory Category { get; init; } = ModelCategory.Other;
    public string Folder { get; init; } = "";
    public bool Selected { get; set; }
}
