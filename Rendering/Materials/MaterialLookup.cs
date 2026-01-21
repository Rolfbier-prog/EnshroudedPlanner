#nullable enable
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace EnshroudedPlanner.Rendering.Materials
{
    /// <summary>
    /// Lädt das Atlas-PNG und liefert pro MaterialId ein Helix/WPF Material.
    /// Tiles sind 4x3 im Atlas (wie dein atlas_4x3_32px.png).
    /// </summary>
    public sealed class MaterialLookup
    {
        private readonly ImageSource _atlas;
        private readonly int _tilesX;
        private readonly int _tilesY;

        private readonly Dictionary<MaterialId, Material> _cache = new();

        /// <param name="atlasPackUri">z.B. "pack://application:,,,/Assets/Textures/atlas_4x3_32px.png"</param>
        public MaterialLookup(string atlasPackUri, int tilesX = 4, int tilesY = 3)
        {
            _tilesX = tilesX;
            _tilesY = tilesY;

            _atlas = new BitmapImage(new Uri(atlasPackUri, UriKind.Absolute));
            if (_atlas.CanFreeze) _atlas.Freeze();
        }

        public Material Get(MaterialId id)
        {
            if (_cache.TryGetValue(id, out var m)) return m;

            // "Kein Material" soll als dunkles, mattes Blau ohne Textur gerendert werden
            // (ähnlich wie der goldene Ghost-Preview – nur eben blau).
            if (id == MaterialId.NoMaterialBlue)
            {
                var nm = BuildNoMaterialBlue();
                _cache[id] = nm;
                return nm;
            }

            // Normales Diffuse-Material mit ImageBrush
            var brush = CreateTileBrush((int)id);
            var diffuse = new DiffuseMaterial(brush);
            if (diffuse.CanFreeze) diffuse.Freeze();

            // OPTIONAL: Emissive für "Glow" (wenn du willst, später fein-tunen)
            // Für jetzt: Glow-Mats bekommen zusätzlich EmissiveMaterial, alles andere nur Diffuse
            Material result = diffuse;
            if (IsGlow(id))
            {
                var e = new EmissiveMaterial(brush);
                if (e.CanFreeze) e.Freeze();

                var group = new MaterialGroup();
                group.Children.Add(diffuse);
                group.Children.Add(e);
                if (group.CanFreeze) group.Freeze();
                result = group;
            }

            _cache[id] = result;
            return result;
        }

        private static Material BuildNoMaterialBlue()
        {
            // Dunkler, matter Blauton (ohne Atlas-Textur)
            var brush = new SolidColorBrush(Color.FromRgb(45, 78, 125));
            if (brush.CanFreeze) brush.Freeze();

            var diffuse = new DiffuseMaterial(brush);
            if (diffuse.CanFreeze) diffuse.Freeze();
            return diffuse;
        }

        private ImageBrush CreateTileBrush(int tileIndex)
        {
            int x = tileIndex % _tilesX;
            int y = tileIndex / _tilesX;

            // Viewbox in RelativeToBoundingBox:
            // X,Y,W,H jeweils 0..1
            double w = 1.0 / _tilesX;
            double h = 1.0 / _tilesY;

            var vb = new Rect(x * w, y * h, w, h);

            var brush = new ImageBrush(_atlas)
            {
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewbox = vb,
                Stretch = Stretch.Fill,
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1)
            };

            // Pixel-Snapping Optik (leicht schärfer), du wolltest es aber "weicher":
            // -> für weich: NICHT SnapsToDevicePixels erzwingen, kein NearestNeighbor.
            // Wenn du später "crisp" willst: RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.Linear);

            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        private static bool IsGlow(MaterialId id)
        {
            return id == MaterialId.GlowYellow
                || id == MaterialId.GlowBlue
                || id == MaterialId.GlowRed;
        }
    }
}
