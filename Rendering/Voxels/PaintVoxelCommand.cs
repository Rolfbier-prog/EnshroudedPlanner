#nullable enable
using System.Collections.Generic;
using EnshroudedPlanner.Rendering.Materials;

namespace EnshroudedPlanner;

/// <summary>
/// Setzt ein einzelnes Voxel (Undo/Redo-fähig).
/// erase=true => Voxel entfernen
/// erase=false => Voxel setzen auf newValue.
/// </summary>
public sealed class PaintVoxelCommand : IEditorCommand
{
    private readonly Dictionary<(int X, int Y, int Z), MaterialId> _voxels;
    private readonly (int X, int Y, int Z) _key;
    private readonly MaterialId _newValue;
    private readonly bool _erase;

    private bool _hadPrev;
    private MaterialId _prev;

    public PaintVoxelCommand(
        Dictionary<(int X, int Y, int Z), MaterialId> voxels,
        (int X, int Y, int Z) key,
        MaterialId newValue,
        bool erase = false)
    {
        _voxels = voxels;
        _key = key;
        _newValue = newValue;
        _erase = erase;
    }

    public void Do()
    {
        _hadPrev = _voxels.TryGetValue(_key, out _prev);

        if (_erase) _voxels.Remove(_key);
        else _voxels[_key] = _newValue;
    }

    public void Undo()
    {
        if (_hadPrev) _voxels[_key] = _prev;
        else _voxels.Remove(_key);
    }
}
