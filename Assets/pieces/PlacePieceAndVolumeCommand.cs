#nullable enable
using System.Collections.Generic;
using EnshroudedPlanner.Rendering.Materials;

namespace EnshroudedPlanner;

/// <summary>
/// Platziert ein Piece in Project.PlacedPieces UND "backt" es gleichzeitig als Voxels in PaintVoxels.
/// Beides ist Undo/Redo-fähig als EIN Command.
/// </summary>
public sealed class PlacePieceAndVolumeCommand : IEditorCommand
{
    private readonly ProjectData _project;
    private readonly Dictionary<(int X, int Y, int Z), MaterialId> _voxels;
    private readonly PlacedPiece _piece;
    private readonly List<(int X, int Y, int Z)> _keys;
    private readonly MaterialId _material;

    private int _insertIndex = -1;
    private readonly Dictionary<(int X, int Y, int Z), (bool Had, MaterialId Old)> _prev = new();

    public PlacePieceAndVolumeCommand(
        ProjectData project,
        Dictionary<(int X, int Y, int Z), MaterialId> voxels,
        PlacedPiece piece,
        List<(int X, int Y, int Z)> keys,
        MaterialId material)
    {
        _project = project;
        _voxels = voxels;
        _piece = piece;
        _keys = keys;
        _material = material;
    }

    public void Do()
    {
        // Piece hinzufügen (Meta)
        _insertIndex = _project.PlacedPieces.Count;
        _project.PlacedPieces.Add(_piece);

        // Voxels backen (Existenz + Material)
        _prev.Clear();
        foreach (var k in _keys)
        {
            if (_prev.ContainsKey(k)) continue;

            bool had = _voxels.TryGetValue(k, out var old);
            _prev[k] = (had, old);

            _voxels[k] = _material;
        }
    }

    public void Undo()
    {
        // Piece entfernen (Index wenn möglich, sonst per Referenz)
        if (_insertIndex >= 0 &&
            _insertIndex < _project.PlacedPieces.Count &&
            ReferenceEquals(_project.PlacedPieces[_insertIndex], _piece))
        {
            _project.PlacedPieces.RemoveAt(_insertIndex);
        }
        else
        {
            _project.PlacedPieces.Remove(_piece);
        }

        // Voxels zurücksetzen
        foreach (var kv in _prev)
        {
            var k = kv.Key;
            var (had, old) = kv.Value;
            if (had) _voxels[k] = old;
            else _voxels.Remove(k);
        }
    }
}
