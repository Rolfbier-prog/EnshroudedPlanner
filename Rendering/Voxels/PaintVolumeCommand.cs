#nullable enable
using System.Collections.Generic;
using EnshroudedPlanner.Rendering.Materials;

namespace EnshroudedPlanner;

/// <summary>
/// Setzt/entfernt ein ganzes Voxels-Volumen (Undo/Redo-fähig).
/// erase=true => Keys entfernen.
/// erase=false => Keys setzen auf newValue.
/// </summary>
public sealed class PaintVolumeCommand : IEditorCommand
{
    private readonly Dictionary<(int X, int Y, int Z), MaterialId> _voxels;
    private readonly List<(int X, int Y, int Z)> _keys;
    private readonly MaterialId _newValue;
    private readonly bool _erase;


    private readonly Dictionary<(int X, int Y, int Z), (bool Had, MaterialId Old)> _prev = new();

    public PaintVolumeCommand(
        Dictionary<(int X, int Y, int Z), MaterialId> voxels,
        List<(int X, int Y, int Z)> keys,
        MaterialId newValue,
        bool erase = false)
    {
        _voxels = voxels;
        _keys = keys;
        _newValue = newValue;
        _erase = erase;
    }

    public void Do()
    {
        _prev.Clear();

        foreach (var k in _keys)
        {
            if (_prev.ContainsKey(k)) continue;

            bool had = _voxels.TryGetValue(k, out var old);
            _prev[k] = (had, old);

            if (_erase) _voxels.Remove(k);
            else _voxels[k] = _newValue;
        }
    }

    public void Undo()
    {
        foreach (var kv in _prev)
        {
            var k = kv.Key;
            var (had, old) = kv.Value;

            if (had) _voxels[k] = old;
            else _voxels.Remove(k);
        }
    }
}
