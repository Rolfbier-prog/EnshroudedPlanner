#nullable enable
using System.Windows.Media.Media3D;

namespace EnshroudedPlanner;

public sealed class SetBuildZoneStateCommand : IEditorCommand
{
    private readonly MainWindow _mw;

    // NEW state
    private readonly bool _newAltarPlaced;
    private readonly int _newAltarBuildSizeVox;
    private readonly Point3D _newAltarCenter;
    private readonly Size3 _newSizeVoxels;

    // OLD state (for Undo)
    private bool _oldAltarPlaced;
    private int _oldAltarBuildSizeVox;
    private Point3D _oldAltarCenter;
    private Size3 _oldSizeVoxels;

    public SetBuildZoneStateCommand(
        MainWindow mw,
        bool newAltarPlaced,
        int newAltarBuildSizeVox,
        Point3D newAltarCenter,
        Size3 newSizeVoxels)
    {
        _mw = mw;
        _newAltarPlaced = newAltarPlaced;
        _newAltarBuildSizeVox = newAltarBuildSizeVox;
        _newAltarCenter = newAltarCenter;
        _newSizeVoxels = newSizeVoxels;
    }

    public void Do()
    {
        // Save old state
        _oldAltarPlaced = _mw.Project.AltarPlaced;
        _oldAltarBuildSizeVox = _mw.Project.AltarBuildSizeVox;
        _oldAltarCenter = _mw.AltarCenter;
        _oldSizeVoxels = _mw.Project.BuildZone.SizeVoxels;

        // Apply new state
        _mw.ApplyBuildZoneState(
            altarPlaced: _newAltarPlaced,
            altarBuildSizeVox: _newAltarBuildSizeVox,
            altarCenter: _newAltarCenter,
            newSize: _newSizeVoxels
        );
    }

    public void Undo()
    {
        // Restore old state
        _mw.ApplyBuildZoneState(
            altarPlaced: _oldAltarPlaced,
            altarBuildSizeVox: _oldAltarBuildSizeVox,
            altarCenter: _oldAltarCenter,
            newSize: _oldSizeVoxels
        );
    }
}


