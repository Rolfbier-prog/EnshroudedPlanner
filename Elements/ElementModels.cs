// Elements/ElementModels.cs
// SYS-1: Element Registry (Metadata-Layer) – keine Placement/Offset/Rotation-Logik hier!
#nullable enable

namespace EnshroudedPlanner.Elements;

public enum ElementFacet
{
    Structure,
    Roof,
    Terrain,
    Prefab,
    Part,
    System,
}

public enum ElementKind
{
    Piece,
    Tool,
}

public enum PreviewType
{
    GhostMesh,
    Outline,
    Brush,
    None,
}

public readonly record struct FootprintSpec(int SizeX, int SizeY, int SizeZ, bool UsesMajorGrid);
public readonly record struct RotationSpec(bool AllowRotation, int StepDegrees);
public readonly record struct MaterialSpec(bool UsesSelectedMaterial, bool AllowNoMaterial, int? FixedMaterialId);
public readonly record struct PreviewSpec(PreviewType Type, bool ShowFootprintOutline);

public sealed class CategoryDefinition
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int SortIndex { get; init; }
    public string? IconKey { get; init; }
}

public sealed class ElementDefinition
{
    public string Id { get; init; } = "";
    public ElementKind Kind { get; init; } = ElementKind.Piece;
    public string DisplayName { get; init; } = "";
    public string CategoryId { get; init; } = "";

    // Optional: original PieceLibrary category id (if we map to palette categories).
    public string? SourceCategoryId { get; init; }

    // High-level facet used for UI filtering (Structure/Roof/Terrain/...)
    public ElementFacet Facet { get; init; } = ElementFacet.Structure;

    public FootprintSpec Footprint { get; init; }
    public RotationSpec Rotation { get; init; }
    public MaterialSpec Material { get; init; }
    public PreviewSpec Preview { get; init; }

    // Bridge to existing system (PieceLibrary).
    // IMPORTANT: This is metadata only; placement math stays where it is today.
    public string? PieceId { get; init; }
}
