#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace EnshroudedPlanner;

/// <summary>
/// Helper-Methoden, die von Commands (z.B. Import) gebraucht werden.
/// Hinweis: Keine doppelten Member, sonst CS0102/CS0111/CS0229.
/// </summary>
public partial class MainWindow
{
    /// <summary>Lookup eines Piece-Definitions aus der geladenen Library.</summary>
    public Piece? FindPieceDefinition(string pieceId)
        => _library.Pieces.FirstOrDefault(p => p.Id == pieceId);

    /// <summary>
    /// Achsen-aligned Größe eines Pieces in Voxeln (W=X, L=Y, H=Z) inkl. RotY (0/90/180/270).
    /// RotY rotiert in der TopView-Ebene um die Z-Achse -> X/Y tauschen bei 90/270.
    ///
    /// WICHTIG: Basis-Yaw (PieceBaseYawDeg) wird in MainWindow.xaml.cs angewendet,
    /// damit es nur eine Quelle der Wahrheit gibt.
    /// </summary>
    public static (int W, int L, int H) RotatedSizeInt(Piece def, int rotY)
    {
        int w = def.Size.X;
        int l = def.Size.Y;
        int h = def.Size.Z;

        // Rotation in der TopView-Ebene um die Z-Achse -> X/Y tauschen bei 90/270.
        int r = ((rotY % 360) + 360) % 360;

        if (r == 90 || r == 270)
            (w, l) = (l, w);

        return (w, l, h);
    }


    /// <summary>Enumeriert alle Voxel-Keys innerhalb einer Box (x..x+w-1, y..y+l-1, z..z+h-1).</summary>
    public IEnumerable<(int X, int Y, int Z)> EnumerateBoxVoxels(int x, int y, int z, int w, int l, int h)
    {
        for (int dz = 0; dz < h; dz++)
            for (int dy = 0; dy < l; dy++)
                for (int dx = 0; dx < w; dx++)
                    yield return (x + dx, y + dy, z + dz);
    }


    // ============================================================
    // Ghost-Voxel Mesh (nur sichtbare Faces), für Preview/Import-Preview
    // ============================================================
    private enum Face { NegX, PosX, NegY, PosY, NegZ, PosZ }

    private static void AddFace(MeshGeometry3D mesh, int x, int y, int z, Face face)
    {
        double x0 = x, x1 = x + 1;
        double y0 = y, y1 = y + 1;
        double z0 = z, z1 = z + 1;

        Point3D p0, p1, p2, p3;

        switch (face)
        {
            case Face.NegX:
                p0 = new Point3D(x0, y0, z0); p1 = new Point3D(x0, y1, z0);
                p2 = new Point3D(x0, y1, z1); p3 = new Point3D(x0, y0, z1);
                break;
            case Face.PosX:
                p0 = new Point3D(x1, y0, z1); p1 = new Point3D(x1, y1, z1);
                p2 = new Point3D(x1, y1, z0); p3 = new Point3D(x1, y0, z0);
                break;
            case Face.NegY:
                p0 = new Point3D(x0, y0, z1); p1 = new Point3D(x1, y0, z1);
                p2 = new Point3D(x1, y0, z0); p3 = new Point3D(x0, y0, z0);
                break;
            case Face.PosY:
                p0 = new Point3D(x0, y1, z0); p1 = new Point3D(x1, y1, z0);
                p2 = new Point3D(x1, y1, z1); p3 = new Point3D(x0, y1, z1);
                break;
            case Face.NegZ:
                p0 = new Point3D(x0, y0, z0); p1 = new Point3D(x1, y0, z0);
                p2 = new Point3D(x1, y1, z0); p3 = new Point3D(x0, y1, z0);
                break;
            default: // Face.PosZ
                p0 = new Point3D(x0, y1, z1); p1 = new Point3D(x1, y1, z1);
                p2 = new Point3D(x1, y0, z1); p3 = new Point3D(x0, y0, z1);
                break;
        }

        int i0 = mesh.Positions.Count;
        mesh.Positions.Add(p0); mesh.Positions.Add(p1); mesh.Positions.Add(p2); mesh.Positions.Add(p3);

        mesh.TextureCoordinates.Add(new System.Windows.Point(0, 1));
        mesh.TextureCoordinates.Add(new System.Windows.Point(1, 1));
        mesh.TextureCoordinates.Add(new System.Windows.Point(1, 0));
        mesh.TextureCoordinates.Add(new System.Windows.Point(0, 0));

        mesh.TriangleIndices.Add(i0 + 0);
        mesh.TriangleIndices.Add(i0 + 1);
        mesh.TriangleIndices.Add(i0 + 2);
        mesh.TriangleIndices.Add(i0 + 0);
        mesh.TriangleIndices.Add(i0 + 2);
        mesh.TriangleIndices.Add(i0 + 3);
    }

    private static Model3D BuildGhostVoxelSetModel(HashSet<(int X, int Y, int Z)> filled, Brush brush)
    {
        var mesh = new MeshGeometry3D();

        foreach (var (x, y, z) in filled)
        {
            if (!filled.Contains((x - 1, y, z))) AddFace(mesh, x, y, z, Face.NegX);
            if (!filled.Contains((x + 1, y, z))) AddFace(mesh, x, y, z, Face.PosX);
            if (!filled.Contains((x, y - 1, z))) AddFace(mesh, x, y, z, Face.NegY);
            if (!filled.Contains((x, y + 1, z))) AddFace(mesh, x, y, z, Face.PosY);
            if (!filled.Contains((x, y, z - 1))) AddFace(mesh, x, y, z, Face.NegZ);
            if (!filled.Contains((x, y, z + 1))) AddFace(mesh, x, y, z, Face.PosZ);
        }

        var mat = new DiffuseMaterial(brush);
        var gm = new GeometryModel3D { Geometry = mesh, Material = mat, BackMaterial = mat };
        if (gm.CanFreeze) gm.Freeze();
        return gm;
    }

}