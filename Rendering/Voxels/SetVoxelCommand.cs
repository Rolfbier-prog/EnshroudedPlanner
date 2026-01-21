#nullable enable
using EnshroudedPlanner.Rendering.Materials;

namespace EnshroudedPlanner;

public sealed class SetVoxelCommand : IEditorCommand
{
    private readonly MainWindow _w;
    private readonly (int X, int Y, int Z) _key;
    private readonly MaterialId _newValue;
    private readonly bool _erase;

    private bool _hadPrev;
    private MaterialId _prev;

    public SetVoxelCommand(MainWindow w, (int X, int Y, int Z) key, MaterialId newValue, bool erase = false)
    {
        _w = w;
        _key = key;
        _newValue = newValue;
        _erase = erase;
    }

    public void Do()
    {
        _hadPrev = _w.PaintVoxels.TryGetValue(_key, out _prev);

        if (_erase)
            _w.PaintVoxels.Remove(_key);
        else
            _w.PaintVoxels[_key] = _newValue;
    }

    public void Undo()
    {
        if (_hadPrev)
            _w.PaintVoxels[_key] = _prev;
        else
            _w.PaintVoxels.Remove(_key);
    }
}
