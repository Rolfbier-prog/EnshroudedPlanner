#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;
using EnshroudedPlanner.Rendering.Materials;

namespace EnshroudedPlanner;

/// <summary>
/// Importiert ein BlueprintSnippet an Zielkoordinaten.
/// - Fügt PlacedPieces hinzu
/// - Backt diese Pieces zusätzlich als Voxels in PaintVoxels (damit Carving/Entfernen funktioniert)
/// - Importiert auch explizite Voxel aus dem Snippet
/// Alles Undo/Redo-fähig.
/// </summary>
public sealed class ImportSnippetCommand : IEditorCommand
{
    private readonly MainWindow _w;
    private readonly BlueprintSnippet _snippet;
    private readonly int _tx, _ty, _tz;

    private readonly List<PlacedPiece> _addedPieces = new();
    private readonly List<(int X, int Y, int Z)> _touchedKeys = new();
    private readonly Dictionary<(int x, int y, int z), (bool had, MaterialId old)> _prev = new();



    public ImportSnippetCommand(MainWindow w, BlueprintSnippet snippet, int targetX, int targetY, int targetZ)
    {
        _w = w;
        _snippet = snippet;
        _tx = targetX;
        _ty = targetY;
        _tz = targetZ;
    }

    public void Do()
    {
        _addedPieces.Clear();
        _touchedKeys.Clear();
        _prev.Clear();

        // Anchor: min coords des Snippets bestimmen
        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;

        foreach (var p in _snippet.Pieces)
        {
            minX = Math.Min(minX, (int)Math.Floor(p.Pos.X));
            minY = Math.Min(minY, (int)Math.Floor(p.Pos.Y));
            minZ = Math.Min(minZ, (int)Math.Floor(p.Pos.Z));
        }

        foreach (var v in _snippet.Voxels)
        {
            minX = Math.Min(minX, v.X);
            minY = Math.Min(minY, v.Y);
            minZ = Math.Min(minZ, v.Z);
        }

        if (minX == int.MaxValue) { minX = 0; minY = 0; minZ = 0; } // leeres Snippet

        int offX = _tx - minX;
        int offY = _ty - minY;
        int offZ = _tz - minZ;

        // helper: touch voxel key once
        void Touch((int X, int Y, int Z) k)
        {
            if (_prev.ContainsKey(k)) return;
            bool had = _w.PaintVoxels.TryGetValue(k, out var old);
            _prev[k] = (had, old);
            _touchedKeys.Add(k);
        }

        // 1) Pieces: in Project.PlacedPieces hinzufügen + als Voxels backen
        foreach (var src in _snippet.Pieces)
        {
            var pp = new PlacedPiece
            {
                PieceId = src.PieceId,
                RotY = src.RotY,
                Pos = new Point3D(src.Pos.X + offX, src.Pos.Y + offY, src.Pos.Z + offZ),
                Material = src.Material
            };

            _w.Project.PlacedPieces.Add(pp);
            _addedPieces.Add(pp);

            var def = _w.FindPieceDefinition(src.PieceId);
            if (def == null) continue;

            int px = (int)Math.Floor(pp.Pos.X);
            int py = (int)Math.Floor(pp.Pos.Y);
            int pz = (int)Math.Floor(pp.Pos.Z);

            var gridRot = MainWindow.ToGridRotY(def, pp.RotY);
            var (w, l, h) = MainWindow.RotatedSizeInt(def, gridRot);
            var mat = pp.Material ?? MaterialId.NoMaterialBlue; // Standard bei "kein Material"

            foreach (var k in _w.EnumerateBoxVoxels(px, py, pz, w, l, h))
            {
                Touch(k);
                _w.PaintVoxels[k] = mat;
            }
        }

        // 2) Explizite Voxels aus Snippet übernehmen (überschreibt ggf. gebackene)
        foreach (var v in _snippet.Voxels)
        {
            var k = (v.X + offX, v.Y + offY, v.Z + offZ);
            Touch(k);
            _w.PaintVoxels[k] = v.Material < 0 ? MaterialId.NoMaterialBlue : (MaterialId)v.Material;
        }
    }

    public void Undo()
    {
        // Pieces entfernen
        foreach (var pp in _addedPieces)
            _w.Project.PlacedPieces.Remove(pp);

        // Voxels zurücksetzen
        foreach (var k in _touchedKeys)
        {
            var (had, old) = _prev[k];
            if (had) _w.PaintVoxels[k] = old;
            else _w.PaintVoxels.Remove(k);
        }
    }
}
