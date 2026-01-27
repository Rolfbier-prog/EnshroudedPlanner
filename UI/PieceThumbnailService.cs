using System;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using EnshroudedPlanner;

namespace EnshroudedPlanner.UI
{
    /// <summary>
    /// Generates small, license-safe thumbnails for pieces (no external assets).
    /// Current implementation renders an isometric bounding box based on Piece.Size (X/Y/Z voxels),
    /// styled in the same blue tone as MaterialId.NoMaterialBlue.
    /// </summary>
    public static class PieceThumbnailService
    {
        // Cache by: pieceId + size + requested pixel size
        private static readonly ConcurrentDictionary<string, ImageSource> _cache = new();

        // "Kein Material" Blau (siehe Rendering/Materials/MaterialLookup.cs)
        private static readonly Color BaseBlue = Color.FromRgb(45, 78, 125);

        public static ImageSource GetOrCreate(Piece piece, int pixelSize = 64)
        {
            if (piece == null) return CreatePlaceholder(pixelSize);

            int sx = Math.Max(1, piece.Size?.X ?? 1);
            int sy = Math.Max(1, piece.Size?.Y ?? 1);
            int sz = Math.Max(1, piece.Size?.Z ?? 1);

            string id = string.IsNullOrWhiteSpace(piece.Id) ? piece.DisplayName : piece.Id;
            string key = $"{id}:{sx}x{sy}x{sz}:{pixelSize}";

            return _cache.GetOrAdd(key, _ => CreateIsometricBox(sx, sy, sz, pixelSize));
        }

        private static ImageSource CreatePlaceholder(int pixelSize)
        {
            var dg = new DrawingGroup();
            using (var dc = dg.Open())
            {
                var pen = new Pen(new SolidColorBrush(WithAlpha(BaseBlue, 200)), 1.0);
                pen.Freeze();
                dc.DrawRectangle(null, pen, new Rect(2, 2, pixelSize - 4, pixelSize - 4));
            }
            dg.Freeze();
            var img = new DrawingImage(dg);
            if (img.CanFreeze) img.Freeze();
            return img;
        }

        private static ImageSource CreateIsometricBox(int sx, int sy, int sz, int pixelSize)
        {
            const double cos30 = 0.8660254037844386;
            const double sin30 = 0.5;

            Point P(double x, double y, double z) => new Point((x - y) * cos30, (x + y) * sin30 - z);

            var O  = P(0,  0,  0);
            var B  = P(sx, 0,  0);
            var C  = P(sx, sy, 0);
            var D  = P(0,  sy, 0);

            var Ap = P(0,  0,  sz);
            var Bp = P(sx, 0,  sz);
            var Cp = P(sx, sy, sz);
            var Dp = P(0,  sy, sz);

            var pts = new[] { O, B, C, D, Ap, Bp, Cp, Dp };
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            double w = Math.Max(0.0001, maxX - minX);
            double h = Math.Max(0.0001, maxY - minY);

            double pad = 4.0;
            double scale = (pixelSize - 2 * pad) / Math.Max(w, h);

            Point T(Point p) => new Point((p.X - minX) * scale + pad, (p.Y - minY) * scale + pad);

            // Visible faces for this view: left (y=sy), right (x=sx), top (z=sz)
            var faceLeft  = new[] { T(D), T(C), T(Cp), T(Dp) }; // y=sy
            var faceRight = new[] { T(B), T(C), T(Cp), T(Bp) }; // x=sx
            var faceTop   = new[] { T(Ap), T(Bp), T(Cp), T(Dp) }; // z=sz

            var dg = new DrawingGroup();
            using (var dc = dg.Open())
            {
                var brushTop   = new SolidColorBrush(Lighten(BaseBlue, 1.10));
                var brushRight = new SolidColorBrush(Lighten(BaseBlue, 1.00));
                var brushLeft  = new SolidColorBrush(Lighten(BaseBlue, 0.85));
                brushTop.Freeze(); brushRight.Freeze(); brushLeft.Freeze();

                var outline = new Pen(new SolidColorBrush(Lighten(BaseBlue, 0.65)), 1.0);
                outline.Freeze();

                dc.DrawGeometry(brushLeft, outline, Poly(faceLeft));
                dc.DrawGeometry(brushRight, outline, Poly(faceRight));
                dc.DrawGeometry(brushTop, outline, Poly(faceTop));
            }

            dg.Freeze();
            var img = new DrawingImage(dg);
            if (img.CanFreeze) img.Freeze();
            return img;
        }

        private static Geometry Poly(Point[] pts)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(pts[0], isFilled: true, isClosed: true);
                for (int i = 1; i < pts.Length; i++)
                    ctx.LineTo(pts[i], isStroked: true, isSmoothJoin: true);
            }
            g.Freeze();
            return g;
        }

        private static Color Lighten(Color c, double factor)
        {
            byte L(byte v)
            {
                int nv = (int)Math.Round(v * factor);
                if (nv < 0) nv = 0;
                if (nv > 255) nv = 255;
                return (byte)nv;
            }
            return Color.FromRgb(L(c.R), L(c.G), L(c.B));
        }

        private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    }
}