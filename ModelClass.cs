using System.Numerics;

namespace Pako;

public sealed class ModelClass
{
    public required string DisplayName { get; init; }
    public required ModelKind Kind { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] UVs { get; init; }
    public required uint[] Indices { get; init; }
    public required int[] TriangleMaterials { get; init; }
    public required PreviewMaterial[] Materials { get; init; }
    public required Vector3 Center { get; init; }
    public required float Radius { get; init; }

    public int TriangleCount => Indices.Length / 3;
    public int VertexCount => Positions.Length;
}

public sealed class PreviewMaterial
{
    public required string Name { get; init; }
    public required Vector3 Color { get; init; }
    public PreviewTexture? Diffuse { get; init; }
}

public sealed class PreviewTexture
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Rgba { get; init; }
    public required string Name { get; init; }
}
