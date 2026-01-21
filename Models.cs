// Models.cs  (ALLE Datenklassen nur hier – stelle sicher, dass es KEINE zweiten Kopien im Projekt gibt!)
#nullable enable
using EnshroudedPlanner.Rendering.Materials;
using System.Collections.Generic;
using System.Windows.Media.Media3D;

namespace EnshroudedPlanner;

public class PieceLibrary
{
    public int SchemaVersion { get; set; }
    public Units Units { get; set; } = new Units();
    public List<Category> Categories { get; set; } = new List<Category>();
    public List<Piece> Pieces { get; set; } = new List<Piece>();
}

public class Units
{
    public double VoxelSizeMeters { get; set; } = 0.5;
}

public class Category
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public class Piece
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public Size3 Size { get; set; } = new Size3();
    public bool? ReservedOnly { get; set; }
}

public class Size3
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
}

public class ProjectData
{
        public int SchemaVersion { get; set; } = 2;
public BuildZone BuildZone { get; set; } = new BuildZone();
    public Units Units { get; set; } = new Units();
    public List<PlacedPiece> PlacedPieces { get; set; } = new List<PlacedPiece>();
    // Voxel-Pinsel (PaintVoxels) als Liste speichern (Tuple-Keys sind in JSON unpraktisch)
    public List<VoxelPaint> Voxels { get; set; } = new List<VoxelPaint>();
    public bool AltarPlaced { get; set; }
    public int AltarBuildSizeVox { get; set; }
}

public class BuildZone
{
    public string Mode { get; set; } = "";
    public int FlameAltarLevel { get; set; }

    // WICHTIG: NUR EINMAL definieren (sonst "Mehrdeutigkeit ... SizeVoxels")
    public Size3 SizeVoxels { get; set; } = new Size3();

    public string Origin { get; set; } = "";
}

public class PlacedPiece
{
    public string PieceId { get; set; } = "";
    public Point3D Pos { get; set; }
    public int RotY { get; set; }
    public MaterialId? Material { get; set; } // null = "Ohne Material"
}


public interface IEditorCommand
{
    void Do();
    void Undo();
}

public class PlacePieceCommand : IEditorCommand
{
    private readonly ProjectData _project;
    private readonly PlacedPiece _piece;

    public PlacePieceCommand(ProjectData project, PlacedPiece piece)
    {
        _project = project;
        _piece = piece;
    }

    public void Do() => _project.PlacedPieces.Add(_piece);
    public void Undo() => _project.PlacedPieces.Remove(_piece);
}
