#nullable enable
using System.Collections.Generic;

namespace EnshroudedPlanner;

public sealed class BlueprintSnippet
{
    public List<PlacedPiece> Pieces { get; set; } = new();

    // Voxel-Pinsel als Liste speichern (Tuple-Keys sind in JSON blöd)
    public List<VoxelPaint> Voxels { get; set; } = new();
}

public sealed class VoxelPaint
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }

    // Wir speichern als int (entspricht MaterialId enum index)
    public int Material { get; set; }
}
