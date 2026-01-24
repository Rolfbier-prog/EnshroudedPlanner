// MainWindow.xaml.cs  (HIER KEINE Datenklassen mehr!)
#nullable enable
using HelixToolkit.Wpf;
using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using Velopack;
using Velopack.Sources;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using EnshroudedPlanner.Rendering.Materials;
using EnshroudedPlanner.Rendering.Voxels;

namespace EnshroudedPlanner;

public partial class MainWindow : Window
{
    private const string BaseWindowTitle = "Enshrouded Planner (4x 3D Views)";

    private static string GetAppVersionString()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

            // Prefer semantic version from csproj (<Version>), falls back to assembly version.
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // Strip build metadata (e.g. "+abcdef") if present
                int plus = info.IndexOf('+');
                if (plus >= 0) info = info.Substring(0, plus);
                return info;
            }

            var v = asm.GetName().Version;
            return v?.ToString() ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }

    private void UpdateWindowTitleWithVersion()
    {
        Title = $"{BaseWindowTitle} v{GetAppVersionString()}";
    }
    // ============================================================
    // View Types (komplett hier drin)
    // ============================================================
    public enum ViewType
    {
        Top,
        Bottom,
        Front,
        Back,
        Left,
        Right,
        Free
    }

    // ============================================================
    // VIEWCUBE / WORLD-AXIS FIX
    // ============================================================
    private const double WorldYawDeg = 90;

    // Piece-Library Basis-Yaw (wegen Achsen-Konvention/Library): +90°
    //private const int PieceBaseYawDeg = 90;

    // ============================================================
    // Rotation-Mapping
    // gridRotY   = Rotation im Voxel-Grid (Q/E, Preview/Baking)
    // placedRotY = Rotation, die wir im PlacedPiece speichern (für Export/Import)
    // ============================================================
    public static int ToPlacedRotY(Piece piece, int gridRotY)
    {
        // Default: um Basis-Yaw korrigieren, damit gespeicherte Werte kompatibel bleiben.
        int r = ((gridRotY + PieceBaseYawDeg) % 360 + 360) % 360;
        return r;
    }

    public static int ToGridRotY(Piece piece, int placedRotY)
    {
        int r = ((placedRotY - PieceBaseYawDeg) % 360 + 360) % 360;
        return r;
    }

    //private static int ApplyPieceBaseYaw(int rotY)
    //{
    //    int r = (rotY + PieceBaseYawDeg) % 360;
    //    if (r < 0) r += 360;
    //    return r;
    //}

    private const double WorldPitchDeg = 0;
    private const double WorldRollDeg = 0;

    private const double CompassOutsideMargin = 6.0;

    private readonly Transform3D _worldTransform;
    private readonly GeneralTransform3D _worldTransformInv;

    // 0.0 = Grid zeigt Voxel-KANTEN (Min-Corner, Ganzzahlen)  ✅ für "unten links vom ersten Voxel"
    // 0.5 = Grid zeigt Voxel-ZENTREN (cell-center)
    private const double GridOffset = 0.0;

    private Transform3D CreateWorldTransform()
    {
        var tg = new Transform3DGroup();

        if (Math.Abs(WorldPitchDeg) > 0.0001)
            tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), WorldPitchDeg)));

        if (Math.Abs(WorldRollDeg) > 0.0001)
            tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), WorldRollDeg)));

        if (Math.Abs(WorldYawDeg) > 0.0001)
            tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), WorldYawDeg)));

        return tg;
    }

    // ======= Mouse-Picking / Placement Planes =======
    // Top/Bottom/Free: X/Y Ebene (Z konstant)
    // Front/Back:      X/Z Ebene (Y konstant)
    // Left/Right:      Y/Z Ebene (X konstant)
    private double FrontPlaneY => _altarPlaced ? _altarCenter.Y : (_project.BuildZone.SizeVoxels.Y / 2.0);
    private double LeftPlaneX => _altarPlaced ? _altarCenter.X : (_project.BuildZone.SizeVoxels.X / 2.0);

    private ModelVisual3D CreateSceneRoot() => new ModelVisual3D { Transform = _worldTransform };

    // ============================================================
    // Viewport-Ansichten pro Fenster (A/B/C umschaltbar)
    // ============================================================
    private readonly ViewType[] _viewportTypes = new ViewType[4]
    {
        ViewType.Top,    // View0 (A)
        ViewType.Front,  // View1 (B)
        ViewType.Left,   // View2 (C)
        ViewType.Free    // View3 (D fix)
    };

    private static string ViewTypeToTitle(ViewType vt) => vt switch
    {
        ViewType.Top => "Top",
        ViewType.Bottom => "Bottom",
        ViewType.Front => "Front",
        ViewType.Back => "Back",
        ViewType.Left => "Left",
        ViewType.Right => "Right",
        _ => "Free"
    };

    private ViewType GetViewTypeByIndex(int idx)
    {
        if (idx < 0 || idx > 3) return ViewType.Top;
        return _viewportTypes[idx];
    }

    private ViewType GetViewTypeForViewport(HelixViewport3D vp)
    {
        if (ReferenceEquals(vp, View0)) return _viewportTypes[0];
        if (ReferenceEquals(vp, View1)) return _viewportTypes[1];
        if (ReferenceEquals(vp, View2)) return _viewportTypes[2];
        return ViewType.Free;
    }

    // ============================================================

    private PieceLibrary _library = new PieceLibrary();
    private ProjectData _project = new ProjectData();

    // ======= Blueprint Save/Load State =======
    private const int CurrentProjectSchemaVersion = 2;
    private readonly string _dataRootFolder = ResolveDataRootFolder();

    private string BlueprintsFolder => System.IO.Path.Combine(_dataRootFolder, "Blueprints");
    private string ExportImportFolder => System.IO.Path.Combine(_dataRootFolder, "ExportImport");
    private bool _isDirty = false;
    private string? _lastBlueprintPath = null;

    private Piece? _selectedPiece = null;

    private MaterialLookup? _matLookup;
    private int _previewRotY = 0;

    // ===============================
    // REMOVE MODE (Entfernen-Button)
    // ===============================
    private bool _removeMode = false;
    private Point3D _mouseWorldPos = new Point3D(0, 0, 0);
    // ======= Hover Highlight (Entfernen-Feedback) =======
    private PlacedPiece? _hoverPiece = null;
    private Piece? _hoverPieceDef = null;
    private (int X, int Y, int Z)? _hoverVoxelKey = null;

    private bool _mouseOverViewport = false;
    private int _currentViewportIndex = -1;

    private bool _altarPlaced = false;
    private int _altarBuildSizeVox = 0;
    private Point3D _altarCenter = new Point3D(0, 0, 0);

    private int HalfHeight => _project.BuildZone.SizeVoxels.Z / 2;
    private int CurrentLayerZ => (int)SliderLayer.Value;
    private double GridZ => CurrentLayerZ + 0.02;

    private const int MaxUndo = 20;
    private readonly Stack<IEditorCommand> _undo = new();
    private readonly Stack<IEditorCommand> _redo = new();

    // Performance: Redraw drosseln
    private DateTime _lastRedraw = DateTime.MinValue;

    // Performance: Grid-Geometrie cachen (Points), nicht das Visual!
    private Point3DCollection? _cachedMinorPoints = null;
    private Point3DCollection? _cachedMajorPoints = null;

    private int _cachedGridSizeX = -1;
    private int _cachedGridSizeY = -1;
    private int _cachedGridLayerZ = -1;
    private double _cachedAltarMinX = double.NaN;
    private double _cachedAltarMinY = double.NaN;

    private double _cachedMinX = double.NaN;
    private double _cachedMinY = double.NaN;
    private double _cachedMaxX = double.NaN;
    private double _cachedMaxY = double.NaN;

    // Offsets für Orthographic Views (Slider)
    private double _offA0, _offB0; // View0 X/Y
    private double _offA1, _offB1; // View1 X/Z
    private double _offA2, _offB2; // View2 Y/Z

    // Viewport D (3D) Default-Pan (R/F) – kannst du hier ändern (Startwerte)
    private double _view3PanRight = -60;   // "R" (Right) – seitlich schieben
    private double _view3PanForward = 60; // "F" (Forward) – vor/zurück schieben

    public ProjectData Project => _project;
    public Dictionary<(int X, int Y, int Z), MaterialId> PaintVoxels => _paintVoxels;
    public Point3D AltarCenter => _altarCenter;

    public MainWindow()
    {
        _worldTransform = CreateWorldTransform();
        _worldTransformInv = _worldTransform.Inverse ?? Transform3D.Identity;

        InitializeComponent();
        UpdateWindowTitleWithVersion();

        // Optional: Update-Check beim Start (nur wenn installiert & Setting aktiv)
        try
        {
            if (SettingsStore.Current.CheckForUpdatesOnStart)
            {
                Dispatcher.BeginInvoke(async () =>
                {
                    await _updateService.CheckAndPromptAsync(this, showUpToDateMessage: false);
                }, DispatcherPriority.Background);
            }
        }
        catch
        {
            // ignore
        }

        // Initialisierung + optionaler Startup-Load-Dialog
        Loaded += MainWindow_Loaded;

        PreviewKeyDown -= Window_PreviewKeyDown;
        PreviewKeyDown += Window_PreviewKeyDown;
        Closing += MainWindow_Closing;
    }

    private readonly UpdateService _updateService = new();


    private async void OnCheckUpdatesClicked(object sender, RoutedEventArgs e)
    {
        await _updateService.CheckAndPromptAsync(this, showUpToDateMessage: true);
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow { Owner = this };
        win.ShowDialog();
        // Theme-Anwendung später möglich (SettingsStore.Current.Theme)
    }

    private bool _didInit = false;

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_didInit) return;
        _didInit = true;

        // Titel inkl. Version anzeigen (auch im Dev/VS-Start sichtbar)
        UpdateWindowTitleWithVersion();

        // Update-Check nicht blockierend und nicht doppelt starten
        try
        {
            if (SettingsStore.Current.CheckForUpdatesOnStart)
            {
                Dispatcher.BeginInvoke(async () =>
                {
                    await _updateService.CheckAndPromptAsync(this, showUpToDateMessage: false);
                }, DispatcherPriority.Background);
            }
        }
        catch
        {
            // Update-Fehler sollen die App nicht abstürzen lassen
        }


        LoadLibrary();
        InitializeProject();

        // ===============================
        // Material-Lookup
        // ===============================
        _matLookup = new MaterialLookup(
            "pack://application:,,,/Assets/Textures/atlas_4x3_32px.png",
            tilesX: 4,
            tilesY: 3);

        InitializeUI();
        EnsureDataFolders();

        SetupViewports();
        RedrawAll();

        SetStatus("Bereit (kein Blueprint) – Strg+O zum Laden, Strg+S zum Speichern", autoClearMs: 0);

        var res = MessageBox.Show(
            "Möchtest du ein Blueprint laden?",
            "Blueprint laden",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Yes)
            LoadBlueprint();
    }

    // ======= Close Prompt (Save before Exit) =======
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isDirty) return;

        var res = MessageBox.Show(
            "Du hast ungespeicherte Änderungen. Vor dem Beenden speichern?",
            "Enshrouded Planner",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (res == MessageBoxResult.Yes)
        {
            if (!TrySaveBlueprint(forceDialog: false))
                e.Cancel = true;
        }
    }

    private bool ConfirmSaveIfDirty(string actionText)
    {
        if (!_isDirty) return true;

        var res = MessageBox.Show(
            $"Du hast ungespeicherte Änderungen. Vor '{actionText}' speichern?",
            "Enshrouded Planner",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Cancel)
            return false;

        if (res == MessageBoxResult.Yes)
            return TrySaveBlueprint(forceDialog: false);

        return true; // No
    }

    private void NewBlueprint()
    {
        if (!ConfirmSaveIfDirty("Neues Projekt"))
            return;

        // Reset state
        _project = new ProjectData
        {
            PlacedPieces = new List<PlacedPiece>(),
            Voxels = new List<VoxelPaint>(),
            SchemaVersion = CurrentProjectSchemaVersion,
            AltarPlaced = false,
            AltarBuildSizeVox = 0
        };

        _paintVoxels.Clear();
        _undo.Clear();
        _redo.Clear();
        _pendingImportSnippet = null;
        _importArmed = false;
        _importRotY = 0;
        _selectedPiece = null;
        _previewRotY = 0;

        InitializeProject();

        _altarPlaced = false;
        _altarBuildSizeVox = 0;

        _lastBlueprintPath = null;
        SetDirty(false);

        RefreshProjectInfoText();

        // Layer UI wieder auf Default
        SliderLayer.Minimum = 0;
        SliderLayer.Maximum = _project.BuildZone.SizeVoxels.Z - 1;
        SliderLayer.Value = HalfHeight;
        TxtLayer.Text = "0";
        UpdateLayerTexts();

        if (PieceList != null)
            PieceList.SelectedItem = null;

        InvalidateGridCache();
        SetupViewports();
        RedrawAll();

        SetStatus("Neues Projekt gestartet");
    }

    private void RefreshProjectInfoText()
    {
        if (ProjectInfo == null) return;

        ProjectInfo.Text =
            $"Mode: {_project.BuildZone.Mode}\n" +
            $"Level: {_project.BuildZone.FlameAltarLevel}\n" +
            $"Size Voxels: {_project.BuildZone.SizeVoxels.X} × {_project.BuildZone.SizeVoxels.Y} × {_project.BuildZone.SizeVoxels.Z}\n" +
            $"Origin: {_project.BuildZone.Origin}\n" +
            $"Voxel Size: {_project.Units.VoxelSizeMeters}m\n" +
            $"Z Anzeige (Höhe): -{HalfHeight} .. +{HalfHeight - 1} (UI)";

        // Toolbar summary
        RefreshProjectSummaryText();
    }

    private void RefreshProjectSummaryText()
    {
        if (TxtProjectSummary == null || _project == null) return;

        var size = _project.BuildZone.SizeVoxels;
        int uiLayer = CurrentLayerZ - HalfHeight;

        // Short text for the top bar. Full details remain in "Projekt-Details".
        string mode = (_altarPlaced ? _project.BuildZone.Mode.ToString() : "SANDBOX");
        TxtProjectSummary.Text = $"{mode} | Size {size.X}×{size.Y}×{size.Z} | Layer {uiLayer}";

        if (ProjectInfo != null)
            TxtProjectSummary.ToolTip = ProjectInfo.Text;
    }

    // ======= Helpers =======
    private ViewType GetActiveViewType(HelixViewport3D? viewport = null)
    {
        // prefer current hovered viewport index (A/B/C/D) if available
        if (_currentViewportIndex >= 0 && _currentViewportIndex < 4)
            return GetViewTypeByIndex(_currentViewportIndex);

        if (viewport != null)
            return GetViewTypeForViewport(viewport);

        return ViewType.Top;
    }

    /// <summary>
    /// Snappt die Mausposition passend zur aktiven Ansicht auf ein Voxel-Gitter.
    /// Wichtig: In Front/Back ist Y fix (Schnitt-Ebene), in Left/Right ist X fix.
    /// </summary>
    private Point3D SnapToGrid(Point3D p, ViewType view)
    {
        int z = CurrentLayerZ;

        switch (view)
        {
            case ViewType.Front:
            case ViewType.Back:
                return new Point3D(Math.Floor(p.X), Math.Floor(FrontPlaneY), z);

            case ViewType.Left:
            case ViewType.Right:
                return new Point3D(Math.Floor(LeftPlaneX), Math.Floor(p.Y), z);

            case ViewType.Top:
            case ViewType.Bottom:
            case ViewType.Free:
            default:
                return new Point3D(Math.Floor(p.X), Math.Floor(p.Y), z);
        }
    }

    private Point3D SnapToGrid(Point3D p)
    {
        return SnapToGrid(p, GetActiveViewType());
    }

    // ===============================
    // FULLSCREEN (Maximize) Viewport
    // ===============================
    private int _maximizedIndex = -1;

    private readonly (int Row, int Col, int RowSpan, int ColSpan)[] _origBorderGrid =
    {
        (0, 0, 1, 1), // Border0
        (0, 1, 1, 1), // Border1
        (1, 0, 1, 1), // Border2
        (1, 1, 1, 1), // Border3
    };

    private Border GetBorderByIndex(int idx) => idx switch
    {
        0 => Border0,
        1 => Border1,
        2 => Border2,
        3 => Border3,
        _ => throw new ArgumentOutOfRangeException(nameof(idx))
    };

    private void SetMaximized(int idx)
    {
        for (int i = 0; i < 4; i++)
        {
            var b = GetBorderByIndex(i);
            b.Visibility = Visibility.Visible;

            Grid.SetRow(b, _origBorderGrid[i].Row);
            Grid.SetColumn(b, _origBorderGrid[i].Col);
            Grid.SetRowSpan(b, _origBorderGrid[i].RowSpan);
            Grid.SetColumnSpan(b, _origBorderGrid[i].ColSpan);
        }

        for (int i = 0; i < 4; i++)
            if (i != idx)
                GetBorderByIndex(i).Visibility = Visibility.Collapsed;

        var max = GetBorderByIndex(idx);
        Grid.SetRow(max, 0);
        Grid.SetColumn(max, 0);
        Grid.SetRowSpan(max, 2);
        Grid.SetColumnSpan(max, 2);

        _maximizedIndex = idx;
    }

    private void ClearMaximized()
    {
        for (int i = 0; i < 4; i++)
        {
            var b = GetBorderByIndex(i);
            b.Visibility = Visibility.Visible;

            Grid.SetRow(b, _origBorderGrid[i].Row);
            Grid.SetColumn(b, _origBorderGrid[i].Col);
            Grid.SetRowSpan(b, _origBorderGrid[i].RowSpan);
            Grid.SetColumnSpan(b, _origBorderGrid[i].ColSpan);
        }

        _maximizedIndex = -1;
    }

    private void BtnMax_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is null) return;
        if (!int.TryParse(fe.Tag.ToString(), out int idx)) return;
        if (idx < 0 || idx > 3) return;

        if (_maximizedIndex == idx)
            ClearMaximized();
        else
            SetMaximized(idx);
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn)
            return;

        if (!int.TryParse(btn.Tag?.ToString(), out int idx))
            return;

        switch (idx)
        {
            case 0:
                SetupOrthographicView(View0, _viewportTypes[0]);
                break;
            case 1:
                SetupOrthographicView(View1, _viewportTypes[1]);
                break;
            case 2:
                SetupOrthographicView(View2, _viewportTypes[2]);
                break;
            case 3:
                ResetViewport3D();
                break;
        }
    }


    /// <summary>
    /// Mouse Picking (HelixToolkit.Wpf 3.1.2)
    /// Szene ist per _worldTransform gedreht:
    /// Ebene in Welt definieren -> ins Scene-space -> UnProject -> zurück in Welt
    /// </summary>
    private bool TryGetMouseWorldPoint(HelixViewport3D viewport, Point mousePos, out Point3D world)
    {
        world = default;

        ViewType activeView = (_currentViewportIndex >= 0 && _currentViewportIndex < 4)
            ? GetViewTypeByIndex(_currentViewportIndex)
            : GetViewTypeForViewport(viewport);

        Point3D planePointWorld;
        Vector3D planeNormalWorld;

        switch (activeView)
        {
            case ViewType.Front:
            case ViewType.Back:
                planePointWorld = new Point3D(0, FrontPlaneY, 0);
                planeNormalWorld = new Vector3D(0, 1, 0);
                break;

            case ViewType.Left:
            case ViewType.Right:
                planePointWorld = new Point3D(LeftPlaneX, 0, 0);
                planeNormalWorld = new Vector3D(1, 0, 0);
                break;

            default:
                planePointWorld = new Point3D(0, 0, CurrentLayerZ);
                planeNormalWorld = new Vector3D(0, 0, 1);
                break;
        }

        Point3D planePointScene = _worldTransform.Transform(planePointWorld);

        Point3D n0 = _worldTransform.Transform(new Point3D(0, 0, 0));
        Point3D n1 = _worldTransform.Transform(new Point3D(
            planeNormalWorld.X, planeNormalWorld.Y, planeNormalWorld.Z));
        Vector3D planeNormalScene = n1 - n0;

        Point3D? sceneOpt = viewport.Viewport.UnProject(mousePos, planePointScene, planeNormalScene);
        if (!sceneOpt.HasValue) return false;

        Point3D scene = sceneOpt.Value;
        if (double.IsNaN(scene.X) || double.IsNaN(scene.Y) || double.IsNaN(scene.Z))
            return false;

        if (!_worldTransformInv.TryTransform(scene, out world))
            return false;

        return true;
    }

    // ======= Viewports =======
    private void SetupViewports()
    {
        if (View0 is null || View1 is null || View2 is null || View3 is null)
            return;

        SetupOrthographicView(View0, _viewportTypes[0]);
        Title0.Text = $"Viewport A ({ViewTypeToTitle(_viewportTypes[0])})";

        SetupOrthographicView(View1, _viewportTypes[1]);
        Title1.Text = $"Viewport B ({ViewTypeToTitle(_viewportTypes[1])})";

        SetupOrthographicView(View2, _viewportTypes[2]);
        Title2.Text = $"Viewport C ({ViewTypeToTitle(_viewportTypes[2])})";

        Setup3DView(View3);
        ResetViewport3D();
        Title3.Text = "Viewport D (3D frei)";

        // Events: doppelte Registrierung verhindern
        View0.MouseMove -= Viewport_MouseMove;
        View1.MouseMove -= Viewport_MouseMove;
        View2.MouseMove -= Viewport_MouseMove;
        View3.MouseMove -= Viewport_MouseMove;

        View0.MouseLeftButtonDown -= Viewport_MouseLeftButtonDown;
        View1.MouseLeftButtonDown -= Viewport_MouseLeftButtonDown;
        View2.MouseLeftButtonDown -= Viewport_MouseLeftButtonDown;
        View3.MouseLeftButtonDown -= Viewport_MouseLeftButtonDown;

        View0.PreviewMouseRightButtonDown -= Viewport_PreviewMouseRightButtonDown;
        View1.PreviewMouseRightButtonDown -= Viewport_PreviewMouseRightButtonDown;
        View2.PreviewMouseRightButtonDown -= Viewport_PreviewMouseRightButtonDown;
        View3.PreviewMouseRightButtonDown -= Viewport_PreviewMouseRightButtonDown;

        View0.MouseMove += Viewport_MouseMove;
        View1.MouseMove += Viewport_MouseMove;
        View2.MouseMove += Viewport_MouseMove;
        View3.MouseMove += Viewport_MouseMove;

        View0.MouseLeftButtonDown += Viewport_MouseLeftButtonDown;
        View1.MouseLeftButtonDown += Viewport_MouseLeftButtonDown;
        View2.MouseLeftButtonDown += Viewport_MouseLeftButtonDown;
        View3.MouseLeftButtonDown += Viewport_MouseLeftButtonDown;
        // View0.PreviewMouseRightButtonDown += Viewport_PreviewMouseRightButtonDown;  // deaktiviert: Rightclick-Remove / altes Hover-Highlight
        // View1.PreviewMouseRightButtonDown += Viewport_PreviewMouseRightButtonDown;  // deaktiviert: Rightclick-Remove / altes Hover-Highlight
        // View2.PreviewMouseRightButtonDown += Viewport_PreviewMouseRightButtonDown;  // deaktiviert: Rightclick-Remove / altes Hover-Highlight
        // View3.PreviewMouseRightButtonDown += Viewport_PreviewMouseRightButtonDown;  // deaktiviert: Rightclick-Remove / altes Hover-Highlight
        View0.MouseEnter += (_, __) => { _mouseOverViewport = true; _currentViewportIndex = 0; };
        View1.MouseEnter += (_, __) => { _mouseOverViewport = true; _currentViewportIndex = 1; };
        View2.MouseEnter += (_, __) => { _mouseOverViewport = true; _currentViewportIndex = 2; };
        View3.MouseEnter += (_, __) => { _mouseOverViewport = true; _currentViewportIndex = 3; };

        View0.MouseLeave += (_, __) => { _mouseOverViewport = false; _currentViewportIndex = -1; RedrawAll(); };
        View1.MouseLeave += (_, __) => { _mouseOverViewport = false; _currentViewportIndex = -1; RedrawAll(); };
        View2.MouseLeave += (_, __) => { _mouseOverViewport = false; _currentViewportIndex = -1; RedrawAll(); };
        View3.MouseLeave += (_, __) => { _mouseOverViewport = false; _currentViewportIndex = -1; _hoverPiece = null; _hoverPieceDef = null; _hoverVoxelKey = null; RedrawAll(); };
    }

    // Viewport D (3D) wieder auf Standard: Zentrum der Zone, Blickrichtung W-N (schräg) + leicht von oben
    private void ResetViewport3D()
    {
        if (View3 == null)
            return;

        // Zentrum (immer Zone-Mitte)
        GetBuildBounds(out double minX, out double maxX, out double minY, out double maxY);
        double cx = (minX + maxX) * 0.5;
        double cy = (minY + maxY) * 0.5;
        double cz = HalfHeight; // UI-0 liegt bei HalfHeight (intern)

        // Distanz abhängig von Größe (etwas näher)
        double sizeX = Math.Max(1.0, maxX - minX);
        double sizeY = Math.Max(1.0, maxY - minY);
        double radius = Math.Max(sizeX, sizeY) * 0.5;
        double dist = Math.Max(35.0, radius * 1.7);
        double height = dist * 0.65;

        // Kamera-Position: SE -> Blick nach W-N (gegenüber von E-S)
        var pos = new Point3D(cx + dist, cy - dist, cz + height);
        var target = new Point3D(cx, cy, cz);
        var look = target - pos;

        var cam = new PerspectiveCamera
        {
            Position = pos,
            LookDirection = new Vector3D(look.X, look.Y, look.Z),
            UpDirection = new Vector3D(0, 0, 1),
            FieldOfView = 45
        };

        // Default-Framing-Offset (ohne Rotation): verschiebt Kamera + Ziel gemeinsam
        // (LookDirection bleibt gleich, dadurch wandert das "LookAt" mit).
        try
        {
            // Kamera-Basis (Right/Forward) aus aktueller Ansicht ableiten
            var lookN = cam.LookDirection;
            if (lookN.LengthSquared > 1e-9) lookN.Normalize();

            var upN = cam.UpDirection;
            if (upN.LengthSquared < 1e-9) upN = new Vector3D(0, 0, 1);
            upN.Normalize();

            var right = Vector3D.CrossProduct(lookN, upN);
            if (right.LengthSquared > 1e-9) right.Normalize();

            var forward = Vector3D.CrossProduct(upN, right);
            if (forward.LengthSquared > 1e-9) forward.Normalize();

            // Default-Framing-Offset: R/F (wie früher "R:.. F:..")
            var delta = (right * _view3PanRight) + (forward * _view3PanForward);

            // Pan: Position verschieben, LookDirection unverändert lassen
            cam.Position = cam.Position + delta;
        }
        catch

        {
            // ignorieren
        }

        View3.Camera = cam;
    }

    private void SetupOrthographicView(HelixViewport3D viewport, ViewType type, bool preserveZoom = false)
    {
        if (viewport is null) return;

        // If the user zoomed in/out, Helix changes OrthographicCamera.Width.
        // When sliders change, we want to keep the current zoom instead of snapping back.
        double? oldWidth = null;
        if (preserveZoom && viewport.Camera is OrthographicCamera oldOrtho)
            oldWidth = oldOrtho.Width;

        double dist = 200;

        Vector3D offset = new Vector3D(0, 0, 0);

        if (ReferenceEquals(viewport, View0)) offset = new Vector3D(_offA0, _offB0, 0);       // X/Y
        else if (ReferenceEquals(viewport, View1)) offset = new Vector3D(_offA1, 0, _offB1); // X/Z
        else if (ReferenceEquals(viewport, View2)) offset = new Vector3D(0, _offA2, _offB2); // Y/Z

        GetBuildBounds(out double minX, out double maxX, out double minY, out double maxY);
        double width = Math.Max(maxX - minX, maxY - minY) + 40;

        Point3D posWorld;
        Vector3D lookWorld;
        Vector3D upWorld;

        switch (type)
        {
            case ViewType.Top:
                posWorld = (_altarCenter + offset) + new Vector3D(0, 0, dist);
                lookWorld = new Vector3D(0, 0, -dist);
                upWorld = new Vector3D(0, 1, 0);
                break;

            case ViewType.Bottom:
                posWorld = (_altarCenter + offset) + new Vector3D(0, 0, -dist);
                lookWorld = new Vector3D(0, 0, dist);
                upWorld = new Vector3D(0, 1, 0);
                break;

            case ViewType.Front:
                posWorld = (_altarCenter + offset) + new Vector3D(0, -dist, 0);
                lookWorld = new Vector3D(0, dist, 0);
                upWorld = new Vector3D(0, 0, 1);
                break;

            case ViewType.Back:
                posWorld = (_altarCenter + offset) + new Vector3D(0, dist, 0);
                lookWorld = new Vector3D(0, -dist, 0);
                upWorld = new Vector3D(0, 0, 1);
                break;

            case ViewType.Left:
                posWorld = (_altarCenter + offset) + new Vector3D(-dist, 0, 0);
                lookWorld = new Vector3D(dist, 0, 0);
                upWorld = new Vector3D(0, 0, 1);
                break;

            case ViewType.Right:
                posWorld = (_altarCenter + offset) + new Vector3D(dist, 0, 0);
                lookWorld = new Vector3D(-dist, 0, 0);
                upWorld = new Vector3D(0, 0, 1);
                break;

            default:
                return;
        }

        Point3D posScene = _worldTransform.Transform(posWorld);

        Point3D o0 = _worldTransform.Transform(new Point3D(0, 0, 0));
        Point3D oLook = _worldTransform.Transform(new Point3D(lookWorld.X, lookWorld.Y, lookWorld.Z));
        Point3D oUp = _worldTransform.Transform(new Point3D(upWorld.X, upWorld.Y, upWorld.Z));

        Vector3D lookScene = oLook - o0;
        Vector3D upScene = oUp - o0;

        viewport.ShowCoordinateSystem = false;
        viewport.ShowViewCube = false;
        viewport.IsHeadLightEnabled = true;

        var cam = new OrthographicCamera
        {
            Position = posScene,
            LookDirection = lookScene,
            UpDirection = upScene,
            Width = width
        };

        // Keep zoom level when slider offsets change
        if (oldWidth.HasValue)
            cam.Width = oldWidth.Value;

        viewport.Camera = cam;
    }

    private static void Setup3DView(HelixViewport3D viewport)
    {
        viewport.ShowCoordinateSystem = false;
        viewport.ShowViewCube = true;
        viewport.IsHeadLightEnabled = true;

        viewport.Camera = new PerspectiveCamera
        {
            Position = new Point3D(-60, -60, 60),
            LookDirection = new Vector3D(60, 60, -40),
            UpDirection = new Vector3D(0, 0, 1),
            FieldOfView = 45
        };

        // Helix Controls:
        viewport.IsPanEnabled = true;
        viewport.IsRotationEnabled = true;
        viewport.IsZoomEnabled = true;

        viewport.PanGesture = new MouseGesture(MouseAction.LeftClick, ModifierKeys.Shift);
        viewport.RotateGesture = new MouseGesture(MouseAction.RightClick);
        viewport.ZoomGesture = new MouseGesture(MouseAction.MiddleClick);
    }

    // ============================================================
    // ComboBox View-Umschaltung (A/B/C)
    // ============================================================
    private void ComboView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb) return;
        if (cb.Tag is null) return;
        if (!int.TryParse(cb.Tag.ToString(), out int idx)) return;
        if (idx < 0 || idx > 2) return;

        var selected = (cb.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (string.IsNullOrWhiteSpace(selected)) return;

        if (!TryParseViewType(selected, out var vt))
            return;

        if (vt == ViewType.Free) return;

        _viewportTypes[idx] = vt;

        if (idx == 0)
        {
            SetupOrthographicView(View0, _viewportTypes[0]);
            Title0.Text = $"Viewport A ({ViewTypeToTitle(_viewportTypes[0])})";
        }
        else if (idx == 1)
        {
            SetupOrthographicView(View1, _viewportTypes[1]);
            Title1.Text = $"Viewport B ({ViewTypeToTitle(_viewportTypes[1])})";
        }
        else if (idx == 2)
        {
            SetupOrthographicView(View2, _viewportTypes[2]);
            Title2.Text = $"Viewport C ({ViewTypeToTitle(_viewportTypes[2])})";
        }

        RedrawAll();
    }

    private static bool TryParseViewType(string text, out ViewType vt)
    {
        vt = ViewType.Top;
        text = text.Trim();

        return text switch
        {
            "Top" => (vt = ViewType.Top) == ViewType.Top,
            "Bottom" => (vt = ViewType.Bottom) == ViewType.Bottom,
            "Front" => (vt = ViewType.Front) == ViewType.Front,
            "Back" => (vt = ViewType.Back) == ViewType.Back,
            "Left" => (vt = ViewType.Left) == ViewType.Left,
            "Right" => (vt = ViewType.Right) == ViewType.Right,
            _ => false
        };
    }

    // ============================================================
    // [VOXEL-PINSEL] Material + kleine Test-Voxel Map
    // ============================================================
    // Standard-Auswahl: "kein Material" (dunkles, mattes Blau ohne Textur)
    private MaterialId _activeMaterial = MaterialId.NoMaterialBlue;


    private readonly Dictionary<(int X, int Y, int Z), MaterialId> _paintVoxels = new();

    // Atlas/Brush Cache
    private ImageSource? _atlasImage;
    private readonly Dictionary<MaterialId, Brush> _tileBrushCache = new();

    private const int AtlasTilesX = 4;
    private const int AtlasTilesY = 3;

    private void ComboMaterial_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboMaterialGroup.SelectedItem is MaterialId id)
        {
            _activeMaterial = id;
            TxtActiveMaterial.Text = $"Aktiv: {_activeMaterial}";
            RedrawAll();
        }
    }

    public void ApplyBuildZoneState(bool altarPlaced, int altarBuildSizeVox, Point3D altarCenter, Size3 newSize)
    {
        _altarPlaced = altarPlaced;
        _altarBuildSizeVox = altarBuildSizeVox;
        _altarCenter = altarCenter;

        _project.AltarPlaced = altarPlaced;
        _project.AltarBuildSizeVox = altarBuildSizeVox;

        _project.BuildZone.SizeVoxels = newSize;

        MarkDirty();

        SliderLayer.Minimum = 0;
        SliderLayer.Maximum = _project.BuildZone.SizeVoxels.Z - 1;
        if (SliderLayer.Value > SliderLayer.Maximum) SliderLayer.Value = SliderLayer.Maximum;

        ProjectInfo.Text =
            $"Mode: {(_altarPlaced ? _project.BuildZone.Mode : "SANDBOX")}\n" +
            $"Level: {(_altarPlaced ? _project.BuildZone.FlameAltarLevel.ToString() : "-")}\n" +
            $"Size Voxels: {_project.BuildZone.SizeVoxels.X} × {_project.BuildZone.SizeVoxels.Y} × {_project.BuildZone.SizeVoxels.Z}\n" +
            $"Origin: {_project.BuildZone.Origin}\n" +
            $"Voxel Size: {_project.Units.VoxelSizeMeters}m\n" +
            $"Z Anzeige (Höhe): -{HalfHeight} .. +{HalfHeight - 1} (UI)";

        InvalidateGridCache();
        SetupViewports();
    }

    private Brush GetTileBrush(MaterialId id)
    {
        if (_tileBrushCache.TryGetValue(id, out var b)) return b;

        // "Kein Material": dunkles, mattes Blau ohne Textur
        if (id == MaterialId.NoMaterialBlue)
        {
            var solid = new SolidColorBrush(Color.FromRgb(45, 78, 125));
            if (solid.CanFreeze) solid.Freeze();
            _tileBrushCache[id] = solid;
            return solid;
        }

        if (_atlasImage == null)
        {
            _atlasImage = new BitmapImage(new Uri("pack://application:,,,/Assets/Textures/atlas_4x3_32px.png", UriKind.Absolute));
            if (_atlasImage.CanFreeze) _atlasImage.Freeze();
        }

        int tileIndex = (int)id;
        int x = tileIndex % AtlasTilesX;
        int y = tileIndex / AtlasTilesX;

        double w = 1.0 / AtlasTilesX;
        double h = 1.0 / AtlasTilesY;

        var vb = new Rect(x * w, y * h, w, h);

        var brush = new ImageBrush(_atlasImage)
        {
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewbox = vb,
            Stretch = Stretch.Fill,
            TileMode = TileMode.None
        };

        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.Linear);

        if (brush.CanFreeze) brush.Freeze();
        _tileBrushCache[id] = brush;
        return brush;
    }

    // ======= Input =======
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Import abbrechen
            _importArmed = false;
            _pendingImportSnippet = null;
            _importRotY = 0;

            // Piece-Selection abbrechen
            _selectedPiece = null;
            PieceList.SelectedItem = null;
            _previewRotY = 0;

            e.Handled = true;
            RedrawAll();
            return;
        }
        // Shortcuts
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (e.Key == Key.S)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    SaveBlueprintAs();
                else
                    SaveBlueprint();

                e.Handled = true;
                return;
            }

            if (e.Key == Key.O)
            {
                LoadBlueprint();
                e.Handled = true;
                return;
            }
        }


        // Q/E sollen sowohl Piece-Preview als auch Import-Preview drehen können
        if (e.Key == Key.Q || e.Key == Key.E)
        {
            int delta = (e.Key == Key.Q) ? 270 : 90;

            if (_importArmed && _pendingImportSnippet != null)
            {
                _importRotY = (_importRotY + delta) % 360;
                e.Handled = true;
                RedrawAll();
                return;
            }

            if (_selectedPiece != null)
            {
                _previewRotY = (_previewRotY + delta) % 360;
                e.Handled = true;
                RedrawAll();
                return;
            }
        }
    }


    private void UpdateHoverTargets()
    {
        _hoverPiece = null;
        _hoverPieceDef = null;
        _hoverVoxelKey = null;

        // nur wenn Maus über Viewport
        if (!_mouseOverViewport) return;

        var p = SnapToGrid(_mouseWorldPos);
        int x = (int)p.X, y = (int)p.Y, z = (int)p.Z;

        // 1) Voxel-Pinsel?
        var vKey = (x, y, z);
        if (_paintVoxels.ContainsKey(vKey))
        {
            _hoverVoxelKey = vKey;
            return;
        }

        // 2) Piece?
        if (TryFindPieceAtCell(x, y, z, out var pp, out var def))
        {
            _hoverPiece = pp;
            _hoverPieceDef = def;
        }
    }

    private bool TryFindPieceAtCell(int x, int y, int z, out PlacedPiece pp, out Piece def)
    {
        // Reverse = "oberstes" zuletzt gesetztes zuerst
        for (int i = _project.PlacedPieces.Count - 1; i >= 0; i--)
        {
            var candidate = _project.PlacedPieces[i];
            var piece = _library.Pieces.FirstOrDefault(p => p.Id == candidate.PieceId);
            if (piece == null) continue;

            var (w, l, h) = GetVoxelAlignedSize(piece, candidate.RotY);

            int px = (int)candidate.Pos.X;
            int py = (int)candidate.Pos.Y;
            int pz = (int)candidate.Pos.Z;

            if (x >= px && x < px + (int)Math.Round(w) &&
                y >= py && y < py + (int)Math.Round(l) &&
                z >= pz && z < pz + (int)Math.Round(h))
            {
                pp = candidate;
                def = piece;
                return true;
            }
        }

        pp = default!;
        def = default!;
        return false;
    }

    private const int HoverFloodLimit = 6000;

    private void DrawHoverHighlight(ModelVisual3D root, Color color)
    {
        // 1) PaintVoxel-Element (Connected Component) highlight
        if (_hoverVoxelKey.HasValue)
        {
            var key = _hoverVoxelKey.Value;

            // Versuche, das "Element" (zusammenhängende Voxels gleicher MaterialId) zu finden
            if (TryGetPaintVoxelElementBounds(key, out int ex, out int ey, out int ez, out int ew, out int el, out int eh))
            {
                // leicht anheben gegen Z-Fighting
                DrawWireBox(root, ex + GridOffset, ey + GridOffset, ez + 0.02, ew, el, eh, color);
            }
            else
            {
                // Fallback: 1x1x1
                DrawWireBox(root, key.X + GridOffset, key.Y + GridOffset, key.Z + 0.02, 1, 1, 1, color);
            }
            return;
        }

        // 2) Piece highlight (volle AABB)
        if (_hoverPiece != null && _hoverPieceDef != null)
        {
            var pp = _hoverPiece;
            var piece = _hoverPieceDef;

            var (w, l, h0) = GetVoxelAlignedSize(piece, ToGridRotY(piece, pp.RotY));

            int px = (int)pp.Pos.X;
            int py = (int)pp.Pos.Y;
            double pz = pp.Pos.Z;

            DrawWireBox(root, px + GridOffset, py + GridOffset, pz + 0.02, w, l, h0, color);
        }
    }

    /// <summary>
    /// Ermittelt für einen Hover-Voxel das zusammenhängende "Element" (6-Nachbarn) gleicher MaterialId
    /// und gibt dessen axis-aligned Bounds in Voxeln zurück.
    /// </summary>
    private bool TryGetPaintVoxelElementBounds((int X, int Y, int Z) start,
        out int x, out int y, out int z, out int w, out int l, out int h)
    {
        x = y = z = w = l = h = 0;

        if (!_paintVoxels.TryGetValue(start, out MaterialId mat))
            return false;

        int minX = start.X, maxX = start.X;
        int minY = start.Y, maxY = start.Y;
        int minZ = start.Z, maxZ = start.Z;

        // Flood-Fill mit Limit (damit große Terrain-Flächen nicht ruckeln)
        var q = new Queue<(int X, int Y, int Z)>();
        var visited = new HashSet<long>();

        long Pack(int xx, int yy, int zz)
            => (((long)xx & 0x1FFFFF) << 42) | (((long)yy & 0x1FFFFF) << 21) | ((long)zz & 0x1FFFFF);

        q.Enqueue(start);
        visited.Add(Pack(start.X, start.Y, start.Z));

        int processed = 0;

        while (q.Count > 0)
        {
            var v = q.Dequeue();
            processed++;
            if (processed > HoverFloodLimit)
            {
                // Fallback: nur Startvoxel (schnell & deterministisch)
                x = start.X; y = start.Y; z = start.Z;
                w = 1; l = 1; h = 1;
                return true;
            }

            if (v.X < minX) minX = v.X;
            if (v.X > maxX) maxX = v.X;
            if (v.Y < minY) minY = v.Y;
            if (v.Y > maxY) maxY = v.Y;
            if (v.Z < minZ) minZ = v.Z;
            if (v.Z > maxZ) maxZ = v.Z;

            // 6-Neighborhood
            TryEnqueue(v.X + 1, v.Y, v.Z);
            TryEnqueue(v.X - 1, v.Y, v.Z);
            TryEnqueue(v.X, v.Y + 1, v.Z);
            TryEnqueue(v.X, v.Y - 1, v.Z);
            TryEnqueue(v.X, v.Y, v.Z + 1);
            TryEnqueue(v.X, v.Y, v.Z - 1);
        }

        x = minX;
        y = minY;
        z = minZ;
        w = (maxX - minX) + 1;
        l = (maxY - minY) + 1;
        h = (maxZ - minZ) + 1;
        return true;

        void TryEnqueue(int nx, int ny, int nz)
        {
            long p = Pack(nx, ny, nz);
            if (visited.Contains(p)) return;

            if (_paintVoxels.TryGetValue((nx, ny, nz), out MaterialId m) && m == mat)
            {
                visited.Add(p);
                q.Enqueue((nx, ny, nz));
            }
        }
    }

    private static void DrawRectOutline(ModelVisual3D root, double x, double y, double w, double l, double z, Color color)
    {
        // LinesVisual3D zeichnet SEGMENTPAARE: (P0->P1), (P2->P3) ...
        // Daher 8 Punkte (4 Segmente) für ein Rechteck.
        root.Children.Add(new LinesVisual3D
        {
            Color = color,
            Thickness = 2.0,
            Points = new Point3DCollection
            {
                // unten
                new Point3D(x,     y,     z), new Point3D(x + w, y,     z),
                // rechts
                new Point3D(x + w, y,     z), new Point3D(x + w, y + l, z),
                // oben
                new Point3D(x + w, y + l, z), new Point3D(x,     y + l, z),
                // links
                new Point3D(x,     y + l, z), new Point3D(x,     y,     z),
            }
        });
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not HelixViewport3D viewport) return;
        if (!_mouseOverViewport) return;

        var mouse = e.GetPosition(viewport);

        if (TryGetMouseWorldPoint(viewport, mouse, out var world))
            _mouseWorldPos = world;



        if ((DateTime.UtcNow - _lastRedraw).TotalMilliseconds < 33) return;
        _lastRedraw = DateTime.UtcNow;

        RedrawAll();
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not HelixViewport3D viewport) return;

        // ✅ FIX 1: Viewport D SHIFT+LMB darf NICHT bauen (Helix Pan)
        if (ReferenceEquals(viewport, View3) && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            return;

        // [IMPORT MODE] – nächster Klick platziert Snippet an Mausposition
        if (_importArmed && _pendingImportSnippet != null)
        {
            var target = SnapToGrid(_mouseWorldPos);

            // Normalisieren + optional drehen (Q/E)
            var prepared = BuildRotatedNormalizedSnippet(_pendingImportSnippet, _importRotY);

            ExecuteCommand(new ImportSnippetCommand(this, prepared, (int)target.X, (int)target.Y, (int)target.Z));
            return;
        }

        // ✅ FIX 2: Wenn gerade ein UI-Element im Viewport-Header angeklickt wurde -> ignorieren
        // (Sicherheitsnetz; meistens ist sender ohnehin nur HelixViewport3D)
        if (e.OriginalSource is DependencyObject d)
        {
            var fe = d as FrameworkElement;
            if (fe != null && (fe is Slider || fe is Button || fe is ComboBox || fe is TextBox))
                return;
        }


        // ===== REMOVE MODE =====
        if (_removeMode)
        {
            if (TryGetEraseVolume(out int rx, out int ry, out int rz, out int rw, out int rl, out int rh))
            {
                var keys = EnumerateBoxVoxels(rx, ry, rz, rw, rl, rh).ToList();
                ExecuteCommand(new PaintVolumeCommand(PaintVoxels, keys, newValue: MaterialId.NoMaterialBlue, erase: true));
            }
            return;
        }

        // [NEU] Wenn kein Piece gewählt ist -> 1-Voxel-Pinsel setzen
        if (_selectedPiece == null)
        {
            var p = SnapToGrid(_mouseWorldPos);
            var key = ((int)p.X, (int)p.Y, (int)p.Z);

            ExecuteCommand(new SetVoxelCommand(this, key, _activeMaterial));
            return;
        }

        // ALTAR / SANDBOX platzieren (UNDO/REDO-fähig)
        if (_selectedPiece.Id.StartsWith("__ALTAR__", StringComparison.Ordinal))
        {
            // 1) Sandbox ohne Altar
            if (_selectedPiece.Id.Equals("__ALTAR__NONE", StringComparison.Ordinal))
            {
                var newSize = new Size3
                {
                    X = 320,
                    Y = 320,
                    Z = _project.BuildZone.SizeVoxels.Z
                };

                var newCenter = new Point3D(
                    newSize.X / 2.0,
                    newSize.Y / 2.0,
                    HalfHeight
                );

                ExecuteCommand(new SetBuildZoneStateCommand(
                    this,
                    newAltarPlaced: false,
                    newAltarBuildSizeVox: 0,
                    newAltarCenter: newCenter,
                    newSizeVoxels: newSize
                ));

                return;
            }

            // 2) Normale Altäre (80/160/240/320)
            if (int.TryParse(_selectedPiece.Id.Replace("__ALTAR__", "", StringComparison.Ordinal), out int buildSize))
            {
                var snapped = SnapToGrid(_mouseWorldPos);
                var newCenter = new Point3D(snapped.X + 4, snapped.Y + 4, HalfHeight);

                var newSize = new Size3
                {
                    X = buildSize,
                    Y = buildSize,
                    Z = _project.BuildZone.SizeVoxels.Z
                };

                ExecuteCommand(new SetBuildZoneStateCommand(
                    this,
                    newAltarPlaced: true,
                    newAltarBuildSizeVox: buildSize,
                    newAltarCenter: newCenter,
                    newSizeVoxels: newSize
                ));
            }

            return;
        }

        // Normales Bauteil platzieren
        var snappedPos = SnapToGrid(_mouseWorldPos);

        var placedPiece = new PlacedPiece
        {
            PieceId = _selectedPiece.Id,
            Pos = new Point3D(snappedPos.X, snappedPos.Y, snappedPos.Z),
            RotY = ToPlacedRotY(_selectedPiece, _previewRotY),
            // NEU:
            Material = _activeMaterial
        };

        // Immer voxelgenau backen (wie Preview)
        var gridRotY = _previewRotY;
        var (pw, pl, ph) = GetVoxelAlignedSize(_selectedPiece, gridRotY);

        var bakeKeys = EnumerateBoxVoxels(
            (int)snappedPos.X, (int)snappedPos.Y, (int)snappedPos.Z,
            (int)pw, (int)pl, (int)ph
        ).ToList();


        ExecuteCommand(new PlacePieceAndVolumeCommand(
                    _project,
                    PaintVoxels,
                    placedPiece,
                    bakeKeys,
                    _activeMaterial
                ));
    }

    private bool TryFindPlacedPieceAt(Point3D worldPoint, out PlacedPiece? found)
    {
        found = null;

        // Topmost = zuletzt platziert (von hinten durchgehen)
        for (int i = _project.PlacedPieces.Count - 1; i >= 0; i--)
        {
            var pp = _project.PlacedPieces[i];
            var piece = _library.Pieces.FirstOrDefault(x => x.Id == pp.PieceId);
            if (piece == null) continue;

            var (w, l, h) = GetVoxelAlignedSize(piece, ToGridRotY(piece, pp.RotY));

            // Axis-aligned Bounding Box in Weltkoordinaten
            if (worldPoint.X >= pp.Pos.X && worldPoint.X < pp.Pos.X + w &&
                worldPoint.Y >= pp.Pos.Y && worldPoint.Y < pp.Pos.Y + l &&
                worldPoint.Z >= pp.Pos.Z && worldPoint.Z < pp.Pos.Z + h)
            {
                found = pp;
                return true;
            }
        }

        return false;
    }



    private void Viewport_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-Click entfernen ist deaktiviert (Entfernen läuft über Remove-Mode + Linksklick).
        // Diese Methode bleibt absichtlich leer.
    }


    private void GetBuildBounds(out double minX, out double maxX, out double minY, out double maxY)
    {
        if (_altarPlaced && _altarBuildSizeVox > 0)
        {
            double half = _altarBuildSizeVox / 2.0;
            minX = _altarCenter.X - half;
            maxX = _altarCenter.X + half;
            minY = _altarCenter.Y - half;
            maxY = _altarCenter.Y + half;
        }
        else
        {
            minX = 0;
            minY = 0;
            maxX = _project.BuildZone.SizeVoxels.X;
            maxY = _project.BuildZone.SizeVoxels.Y;
        }
    }

    private void InvalidateGridCache()
    {
        _cachedMinorPoints = null;
        _cachedMajorPoints = null;

        _cachedGridSizeX = -1;
        _cachedGridSizeY = -1;
        _cachedGridLayerZ = -1;

        _cachedAltarMinX = double.NaN;
        _cachedAltarMinY = double.NaN;

        _cachedMinX = _cachedMinY = _cachedMaxX = _cachedMaxY = double.NaN;
    }

    // ======= Data =======
    private void LoadLibrary()
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"Assets\pieces\piece_library.v2.json");
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var temp = JsonConvert.DeserializeObject<PieceLibrary>(json);
                _library = temp ?? CreateDefaultLibrary();
            }
            else
            {
                _library = CreateDefaultLibrary();
            }
        }
        catch
        {
            _library = CreateDefaultLibrary();
        }
    }

    private void InitializeProject()
    {
        _project.BuildZone = new BuildZone
        {
            Mode = "FLAME_ALTAR",
            FlameAltarLevel = 1,
            SizeVoxels = new Size3 { X = 80, Y = 80, Z = 80 },
            Origin = "CRITER"
        };
        _project.Units = new Units { VoxelSizeMeters = 0.5 };

        _altarCenter = new Point3D(
            _project.BuildZone.SizeVoxels.X / 2.0,
            _project.BuildZone.SizeVoxels.Y / 2.0,
            HalfHeight
        );
    }

    private void InitializeUI()
    {
        // ===============================
        // Material UI (Group Dropdown + Tiles)
        // ===============================
        ComboMaterialGroup.SelectionChanged -= ComboMaterialGroup_SelectionChanged;
        ComboMaterialGroup.SelectionChanged += ComboMaterialGroup_SelectionChanged;

        // Gruppen (kannst du später erweitern)
        ComboMaterialGroup.ItemsSource = new[] { "Stone", "Wood", "Brick", "Metal", "Glow" };
        ComboMaterialGroup.SelectedIndex = 0; // triggert Tile-Aufbau

        TxtActiveMaterial.Text = $"Aktiv: {_activeMaterial}";


        // ===============================
        // Combo/Max Events sicher setzen
        // ===============================
        ComboView0.Tag = "0";
        ComboView1.Tag = "1";
        ComboView2.Tag = "2";

        ComboView0.SelectionChanged -= ComboView_SelectionChanged;
        ComboView1.SelectionChanged -= ComboView_SelectionChanged;
        ComboView2.SelectionChanged -= ComboView_SelectionChanged;

        ComboView0.SelectionChanged += ComboView_SelectionChanged;
        ComboView1.SelectionChanged += ComboView_SelectionChanged;
        ComboView2.SelectionChanged += ComboView_SelectionChanged;

        BtnMax0.Tag = "0";
        BtnMax1.Tag = "1";
        BtnMax2.Tag = "2";
        BtnMax3.Tag = "3";

        BtnMax0.Click -= BtnMax_Click;
        BtnMax1.Click -= BtnMax_Click;
        BtnMax2.Click -= BtnMax_Click;
        BtnMax3.Click -= BtnMax_Click;

        BtnMax0.Click += BtnMax_Click;
        BtnMax1.Click += BtnMax_Click;
        BtnMax2.Click += BtnMax_Click;
        BtnMax3.Click += BtnMax_Click;

        // ===============================
        // Slider Offsets (FIX: funktioniert)
        // ===============================
        SliderA0.ValueChanged += (_, __) => { _offA0 = SliderA0.Value; SetupOrthographicView(View0, _viewportTypes[0], preserveZoom: true); };
        SliderB0.ValueChanged += (_, __) => { _offB0 = SliderB0.Value; SetupOrthographicView(View0, _viewportTypes[0], preserveZoom: true); };

        SliderA1.ValueChanged += (_, __) => { _offA1 = SliderA1.Value; SetupOrthographicView(View1, _viewportTypes[1], preserveZoom: true); };
        SliderB1.ValueChanged += (_, __) => { _offB1 = SliderB1.Value; SetupOrthographicView(View1, _viewportTypes[1], preserveZoom: true); };

        SliderA2.ValueChanged += (_, __) => { _offA2 = SliderA2.Value; SetupOrthographicView(View2, _viewportTypes[2], preserveZoom: true); };
        SliderB2.ValueChanged += (_, __) => { _offB2 = SliderB2.Value; SetupOrthographicView(View2, _viewportTypes[2], preserveZoom: true); };

        // ===============================
        // Project Info
        // ===============================
        RefreshProjectInfoText();

        SliderLayer.Minimum = 0;
        SliderLayer.Maximum = _project.BuildZone.SizeVoxels.Z - 1;
        SliderLayer.Value = HalfHeight;
        TxtLayer.Text = "0";
        UpdateLayerTexts();

        SliderLayer.ValueChanged += (_, __) => { UpdateLayerTexts(); InvalidateGridCache(); RedrawAll(); };
        BtnLayerDown.Click += (_, __) => SetLayer(CurrentLayerZ - 1);
        BtnLayerUp.Click += (_, __) => SetLayer(CurrentLayerZ + 1);
        BtnLayerMinus8.Click += (_, __) => SetLayer(CurrentLayerZ - 8);
        BtnLayerPlus8.Click += (_, __) => SetLayer(CurrentLayerZ + 8);

        TxtLayer.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (int.TryParse(TxtLayer.Text, out int uiZ))
            {
                int internalZ = uiZ + HalfHeight;
                SetLayer(internalZ);
            }
        };


        CategoryCombo.DisplayMemberPath = "DisplayName";
        CategoryCombo.SelectionChanged += (_, __) => RefreshPieces();

        if (_library.Categories.Count > 0)
            CategoryCombo.SelectedIndex = 0;

        PieceList.SelectionChanged += (_, __) =>
        {
            // Wenn der User ein Piece auswählt: Import-Modus aus
            _importArmed = false;
            _pendingImportSnippet = null;

            _selectedPiece = PieceList.SelectedItem as Piece;
            _previewRotY = 0;
            RedrawAll();
        };

        RefreshPieces();

        BtnUndo.Click += (_, __) => Undo();
        BtnRedo.Click += (_, __) => Redo();
        BtnNewBlueprint.Click += (_, __) => NewBlueprint();
        BtnSaveBlueprint.Click += (_, __) => SaveBlueprint();
        BtnLoadBlueprint.Click += (_, __) => LoadBlueprint();
        BtnExportBlueprint.Click += (_, __) => ExportSnippet();
        BtnImportBlueprint.Click += (_, __) => BeginImportSnippet();

        BtnCheckUpdates.Click += OnCheckUpdatesClicked;
        BtnSettings.Click += OnSettingsClicked;


        CheckShowGrid.Checked += (_, __) => RedrawAll();
        CheckShowGrid.Unchecked += (_, __) => RedrawAll();
        CheckShowCompass.Checked += (_, __) => RedrawAll();
        CheckShowCompass.Unchecked += (_, __) => RedrawAll();

        BtnWalls.Checked += (_, __) => SetUiRootGroup(UiRootGroup.Structure);
        BtnRoofs.Checked += (_, __) => SetUiRootGroup(UiRootGroup.Roof);
        BtnTerrain.Checked += (_, __) => SetUiRootGroup(UiRootGroup.Terrain);
        BtnWalls.IsChecked = true;
        SetUiRootGroup(UiRootGroup.Structure);

        // ===== Remove-Mode Button =====
        // Wir verwenden FindName, damit das Projekt auch kompiliert, falls der Button noch nicht im XAML existiert.
        if (FindName("BtnRemoveMode") is System.Windows.Controls.Primitives.ToggleButton rm)
        {
            rm.Checked += (_, __) => { _removeMode = true; RedrawAll(); };
            rm.Unchecked += (_, __) => { _removeMode = false; RedrawAll(); };
        }
    }

    private void ComboMaterialGroup_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var group = ComboMaterialGroup.SelectedItem?.ToString() ?? "Stone";
        BuildMaterialTiles(group);
    }

    private void BuildMaterialTiles(string group)
    {
        MaterialTilesPanel.Children.Clear();

        // Auswahl pro Gruppe (du kannst das später aus JSON / Tabelle füttern)
        var ids = group switch
        {
            "Stone" => new[] { MaterialId.StoneGrayLight, MaterialId.StoneGrayDark },
            "Wood" => new[] { MaterialId.WoodBrownLight, MaterialId.WoodBrownDark },
            "Brick" => new[] { MaterialId.BrickSand, MaterialId.BrickRed },
            "Metal" => new[] { MaterialId.MetalLight, MaterialId.MetalDark },
            "Glow" => new[] { MaterialId.GlowYellow, MaterialId.GlowBlue, MaterialId.GlowRed },
            _ => new[] { MaterialId.StoneGrayLight }
        };

        // "Ohne Material" Kachel
        var noneBtn = new Button
        {
            Width = 48,
            Height = 48,
            Margin = new Thickness(4),
            ToolTip = "Ohne Material",
            Content = "Ø",
            FontWeight = FontWeights.Bold,
            Background = GetTileBrush(MaterialId.NoMaterialBlue),
            Foreground = Brushes.White
        };
        noneBtn.Click += (_, __) =>
        {
            _activeMaterial = MaterialId.NoMaterialBlue;
            TxtActiveMaterial.Text = "Aktiv: Ohne Material (Blau matt)";
        };
        MaterialTilesPanel.Children.Add(noneBtn);

        foreach (var id in ids)
        {
            var btn = new Button
            {
                Width = 48,
                Height = 48,
                Margin = new Thickness(4),
                ToolTip = id.ToString(),
                Background = GetTileBrush(id),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent
            };

            btn.Click += (_, __) =>
            {
                _activeMaterial = id;
                TxtActiveMaterial.Text = $"Aktiv: {_activeMaterial}";

            };

            MaterialTilesPanel.Children.Add(btn);
        }
    }

    private void FilterByGroup(string group)
    {
        var filtered = _library.Categories
            .Where(c => c.Id.Contains(group, StringComparison.OrdinalIgnoreCase))
            .ToList();

        CategoryCombo.ItemsSource = filtered;
        CategoryCombo.SelectedIndex = filtered.Count > 0 ? 0 : -1;
    }

    private enum UiRootGroup { Structure, Roof, Terrain }

    private sealed class UiCategory
    {
        public required string Id { get; init; }          // z.B. "WALL"
        public required string DisplayName { get; init; } // z.B. "Wände"
        public required Func<string, bool> MatchCategoryId { get; init; } // prüft Piece.CategoryId
    }

    private UiRootGroup _uiRoot = UiRootGroup.Structure;
    private List<UiCategory> _uiCats = new();

    private void SetUiRootGroup(UiRootGroup root)
    {
        _uiRoot = root;

        // Buttons exklusiv (nur einer aktiv)
        if (root != UiRootGroup.Structure) BtnWalls.IsChecked = false;
        if (root != UiRootGroup.Roof) BtnRoofs.IsChecked = false;
        if (root != UiRootGroup.Terrain) BtnTerrain.IsChecked = false;

        _uiCats = root switch
        {
            UiRootGroup.Structure => new List<UiCategory>
        {
            new UiCategory { Id="WALL",  DisplayName="Wände",        MatchCategoryId = cid => cid.StartsWith("WALL") },
            new UiCategory { Id="FLOOR", DisplayName="Böden",        MatchCategoryId = cid => cid.StartsWith("FLOOR") },
            new UiCategory { Id="BLOCK", DisplayName="Blöcke",       MatchCategoryId = cid => cid.StartsWith("BLOCK") },
            new UiCategory { Id="ALTAR", DisplayName="Flammenaltar", MatchCategoryId = cid => cid == "ALTAR" },
            // später: STAIRS, PILLAR, BEAM etc.
        },
            UiRootGroup.Roof => new List<UiCategory>
        {
            new UiCategory { Id="ROOF", DisplayName="Dächer", MatchCategoryId = cid => cid.StartsWith("ROOF") }
        },
            UiRootGroup.Terrain => new List<UiCategory>
        {
            new UiCategory { Id="TERRAIN", DisplayName="Terrain", MatchCategoryId = cid => cid.StartsWith("TERRAIN") }
        },
            _ => new List<UiCategory>()
        };

        CategoryCombo.ItemsSource = _uiCats;
        CategoryCombo.DisplayMemberPath = "DisplayName";
        CategoryCombo.SelectedIndex = _uiCats.Count > 0 ? 0 : -1;

        RefreshPieces();
    }

    private void RefreshPieces()
    {
        var uiCat = CategoryCombo.SelectedItem as UiCategory;
        if (uiCat == null)
        {
            PieceList.ItemsSource = null;
            return;
        }

        List<Piece> list;

        // Flammenaltar / Freebuild sind "virtuelle" Palette-Einträge (keine echten Library-Pieces)
        if (uiCat.Id == "ALTAR")
        {
            list = BuildAltarPalette();
        }
        else
        {
            list = _library.Pieces
                .Where(p => uiCat.MatchCategoryId(p.CategoryId))
                .OrderBy(p => p.DisplayName)
                .ToList();
        }

        PieceList.DisplayMemberPath = "DisplayName";
        PieceList.ItemsSource = list;
    }

    private static List<Piece> BuildAltarPalette()
    {
        // Enshrouded: 1=40m(80 Vox), 2=80m(160 Vox), 3=120m(240 Vox), 4=160m(320 Vox)
        // Freebuild: wie Stufe 4 (ohne Altar)
        var altarFootprint = new Size3 { X = 8, Y = 8, Z = 4 };

        return new List<Piece>
        {
            new Piece { Id = "__ALTAR__80",  DisplayName = "Flammenaltar Stufe 1 (40m / 80 Vox)",   CategoryId = "ALTAR", Size = altarFootprint },
            new Piece { Id = "__ALTAR__160", DisplayName = "Flammenaltar Stufe 2 (80m / 160 Vox)",  CategoryId = "ALTAR", Size = altarFootprint },
            new Piece { Id = "__ALTAR__240", DisplayName = "Flammenaltar Stufe 3 (120m / 240 Vox)", CategoryId = "ALTAR", Size = altarFootprint },
            new Piece { Id = "__ALTAR__320", DisplayName = "Flammenaltar Stufe 4 (160m / 320 Vox)", CategoryId = "ALTAR", Size = altarFootprint },

            // Ohne Altar: Sandbox/Freebuild (visuell/Bounds 320×320; tatsächlich clamped wird nichts)
            new Piece { Id = "__ALTAR__NONE", DisplayName = "Freebuild (ohne Altar) (160m / 320 Vox)", CategoryId = "ALTAR", Size = new Size3 { X = 1, Y = 1, Z = 1 } },
        };
    }

    // ======= Rendering =======
    private void RedrawAll()
    {

        UpdateHoverTargets();
        DrawViewport(View0, _viewportTypes[0]);
        DrawViewport(View1, _viewportTypes[1]);
        DrawViewport(View2, _viewportTypes[2]);
        DrawViewport(View3, ViewType.Free);
    }

    private void DrawViewport(HelixViewport3D viewport, ViewType viewType)
    {
        if (viewport is null) return;

        viewport.Children.Clear();
        viewport.Children.Add(new DefaultLights());

        var root = CreateSceneRoot();
        viewport.Children.Add(root);

        DrawGroundPlane(root);
        if (_altarPlaced)
        {
            DrawAltar(root);
        }

        // Grid/Kompass unabhängig vom Altar
        if (CheckShowGrid.IsChecked == true)
            DrawGrid(root);

        if (CheckShowCompass.IsChecked == true)
            DrawCompass(root, viewType);


        foreach (var pp in _project.PlacedPieces)
        {
            // nichts zeichnen (Meta nur für Undo/Export)
        }


        // PaintVoxels als EIN Mesh (Face-Culling, pro Voxel-Face => keine gestreckten Tiles)
        if (_paintVoxels.Count > 0)
        {
            root.Children.Add(new ModelVisual3D { Content = BuildPaintVoxelModel() });
        }

        // Remove-Preview am Mauszeiger
        if (_mouseOverViewport && _currentViewportIndex >= 0)
        {
            if (_removeMode)
            {
                if (TryGetEraseVolume(out int ex, out int ey, out int ez, out int ew, out int el, out int eh))
                {
                    // leicht über der Oberfläche, damit kein Z-Fighting
                    DrawWireBox(root, ex + GridOffset, ey + GridOffset, ez, ew, el, eh, Colors.Red);
                }
            }
            else if (_selectedPiece != null)
            {
                if (_selectedPiece.Id.StartsWith("__ALTAR__", StringComparison.Ordinal))
                    DrawAltarAt(root, SnapToGrid(_mouseWorldPos));
                else
                {
                    // [PREVIEW] Piece-Vorschau als Ghost-Voxels (voxelgenau, kein 0.5-Offset)
                    var sp = SnapToGrid(_mouseWorldPos);
                    var (pw, pl, ph) = GetVoxelAlignedSize(_selectedPiece, _previewRotY);
                    DrawGhostVoxelPreview(root, (int)sp.X, (int)sp.Y, (int)sp.Z, (int)pw, (int)pl, (int)ph);
                }
            }
        }
        // ===============================
        // IMPORT PREVIEW (Ghost)
        // ===============================
        if (_importArmed && _pendingImportSnippet != null && _mouseOverViewport)
        {
            var basePos = SnapToGrid(_mouseWorldPos);

            var prepared = BuildRotatedNormalizedSnippet(_pendingImportSnippet, _importRotY);

            // Preview: Pieces
            foreach (var pp in prepared.Pieces)
            {
                var piece = _library.Pieces.FirstOrDefault(p => p.Id == pp.PieceId);
                if (piece == null) continue;

                var pos = new Point3D(
                    basePos.X + pp.Pos.X,
                    basePos.Y + pp.Pos.Y,
                    basePos.Z + pp.Pos.Z
                );

                // [IMPORT PREVIEW] Pieces als Ghost-Voxels (voxelgenau, kein Box-Offset)
                var gridRot = ToGridRotY(piece, pp.RotY);
                var (iwD, ilD, ihD) = GetVoxelAlignedSize(piece, gridRot);
                DrawGhostVoxelPreview(root,
                    (int)Math.Floor(pos.X),
                    (int)Math.Floor(pos.Y),
                    (int)Math.Floor(pos.Z),
                    (int)Math.Round(iwD),
                    (int)Math.Round(ilD),
                    (int)Math.Round(ihD));
            }

            // Preview: Voxels
            // Preview: Voxels (als ein Mesh, nicht transparent)
            {
                var ghostKeys = new HashSet<(int X, int Y, int Z)>();
                foreach (var v in prepared.Voxels)
                    ghostKeys.Add(((int)basePos.X + v.X, (int)basePos.Y + v.Y, (int)basePos.Z + v.Z));

                var ghostBrush = new SolidColorBrush(Colors.Goldenrod);
                if (ghostBrush.CanFreeze) ghostBrush.Freeze();

                root.Children.Add(new ModelVisual3D { Content = BuildGhostVoxelSetModel(ghostKeys, ghostBrush) });
            }
        }

        // Hover-Highlight (nicht im Remove-Mode, damit es nicht mit dem Remove-Brush verwechselt wird)
        if (_mouseOverViewport && !_removeMode)
        {
            // Farbe: Piece-Placement vs. Material-Paint (inkl. NoMaterialBlue)
            var hoverColor = (_selectedPiece != null) ? Colors.DeepSkyBlue : Colors.LimeGreen;
            DrawHoverHighlight(root, hoverColor);
        }
    }



    // ============================================================
    // PaintVoxels -> optimiertes Chunk-Mesh (opak)
    // - baut aus der Sparse-Dictionary eine dichte Chunk-Box um die belegten Voxels
    // - encodiert MaterialId so, dass 0 im Chunk "leer" bleibt (MaterialId 0 wäre sonst unsichtbar)
    // ============================================================
    private Model3D BuildPaintVoxelModel()
    {
        if (_matLookup == null || _paintVoxels.Count == 0)
            return new Model3DGroup();

        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;

        foreach (var k in _paintVoxels.Keys)
        {
            if (k.X < minX) minX = k.X;
            if (k.Y < minY) minY = k.Y;
            if (k.Z < minZ) minZ = k.Z;

            if (k.X > maxX) maxX = k.X;
            if (k.Y > maxY) maxY = k.Y;
            if (k.Z > maxZ) maxZ = k.Z;
        }

        // Defensive: leere Bounds
        if (minX == int.MaxValue)
            return new Model3DGroup();

        int sx = (maxX - minX) + 1;
        int sy = (maxY - minY) + 1;
        int sz = (maxZ - minZ) + 1;

        var chunk = new VoxelChunk(minX, minY, minZ, sx, sy, sz);

        foreach (var kv in _paintVoxels)
        {
            var (x, y, z) = kv.Key;
            int lx = x - minX;
            int ly = y - minY;
            int lz = z - minZ;
            if (!chunk.InBounds(lx, ly, lz)) continue;

            // Encode: 0 bleibt "leer"
            int enc = ((int)kv.Value) + 1;
            chunk.Set(lx, ly, lz, enc);
        }

        return VoxelChunkVisual.BuildModel(chunk, _matLookup);
    }

    private void DrawAltarAt(ModelVisual3D root, Point3D pos)
    {
        // Freebuild / Sandbox ohne Altar (Preview: fixe 320×320-Zone)
        if (_selectedPiece != null && _selectedPiece.Id.Equals("__ALTAR__NONE", StringComparison.Ordinal))
        {
            // nur eine Vorschau der Sandbox-Grenzen (kein Altar-Block)
            DrawWireBox(root,
                0 + GridOffset, 0 + GridOffset,
                HalfHeight + 0.03,
                320, 320, 0.1,
                Colors.DeepSkyBlue);
            return;
        }

        // Normale Altäre: Altar-Footprint + Vorschau der Build-Zone (Stufe 1–4)
        int buildSize = 0;
        if (_selectedPiece != null &&
            int.TryParse(_selectedPiece.Id.Replace("__ALTAR__", "", StringComparison.Ordinal), out int bs))
        {
            buildSize = bs;
        }

        var center = new Point3D(pos.X + 4, pos.Y + 4, HalfHeight);

        // Altar selbst (8×8×4)
        root.Children.Add(new BoxVisual3D
        {
            Width = 8,
            Length = 8,
            Height = 4,
            Center = center,
            Fill = new SolidColorBrush(Color.FromArgb(120, 80, 180, 80))
        });

        // Build-Zone Preview (nur wenn Stufe bekannt)
        if (buildSize > 0)
        {
            double half = buildSize / 2.0;
            double minX = center.X - half;
            double minY = center.Y - half;

            DrawWireBox(root,
                minX + GridOffset, minY + GridOffset,
                HalfHeight + 0.03,
                buildSize, buildSize, 0.1,
                Colors.LimeGreen);
        }
    }

    private void DrawGroundPlane(ModelVisual3D root)
    {
        GetBuildBounds(out double minX, out double maxX, out double minY, out double maxY);

        double width = Math.Max(1, maxX - minX);
        double length = Math.Max(1, maxY - minY);

        root.Children.Add(new BoxVisual3D
        {
            Width = width,
            Length = length,
            Height = 0.02,
            Center = new Point3D((minX + maxX) / 2.0, (minY + maxY) / 2.0, HalfHeight + 0.01),
            Fill = new SolidColorBrush(Color.FromArgb(60, 0, 200, 0))
        });
    }

    private void DrawAltar(ModelVisual3D root)
    {
        root.Children.Add(new BoxVisual3D
        {
            Width = 8,
            Length = 8,
            Height = 4,
            Center = new Point3D(_altarCenter.X, _altarCenter.Y, HalfHeight),
            Fill = new SolidColorBrush(Color.FromArgb(80, 0, 200, 0))
        });
    }

    private void DrawGrid(ModelVisual3D root)
    {
        int sizeX = _project.BuildZone.SizeVoxels.X;
        int sizeY = _project.BuildZone.SizeVoxels.Y;
        int layerZ = CurrentLayerZ;

        GetBuildBounds(out double minX, out double maxX, out double minY, out double maxY);

        // Grid-Regel (Roadmap v4):
        // - Fine-Lines (1er Raster) über das gesamte Grid
        // - Major-Lines (8er Raster) über das gesamte Grid
        // - Das "Zentrum" soll NICHT auf einem Major-Line-Kreuz liegen:
        //   Der mittlere 8x8 Bereich ist Fine (Major liegt auf den 8x8-Rändern, nicht durchs Zentrum).
        //
        // Hinweis für später (Flammenaltäre):
        // _altarBuildSizeVox wird extern gesetzt (z.B. 80/160/240/320), GetBuildBounds() nutzt das automatisch.

        // Fokus-Zentrum: um Altar (wenn vorhanden) sonst Mitte der BuildBounds, auf Ganzzahlen gesnappt.
        double centerX;
        double centerY;

        if (_altarPlaced)
        {
            centerX = Math.Floor(_altarCenter.X);
            centerY = Math.Floor(_altarCenter.Y);
        }
        else
        {
            centerX = Math.Floor((minX + maxX) * 0.5);
            centerY = Math.Floor((minY + maxY) * 0.5);
        }

        // Major-Anker liegt 4 Voxels vom Zentrum -> Zentrum liegt in einem 8x8-Fine-Feld zwischen Major-Lines.
        double majorAnchorX = centerX - 4.0;
        double majorAnchorY = centerY - 4.0;

        bool cacheValid =
            _cachedMinorPoints != null &&
            _cachedMajorPoints != null &&
            _cachedGridSizeX == sizeX &&
            _cachedGridSizeY == sizeY &&
            _cachedGridLayerZ == layerZ &&
            Math.Abs(_cachedAltarMinX - majorAnchorX) < 0.0001 &&
            Math.Abs(_cachedAltarMinY - majorAnchorY) < 0.0001 &&
            Math.Abs(_cachedMinX - minX) < 0.0001 &&
            Math.Abs(_cachedMinY - minY) < 0.0001 &&
            Math.Abs(_cachedMaxX - maxX) < 0.0001 &&
            Math.Abs(_cachedMaxY - maxY) < 0.0001;

        if (!cacheValid)
        {
            _cachedGridSizeX = sizeX;
            _cachedGridSizeY = sizeY;
            _cachedGridLayerZ = layerZ;

            // Reuse cache fields (historically "altarMin") as the major-anchor origin
            _cachedAltarMinX = majorAnchorX;
            _cachedAltarMinY = majorAnchorY;

            _cachedMinX = minX;
            _cachedMinY = minY;
            _cachedMaxX = maxX;
            _cachedMaxY = maxY;

            double z = CurrentLayerZ + 0.05;

            var minorPts = new Point3DCollection();
            var majorPts = new Point3DCollection();

            void AddVerticalSeg(Point3DCollection pts, double x, double y0, double y1)
            {
                double xx = x + GridOffset;
                pts.Add(new Point3D(xx, y0, z));
                pts.Add(new Point3D(xx, y1, z));
            }

            void AddHorizontalSeg(Point3DCollection pts, double y, double x0, double x1)
            {
                double yy = y + GridOffset;
                pts.Add(new Point3D(x0, yy, z));
                pts.Add(new Point3D(x1, yy, z));
            }

            // ---- Fine-Lines (1er Raster) über das gesamte Grid ----
            int x0i = (int)Math.Ceiling(minX);
            int x1i = (int)Math.Floor(maxX);
            int y0i = (int)Math.Ceiling(minY);
            int y1i = (int)Math.Floor(maxY);

            for (int x = x0i; x <= x1i; x++)
                AddVerticalSeg(minorPts, x, minY, maxY);

            for (int y = y0i; y <= y1i; y++)
                AddHorizontalSeg(minorPts, y, minX, maxX);

            // ---- Major-Lines (8er Raster) über das gesamte Grid ----
            const double step = 8.0;
            const double eps = 0.0001;

            // erste Major-Line <= minX, aligned an majorAnchorX
            double firstMajorX = majorAnchorX + Math.Floor((minX - majorAnchorX) / step) * step;
            if (firstMajorX < minX - eps) firstMajorX += step;
            for (double x = firstMajorX; x <= maxX + eps; x += step)
                AddVerticalSeg(majorPts, x, minY, maxY);

            // erste Major-Line <= minY, aligned an majorAnchorY
            double firstMajorY = majorAnchorY + Math.Floor((minY - majorAnchorY) / step) * step;
            if (firstMajorY < minY - eps) firstMajorY += step;
            for (double y = firstMajorY; y <= maxY + eps; y += step)
                AddHorizontalSeg(majorPts, y, minX, maxX);

            minorPts.Freeze();
            majorPts.Freeze();

            _cachedMinorPoints = minorPts;
            _cachedMajorPoints = majorPts;
        }

        root.Children.Add(new LinesVisual3D
        {
            Color = Color.FromArgb(60, 0, 0, 0),
            Thickness = 0.6,
            Points = _cachedMinorPoints!
        });

        root.Children.Add(new LinesVisual3D
        {
            Color = Color.FromArgb(120, 0, 0, 0),
            Thickness = 1.8,
            Points = _cachedMajorPoints!
        });
    }



    private void DrawCompass(ModelVisual3D root, ViewType viewType)
    {
        // Kompass auch in Sandbox anzeigen (BuildBounds gibt dann 0..Size zurück)
        if (_project.BuildZone.SizeVoxels.X <= 0 || _project.BuildZone.SizeVoxels.Y <= 0) return;

        double z = GridZ + 0.01;

        GetBuildBounds(out double minX, out double maxX, out double minY, out double maxY);

        double cx = (minX + maxX) / 2.0;
        double cy = (minY + maxY) / 2.0;

        (string Text, Point3D Pos)[] labels =
        {
            ("N", new Point3D(cx, maxY + CompassOutsideMargin, z)),
            ("S", new Point3D(cx, minY - CompassOutsideMargin, z)),
            ("E", new Point3D(maxX + CompassOutsideMargin, cy, z)),
            ("W", new Point3D(minX - CompassOutsideMargin, cy, z)),
        };

        foreach (var (text, pos) in labels)
        {
            root.Children.Add(new BillboardTextVisual3D
            {
                Text = text,
                Position = new Point3D(pos.X + 2.0, pos.Y + 2.0, pos.Z),
                Foreground = Brushes.Black,
                Background = Brushes.Transparent,
                FontSize = 18
            });
        }
    }

    private void DrawPiece(ModelVisual3D root, Piece piece, Point3D position, int rotation, Color color)
    {
        var (width, length, height) = GetVoxelAlignedSize(piece, rotation);

        root.Children.Add(new BoxVisual3D
        {
            Width = width,
            Length = length,
            Height = height,
            Center = new Point3D(
                position.X + width / 2.0,
                position.Y + length / 2.0,
                position.Z + height / 2.0
            ),
            Fill = new SolidColorBrush(color)
        });
    }

    private void DrawPiece(ModelVisual3D root, Piece piece, Point3D position, int rotation, MaterialId material)
    {
        var (width, length, height) = GetVoxelAlignedSize(piece, rotation);

        root.Children.Add(new BoxVisual3D
        {
            Width = width,
            Length = length,
            Height = height,
            Center = new Point3D(
                position.X + width / 2.0,
                position.Y + length / 2.0,
                position.Z + height / 2.0
            ),
            Fill = GetTileBrush(material)
        });
    }




    // ============================================================
    // Voxel-Rule: Alles ist voxel-grid-aligned.
    // - Pos ist immer Min-Corner (Voxel-Ecke) in "Voxel Units"
    // - Piece-Size wird auf ganze Voxels normalisiert
    // ============================================================
    private const double VoxelEpsilon = 0.0001;

    private static double ToWholeVoxels(double v)
    {
        // Hart auf ganze Voxel runden (robust gegen 0.999999 / 1.000001 etc.)
        // Falls du später absichtlich halbe Voxels erlauben willst: hier zentral ändern.
        return Math.Max(0.0, Math.Round(v));
    }

    private static (double W, double L, double H) GetVoxelAlignedSize(Piece piece, int rotation)
    {
        var (w, l, h) = RotatedSize(piece, rotation);

        // zentrale Regel: Size muss in ganzen Voxels sein
        w = ToWholeVoxels(w);
        l = ToWholeVoxels(l);
        h = ToWholeVoxels(h);

        // Safety: niemals 0
        if (w < 1) w = 1;
        if (l < 1) l = 1;
        if (h < 1) h = 1;

        return (w, l, h);
    }


    private bool TryGetEraseVolume(out int x, out int y, out int z, out int w, out int l, out int h)
    {
        // Position immer voxel-grid-aligned (Min-Corner)
        var p = SnapToGrid(_mouseWorldPos);
        x = (int)p.X;
        y = (int)p.Y;
        z = (int)p.Z;

        // 1) Kein Piece gewählt => 1 einzelner Voxel
        if (_selectedPiece == null)
        {
            w = 1;
            l = 1;
            h = 1;
            return true;
        }

        // 2) Piece gewählt => Remove-Brush = Voxel-AABB des ausgewählten Pieces
        // Rotation kommt aus _previewRotY (Q/E drehen den Brush)
        var (wd, ld, hd) = GetVoxelAlignedSize(_selectedPiece, _previewRotY);

        // Sicherheit: ints
        w = Math.Max(1, (int)Math.Round(wd));
        l = Math.Max(1, (int)Math.Round(ld));
        h = Math.Max(1, (int)Math.Round(hd));
        return true;
    }

    private static void DrawWireBox(ModelVisual3D root, double x, double y, double z, double w, double l, double h, Color color)
    {
        // 12 Kanten => 12 Segmente => 24 Punkte (paarweise)
        double x0 = x, x1 = x + w;
        double y0 = y, y1 = y + l;
        double z0 = z, z1 = z + h;

        root.Children.Add(new LinesVisual3D
        {
            Color = color,
            Thickness = 2.0,
            Points = new Point3DCollection
            {
                // Bottom rectangle (z0)
                new Point3D(x0,y0,z0), new Point3D(x1,y0,z0),
                new Point3D(x1,y0,z0), new Point3D(x1,y1,z0),
                new Point3D(x1,y1,z0), new Point3D(x0,y1,z0),
                new Point3D(x0,y1,z0), new Point3D(x0,y0,z0),

                // Top rectangle (z1)
                new Point3D(x0,y0,z1), new Point3D(x1,y0,z1),
                new Point3D(x1,y0,z1), new Point3D(x1,y1,z1),
                new Point3D(x1,y1,z1), new Point3D(x0,y1,z1),
                new Point3D(x0,y1,z1), new Point3D(x0,y0,z1),

                // Vertical edges
                new Point3D(x0,y0,z0), new Point3D(x0,y0,z1),
                new Point3D(x1,y0,z0), new Point3D(x1,y0,z1),
                new Point3D(x1,y1,z0), new Point3D(x1,y1,z1),
                new Point3D(x0,y1,z0), new Point3D(x0,y1,z1),
            }
        });
    }

    private const int PieceBaseYawDeg = 90; // <- Basis-Korrektur für Library->World (TopView-Achsen)

    private static (double width, double length, double height) RotatedSize(Piece piece, int rotY)
    {
        // Rotation bezieht sich auf das Voxel-Grid (User/QE).
        // Basis-Korrektur (PieceBaseYawDeg) gilt NUR für gespeicherte Piece-RotY (Meta/Export),
        // nicht für die Voxel-AABB/Preview/Baking. Dadurch ist "0°" immer +X-Aufbau.
        int r = ((rotY % 360) + 360) % 360;

        return r switch
        {
            90 or 270 => (piece.Size.Y, piece.Size.X, piece.Size.Z),
            _ => (piece.Size.X, piece.Size.Y, piece.Size.Z)
        };
    }



    // ======= Undo/Redo =======
    private void ExecuteCommand(IEditorCommand cmd)
    {
        cmd.Do();
        MarkDirty();
        _undo.Push(cmd);

        while (_undo.Count > MaxUndo)
        {
            var items = _undo.Reverse().Skip(1).ToList();
            _undo.Clear();
            foreach (var c in items) _undo.Push(c);
        }

        _redo.Clear();
        RedrawAll();
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var cmd = _undo.Pop();
        cmd.Undo();
        _redo.Push(cmd);
        RedrawAll();
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        var cmd = _redo.Pop();
        cmd.Do();
        _undo.Push(cmd);
        RedrawAll();
    }

    // ======= Save/Load =======

    private void SyncProjectVoxelsFromPaintMap()
    {
        _project.Voxels.Clear();
        foreach (var kv in _paintVoxels)
        {
            var (x, y, z) = kv.Key;
            _project.Voxels.Add(new VoxelPaint
            {
                X = x,
                Y = y,
                Z = z,
                Material = (int)kv.Value
            });
        }
    }

    private void LoadPaintMapFromProject()
    {
        _paintVoxels.Clear();
        if (_project.Voxels == null) return;

        foreach (var v in _project.Voxels)
        {
            // Letzter gewinnt bei Duplikaten
            _paintVoxels[(v.X, v.Y, v.Z)] = (MaterialId)v.Material;
        }
    }


    // ======= Status + Folders =======
    private DispatcherTimer? _statusTimer;

    private static string ResolveDataRootFolder()
    {
        // Versuche im Debug/Dev-Fall den Projekt-/Solution-Ordner zu finden (dort sollen Blueprints liegen).
        // Falls nichts gefunden wird: App-Bin-Ordner verwenden (Deploy-freundlich).
        try
        {
            string? dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6 && !string.IsNullOrWhiteSpace(dir); i++)
            {
                if (System.IO.Directory.EnumerateFiles(dir, "*.sln").Any()
                    || System.IO.Directory.EnumerateFiles(dir, "*.csproj").Any())
                {
                    return dir!;
                }

                dir = System.IO.Directory.GetParent(dir)?.FullName;
            }
        }
        catch { }

        return AppContext.BaseDirectory;
    }

    private void EnsureDataFolders()
    {
        try
        {
            System.IO.Directory.CreateDirectory(BlueprintsFolder);
            System.IO.Directory.CreateDirectory(ExportImportFolder);
        }
        catch
        {
            // Ignorieren: wenn das im Deploy schiefgeht, zeigen Dialoge trotzdem den Standardpfad an.
        }
    }

    private void SetStatus(string text, int autoClearMs = 4000)
    {
        if (StatusTextBlock != null)
            StatusTextBlock.Text = text;

        _statusTimer?.Stop();
        if (autoClearMs <= 0) return;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(autoClearMs) };
        _statusTimer.Tick += (_, __) =>
        {
            _statusTimer?.Stop();
            _statusTimer = null;
            if (StatusTextBlock != null)
                StatusTextBlock.Text = "Bereit";
        };
        _statusTimer.Start();
    }

    private void UpdateStatusFileLabel()
    {
        if (StatusFileTextBlock == null) return;

        string name = string.IsNullOrWhiteSpace(_lastBlueprintPath)
            ? "(kein Blueprint)"
            : System.IO.Path.GetFileName(_lastBlueprintPath);

        StatusFileTextBlock.Text = _isDirty ? $"{name} *" : name;
    }

    private void SetDirty(bool value)
    {
        _isDirty = value;
        UpdateStatusFileLabel();
    }

    private void MarkDirty() => SetDirty(true);

    private void SaveBlueprint() => TrySaveBlueprint(forceDialog: false);

    private void SaveBlueprintAs() => TrySaveBlueprint(forceDialog: true);

    private bool TrySaveBlueprint(bool forceDialog)
    {
        try
        {
            EnsureDataFolders();

            // Snapshot der PaintVoxels in _project (damit sie mitgespeichert werden)
            SyncProjectVoxelsFromPaintMap();

            // Versionsfeld setzen (Migration/Kompatibilitaet)
            _project.SchemaVersion = CurrentProjectSchemaVersion;

            string? path = (!forceDialog && !string.IsNullOrWhiteSpace(_lastBlueprintPath)) ? _lastBlueprintPath : null;

            if (string.IsNullOrWhiteSpace(path))
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "Blueprint Files (*.json)|*.json",
                    DefaultExt = "json",
                    InitialDirectory = BlueprintsFolder,
                    FileName = string.IsNullOrWhiteSpace(_lastBlueprintPath)
                        ? "blueprint.json"
                        : Path.GetFileName(_lastBlueprintPath)
                };

                if (dialog.ShowDialog() != true)
                    return false; // abgebrochen

                path = dialog.FileName;
            }

            string json = JsonConvert.SerializeObject(_project, Formatting.Indented);
            File.WriteAllText(path, json);

            _lastBlueprintPath = path;

            SetDirty(false);
            SetStatus($"Gespeichert: {Path.GetFileName(path)}  (Pieces: {_project.PlacedPieces.Count}, Voxels: {_project.Voxels.Count})");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Speichern fehlgeschlagen:\n" + ex.ToString(),
                "Fehler",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    // ======= Import/Export? =======
    private BlueprintSnippet? _pendingImportSnippet = null;
    private bool _importArmed = false;
    private int _importRotY = 0; // Q/E drehen im Import-Modus


    private void BeginImportSnippet()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Snippet (*.json)|*.json",
            DefaultExt = "json",
            InitialDirectory = ExportImportFolder,
        };

        if (dialog.ShowDialog() != true) return;

        string json = File.ReadAllText(dialog.FileName);
        var snippet = JsonConvert.DeserializeObject<BlueprintSnippet>(json);

        if (snippet == null)
        {
            MessageBox.Show("Snippet konnte nicht geladen werden.", "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _pendingImportSnippet = snippet;
        _importArmed = true;
        _importRotY = 0;

        // WICHTIG: Preview sofort aktiv
        _selectedPiece = null;

        MessageBox.Show(
            "Import aktiv:\nLinksklick = platzieren\nESC = abbrechen",
            "Import",
            MessageBoxButton.OK,
            MessageBoxImage.Information
        );
    }

    private (int minX, int minY, int minZ) GetSnippetMinBounds(BlueprintSnippet snip)
    {
        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;

        // Pieces
        foreach (var p in snip.Pieces)
        {
            minX = Math.Min(minX, (int)Math.Floor(p.Pos.X));
            minY = Math.Min(minY, (int)Math.Floor(p.Pos.Y));
            minZ = Math.Min(minZ, (int)Math.Floor(p.Pos.Z));
        }

        // Voxels
        foreach (var v in snip.Voxels)
        {
            minX = Math.Min(minX, v.X);
            minY = Math.Min(minY, v.Y);
            minZ = Math.Min(minZ, v.Z);
        }

        if (minX == int.MaxValue) minX = 0;
        if (minY == int.MaxValue) minY = 0;
        if (minZ == int.MaxValue) minZ = 0;

        return (minX, minY, minZ);
    }

    private void ExportSnippet()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Snippet (*.json)|*.json",
            DefaultExt = "json",
            InitialDirectory = ExportImportFolder,
            FileName = "snippet.json"
        };

        if (dialog.ShowDialog() != true) return;

        var snippet = new BlueprintSnippet();

        // Pieces exportieren
        snippet.Pieces.AddRange(_project.PlacedPieces);

        // Voxel-Pinsel exportieren
        foreach (var kv in _paintVoxels)
        {
            var (x, y, z) = kv.Key;
            snippet.Voxels.Add(new VoxelPaint
            {
                X = x,
                Y = y,
                Z = z,
                Material = (int)kv.Value
            });
        }

        string json = JsonConvert.SerializeObject(snippet, Formatting.Indented);
        File.WriteAllText(dialog.FileName, json);

        MessageBox.Show("Snippet exportiert!", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void LoadBlueprint()
    {
        EnsureDataFolders();

        var dialog = new OpenFileDialog
        {
            Filter = "Blueprint Files (*.json)|*.json",
            DefaultExt = "json",
            InitialDirectory = BlueprintsFolder,
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            string json = File.ReadAllText(dialog.FileName);
            var loaded = JsonConvert.DeserializeObject<ProjectData>(json);

            if (loaded == null)
            {
                MessageBox.Show(
                    "Blueprint konnte nicht geladen werden (leere/ungueltige Datei).",
                    "Fehler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            // Migration/Defaults
            if (loaded.SchemaVersion <= 0)
                loaded.SchemaVersion = CurrentProjectSchemaVersion;

            loaded.PlacedPieces ??= new List<PlacedPiece>();
            loaded.Voxels ??= new List<VoxelPaint>();
            loaded.BuildZone ??= new BuildZone();

            _project = loaded;
            _lastBlueprintPath = dialog.FileName;

            // Undo/Redo nach Laden zuruecksetzen
            _undo.Clear();
            _redo.Clear();

            // PaintVoxels aus dem geladenen Projekt wiederherstellen
            LoadPaintMapFromProject();

            _altarPlaced = _project.AltarPlaced;
            _altarBuildSizeVox = _project.AltarBuildSizeVox;

            _altarCenter = new Point3D(
                _project.BuildZone.SizeVoxels.X / 2.0,
                _project.BuildZone.SizeVoxels.Y / 2.0,
                HalfHeight
            );

            RefreshProjectInfoText();

            // Layer-UI an neue Z-Hoehe anpassen
            SliderLayer.Minimum = 0;
            SliderLayer.Maximum = _project.BuildZone.SizeVoxels.Z - 1;
            if (SliderLayer.Value > SliderLayer.Maximum) SliderLayer.Value = SliderLayer.Maximum;
            if (SliderLayer.Value < SliderLayer.Minimum) SliderLayer.Value = SliderLayer.Minimum;
            UpdateLayerTexts();

            InvalidateGridCache();
            SetupViewports();
            RedrawAll();

            SetDirty(false);
            SetStatus($"Geladen: {Path.GetFileName(dialog.FileName)}  (Pieces: {_project.PlacedPieces.Count}, Voxels: {_project.Voxels.Count})");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Laden fehlgeschlagen:\n" + ex.ToString(),
                "Fehler",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ImportBlueprintAdditive()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Blueprint Files (*.json)|*.json",
            DefaultExt = "json",
            InitialDirectory = BlueprintsFolder
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            string json = File.ReadAllText(dialog.FileName);
            var loaded = JsonConvert.DeserializeObject<ProjectData>(json);
            if (loaded == null)
            {
                MessageBox.Show("Blueprint konnte nicht geladen werden (ungueltige Datei).", "Import", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Pieces hinzufügen
            foreach (var pp in loaded.PlacedPieces)
                _project.PlacedPieces.Add(pp);

            // Voxels mergen (PaintVoxels)
            if (loaded.Voxels != null)
            {
                foreach (var v in loaded.Voxels)
                    _paintVoxels[(v.X, v.Y, v.Z)] = (MaterialId)v.Material;
            }

            MarkDirty();
            RedrawAll();
            MessageBox.Show("Blueprint wurde zum aktuellen Projekt hinzugefügt.", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Import fehlgeschlagen:" + ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ======= Import Snippet Helpers (Normalize + Rotate) =======

    private BlueprintSnippet BuildRotatedNormalizedSnippet(BlueprintSnippet original, int rotY)
    {
        var (minX, minY, minZ, maxX, maxY, maxZ) = GetSnippetBounds(original);

        // Leeres Snippet?
        if (maxX < minX || maxY < minY || maxZ < minZ)
            return new BlueprintSnippet();

        // Normalisieren (min -> 0)
        var sn = new BlueprintSnippet();

        foreach (var pp in original.Pieces)
        {
            sn.Pieces.Add(new PlacedPiece
            {
                PieceId = pp.PieceId,
                Pos = new Point3D(pp.Pos.X - minX, pp.Pos.Y - minY, pp.Pos.Z - minZ),
                RotY = pp.RotY,
                Material = pp.Material
            });
        }

        foreach (var v in original.Voxels)
        {
            sn.Voxels.Add(new VoxelPaint
            {
                X = v.X - minX,
                Y = v.Y - minY,
                Z = v.Z - minZ,
                Material = v.Material
            });
        }

        rotY = ((rotY % 360) + 360) % 360;
        if (rotY == 0) return sn;

        int width = (maxX - minX) + 1;   // Voxels
        int height = (maxY - minY) + 1;  // Voxels
        int maxX0 = width - 1;
        int maxY0 = height - 1;

        var rotated = new BlueprintSnippet();

        foreach (var pp in sn.Pieces)
        {
            int x = (int)Math.Floor(pp.Pos.X);
            int y = (int)Math.Floor(pp.Pos.Y);
            int z = (int)Math.Floor(pp.Pos.Z);

            int w = 1, l = 1;
            var def = _library.Pieces.FirstOrDefault(p => p.Id == pp.PieceId);
            if (def != null)
            {
                var (rw, rl, _) = RotatedSize(def, pp.RotY);
                w = Math.Max(1, (int)Math.Round(rw));
                l = Math.Max(1, (int)Math.Round(rl));
            }

            var (nx, ny) = RotateAnchor(x, y, w, l, maxX0, maxY0, rotY);

            rotated.Pieces.Add(new PlacedPiece
            {
                PieceId = pp.PieceId,
                Pos = new Point3D(nx, ny, z),
                RotY = (pp.RotY + rotY) % 360,
                Material = pp.Material
            });
        }

        foreach (var v in sn.Voxels)
        {
            var (nx, ny) = RotateAnchor(v.X, v.Y, 1, 1, maxX0, maxY0, rotY);
            rotated.Voxels.Add(new VoxelPaint { X = nx, Y = ny, Z = v.Z, Material = v.Material });
        }

        return rotated;
    }

    private (int minX, int minY, int minZ, int maxX, int maxY, int maxZ) GetSnippetBounds(BlueprintSnippet snippet)
    {
        bool any = false;

        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;

        foreach (var pp in snippet.Pieces)
        {
            any = true;

            int x = (int)Math.Floor(pp.Pos.X);
            int y = (int)Math.Floor(pp.Pos.Y);
            int z = (int)Math.Floor(pp.Pos.Z);

            int w = 1, l = 1, h = 1;
            var def = _library.Pieces.FirstOrDefault(p => p.Id == pp.PieceId);
            if (def != null)
            {
                var (rw, rl, rh) = RotatedSize(def, pp.RotY);
                w = Math.Max(1, (int)Math.Round(rw));
                l = Math.Max(1, (int)Math.Round(rl));
                h = Math.Max(1, (int)Math.Round(rh));
            }

            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z);

            maxX = Math.Max(maxX, x + w - 1);
            maxY = Math.Max(maxY, y + l - 1);
            maxZ = Math.Max(maxZ, z + h - 1);
        }

        foreach (var v in snippet.Voxels)
        {
            any = true;

            minX = Math.Min(minX, v.X);
            minY = Math.Min(minY, v.Y);
            minZ = Math.Min(minZ, v.Z);

            maxX = Math.Max(maxX, v.X);
            maxY = Math.Max(maxY, v.Y);
            maxZ = Math.Max(maxZ, v.Z);
        }

        if (!any)
            return (0, 0, 0, -1, -1, -1);

        return (minX, minY, minZ, maxX, maxY, maxZ);
    }

    private static (int X, int Y) RotateAnchor(int x, int y, int w, int l, int maxX, int maxY, int rot)
    {
        rot = ((rot % 360) + 360) % 360;

        return rot switch
        {
            0 => (x, y),
            90 => (maxY - (y + l - 1), x),
            180 => (maxX - (x + w - 1), maxY - (y + l - 1)),
            270 => (y, maxX - (x + w - 1)),
            _ => (x, y)
        };
    }

    // ======= Layer =======
    private void SetLayer(int internalZ)
    {
        if (internalZ < 0) internalZ = 0;
        if (internalZ > _project.BuildZone.SizeVoxels.Z - 1) internalZ = _project.BuildZone.SizeVoxels.Z - 1;
        SliderLayer.Value = internalZ;
    }

    private void UpdateLayerTexts()
    {
        int ui = CurrentLayerZ - HalfHeight;
        TxtLayer.Text = ui.ToString();
        TxtLayerInfo.Text = $"(intern Z={CurrentLayerZ})";
        RefreshProjectSummaryText();
    }

    // ======= Default Library =======
    private static PieceLibrary CreateDefaultLibrary()
    {
        return new PieceLibrary
        {
            SchemaVersion = 2,
            Units = new Units { VoxelSizeMeters = 0.5 },
            Categories = new List<Category>
            {
                new Category { Id = "WALL_1M", DisplayName = "Wände 1M" },
                new Category { Id = "ALTAR", DisplayName = "Flammenaltar" }
            },
            Pieces = new List<Piece>
            {
                new Piece
                {
                    Id = "base/wall/1m/panel/full",
                    DisplayName = "Wand 1M (2×4×1)",
                    CategoryId = "WALL_1M",
                    Size = new Size3 { X = 2, Y = 4, Z = 1 }
                }
            }
        };
    }

    // ============================================================
    // [RENDER] Ghost-Voxel Preview (gold) – voxelgenau, kein Offset
    // Anker: (x,y,z) = Min-Corner im Voxelgrid; Ausdehnung nach +X/+Y/+Z
    // ============================================================
    private void DrawGhostVoxelPreview(ModelVisual3D root, int x, int y, int z, int w, int l, int h)
    {
        if (w <= 0 || l <= 0 || h <= 0) return;

        // OPAK (nicht transparent) – Ghost entsteht dadurch, dass wir nur die Außenflächen meshen.
        var brush = new SolidColorBrush(Colors.Goldenrod);
        if (brush.CanFreeze) brush.Freeze();

        var filled = new HashSet<(int X, int Y, int Z)>();
        for (int dz = 0; dz < h; dz++)
            for (int dy = 0; dy < l; dy++)
                for (int dx = 0; dx < w; dx++)
                    filled.Add((x + dx, y + dy, z + dz));

        root.Children.Add(new ModelVisual3D
        {
            Content = BuildGhostVoxelSetModel(filled, brush)
        });
    }

    // ============================================================
    // Ghost-Mesh aus Voxel-Keys (nur sichtbare Außenflächen)
    // - nutzt EIN Model3D statt tausender BoxVisual3D
    // - UVs sind hier egal (einfarbig), deshalb simple Quads

}