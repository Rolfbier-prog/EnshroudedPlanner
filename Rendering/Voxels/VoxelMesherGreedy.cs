#nullable enable
using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;
using System.Windows.Media; // <-- für Int32Collection + PointCollection


namespace EnshroudedPlanner.Rendering.Voxels
{
    public static class VoxelMesherGreedy
    {
        public struct MeshByMaterial
        {
            public Dictionary<int, MeshGeometry3D> Meshes; // matId -> mesh
        }

        public static MeshByMaterial Build(VoxelChunk chunk)
        {
            var dict = new Dictionary<int, MeshGeometry3D>();

            // 6 Directions (Greedy on each axis slice)
            // Wir arbeiten in lokalen Chunk-Koordinaten, addieren später Origin.
            GreedyFaces(chunk, dict, dir: 0); // +X
            GreedyFaces(chunk, dict, dir: 1); // -X
            GreedyFaces(chunk, dict, dir: 2); // +Y
            GreedyFaces(chunk, dict, dir: 3); // -Y
            GreedyFaces(chunk, dict, dir: 4); // +Z
            GreedyFaces(chunk, dict, dir: 5); // -Z

            // Freeze (kleiner Perf-Boost)
            foreach (var m in dict.Values)
            {
                if (m.Positions.CanFreeze) m.Positions.Freeze();
                if (m.TriangleIndices.CanFreeze) m.TriangleIndices.Freeze();
                if (m.TextureCoordinates.CanFreeze) m.TextureCoordinates.Freeze();
                if (m.Normals.CanFreeze) m.Normals.Freeze();
                if (m.CanFreeze) m.Freeze();
            }

            return new MeshByMaterial { Meshes = dict };
        }

        private static MeshGeometry3D GetMesh(Dictionary<int, MeshGeometry3D> dict, int matId)
        {
            if (dict.TryGetValue(matId, out var m)) return m;

            m = new MeshGeometry3D
            {
                Positions = new Point3DCollection(),
                TriangleIndices = new Int32Collection(),
                TextureCoordinates = new PointCollection(),
                Normals = new Vector3DCollection()
            };
            dict[matId] = m;
            return m;
        }

        private static void AddQuad(MeshGeometry3D mesh,
            Point3D a, Point3D b, Point3D c, Point3D d,
            Vector3D n)
        {
            int i0 = mesh.Positions.Count;

            mesh.Positions.Add(a);
            mesh.Positions.Add(b);
            mesh.Positions.Add(c);
            mesh.Positions.Add(d);

            mesh.Normals.Add(n);
            mesh.Normals.Add(n);
            mesh.Normals.Add(n);
            mesh.Normals.Add(n);

            // UV: pro Voxel 0..1, auch wenn Greedy ein großes Quad baut.
            // Länge in Voxeln aus Kanten bestimmen (axis-aligned => integer).
            double uLen = (b - a).Length;
            double vLen = (d - a).Length;
            if (uLen < 1) uLen = 1;
            if (vLen < 1) vLen = 1;

            mesh.TextureCoordinates.Add(new System.Windows.Point(0, vLen));
            mesh.TextureCoordinates.Add(new System.Windows.Point(uLen, vLen));
            mesh.TextureCoordinates.Add(new System.Windows.Point(uLen, 0));
            mesh.TextureCoordinates.Add(new System.Windows.Point(0, 0));

            mesh.TriangleIndices.Add(i0 + 0);
            mesh.TriangleIndices.Add(i0 + 1);
            mesh.TriangleIndices.Add(i0 + 2);

            mesh.TriangleIndices.Add(i0 + 0);
            mesh.TriangleIndices.Add(i0 + 2);
            mesh.TriangleIndices.Add(i0 + 3);
        }

        private static bool IsSolid(int matId) => matId != 0;

        /// <summary>
        /// dir:
        /// 0 +X, 1 -X, 2 +Y, 3 -Y, 4 +Z, 5 -Z
        /// </summary>
        private static void GreedyFaces(VoxelChunk c, Dictionary<int, MeshGeometry3D> dict, int dir)
        {
            // Wir bauen pro dir eine 2D-Maske je Slice.
            // Für "light" reicht: face sichtbar wenn solid und Nachbar in dir leer/out.
            // Die Greedy-Merge läuft in der 2D-Ebene der Slice.

            int sx = c.SizeX, sy = c.SizeY, sz = c.SizeZ;

            // Axis mapping:
            // u,v = 2D Koordinaten in Mask, w = Slice Index
            // plus: normal / face-plane corner layout
            int uMax, vMax, wMax;

            // uAxis, vAxis, wAxis: 0=x,1=y,2=z
            int uAxis, vAxis, wAxis;
            int sign;

            switch (dir)
            {
                case 0: uAxis = 1; vAxis = 2; wAxis = 0; sign = +1; break; // +X, mask is YZ slices
                case 1: uAxis = 1; vAxis = 2; wAxis = 0; sign = -1; break; // -X
                case 2: uAxis = 0; vAxis = 2; wAxis = 1; sign = +1; break; // +Y, mask XZ
                case 3: uAxis = 0; vAxis = 2; wAxis = 1; sign = -1; break; // -Y
                case 4: uAxis = 0; vAxis = 1; wAxis = 2; sign = +1; break; // +Z, mask XY
                case 5: uAxis = 0; vAxis = 1; wAxis = 2; sign = -1; break; // -Z
                default: return;
            }

            uMax = (uAxis == 0) ? sx : (uAxis == 1) ? sy : sz;
            vMax = (vAxis == 0) ? sx : (vAxis == 1) ? sy : sz;
            wMax = (wAxis == 0) ? sx : (wAxis == 1) ? sy : sz;

            // mask: matId (0 = kein Face)
            int[,] mask = new int[uMax, vMax];

            for (int w = 0; w <= wMax; w++)
            {
                // mask füllen
                for (int u = 0; u < uMax; u++)
                    for (int v = 0; v < vMax; v++)
                    {
                        int ax = 0, ay = 0, az = 0;
                        SetAxis(ref ax, ref ay, ref az, uAxis, u);
                        SetAxis(ref ax, ref ay, ref az, vAxis, v);
                        SetAxis(ref ax, ref ay, ref az, wAxis, w);

                        int bx = ax, by = ay, bz = az;
                        // neighbor sample: w-1 or w depending on sign (classic greedy plane)
                        // Wir prüfen Voxel auf der "inneren" Seite der Plane.
                        // plane at w, face uses voxel at w-1 (sign+), bzw voxel at w (sign-)
                        int vx, vy, vz, nx, ny, nz;

                        if (sign == +1)
                        {
                            vx = GetAxis(ax, ay, az, wAxis) - 1;
                            vy = GetAxis(ax, ay, az, 1);
                            vz = GetAxis(ax, ay, az, 2);

                            // neighbor = voxel at w
                            nx = GetAxis(ax, ay, az, 0);
                            ny = GetAxis(ax, ay, az, 1);
                            nz = GetAxis(ax, ay, az, 2);
                        }
                        else
                        {
                            // face looks -axis, voxel is at w
                            vx = GetAxis(ax, ay, az, wAxis);
                            vy = GetAxis(ax, ay, az, 1);
                            vz = GetAxis(ax, ay, az, 2);

                            // neighbor = voxel at w-1
                            nx = GetAxis(ax, ay, az, wAxis) - 1;
                            ny = GetAxis(ax, ay, az, 1);
                            nz = GetAxis(ax, ay, az, 2);
                        }

                        int solid = Sample(c, uAxis, vAxis, wAxis, u, v, w, sign, isSolid: true);
                        mask[u, v] = solid; // solid enthält matId oder 0
                    }

                // greedy merge der Maske
                bool[,] used = new bool[uMax, vMax];

                for (int u0 = 0; u0 < uMax; u0++)
                    for (int v0 = 0; v0 < vMax; v0++)
                    {
                        int matId = mask[u0, v0];
                        if (matId == 0 || used[u0, v0]) continue;

                        int u1 = u0;
                        int v1 = v0;

                        // width
                        int wLen = 1;
                        while (u0 + wLen < uMax && !used[u0 + wLen, v0] && mask[u0 + wLen, v0] == matId)
                            wLen++;

                        // height
                        int hLen = 1;
                        bool canGrow = true;
                        while (v0 + hLen < vMax && canGrow)
                        {
                            for (int uu = 0; uu < wLen; uu++)
                            {
                                if (used[u0 + uu, v0 + hLen] || mask[u0 + uu, v0 + hLen] != matId)
                                {
                                    canGrow = false;
                                    break;
                                }
                            }
                            if (canGrow) hLen++;
                        }

                        // mark used
                        for (int uu = 0; uu < wLen; uu++)
                            for (int vv = 0; vv < hLen; vv++)
                                used[u0 + uu, v0 + vv] = true;

                        // Quad in Weltkoordinaten
                        var mesh = GetMesh(dict, matId);

                        // Normal
                        Vector3D n = dir switch
                        {
                            0 => new Vector3D(1, 0, 0),
                            1 => new Vector3D(-1, 0, 0),
                            2 => new Vector3D(0, 1, 0),
                            3 => new Vector3D(0, -1, 0),
                            4 => new Vector3D(0, 0, 1),
                            _ => new Vector3D(0, 0, -1),
                        };

                        // plane position in local coords:
                        // The face lies on the plane w (for + dir), or w (for - dir) too, but vertex order differs.
                        // Wir setzen die 4 Eckpunkte anhand u/v Rechteck und dem Slice w.
                        AddFaceQuad(c, mesh, dir, uAxis, vAxis, wAxis, w, u0, v0, wLen, hLen, n);
                    }
            }
        }

        private static void AddFaceQuad(VoxelChunk c, MeshGeometry3D mesh,
            int dir, int uAxis, int vAxis, int wAxis,
            int w, int u0, int v0, int wLen, int hLen,
            Vector3D n)
        {
            // local coordinate corners
            // rectangle corners in (u,v) with size (wLen,hLen)
            // plane at w in local chunk coords
            double wu = u0;
            double wv = v0;
            double wu2 = u0 + wLen;
            double wv2 = v0 + hLen;

            // plane coordinate:
            double ww = w;

            // convert (u,v,w) -> (x,y,z)
            Point3D P(double u, double v, double wCoord)
            {
                double x = 0, y = 0, z = 0;
                SetAxisD(ref x, ref y, ref z, uAxis, u);
                SetAxisD(ref x, ref y, ref z, vAxis, v);
                SetAxisD(ref x, ref y, ref z, wAxis, wCoord);

                // add chunk origin
                return new Point3D(x + c.OriginX, y + c.OriginY, z + c.OriginZ);
            }

            // Für negative Richtung müssen wir das Quad „auf die andere Seite“ legen
            // und die Winding so wählen, dass Normal nach außen zeigt.
            if (dir == 1 || dir == 3 || dir == 5)
            {
                // -axis faces lie on plane at w (same), aber wir drehen die Eck-Reihenfolge
                var a = P(wu, wv, ww);
                var b = P(wu, wv2, ww);
                var c1 = P(wu2, wv2, ww);
                var d = P(wu2, wv, ww);
                AddQuad(mesh, a, b, c1, d, n);
            }
            else
            {
                // +axis
                var a = P(wu, wv, ww);
                var b = P(wu2, wv, ww);
                var c1 = P(wu2, wv2, ww);
                var d = P(wu, wv2, ww);
                AddQuad(mesh, a, b, c1, d, n);
            }
        }

        private static int Sample(VoxelChunk c, int uAxis, int vAxis, int wAxis, int u, int v, int w, int sign, bool isSolid)
        {
            // face visible if voxel solid on one side and empty on other
            // For +dir: voxel at w-1, neighbor at w
            // For -dir: voxel at w, neighbor at w-1

            int vx = 0, vy = 0, vz = 0;
            int nx = 0, ny = 0, nz = 0;

            // base coords
            SetAxis(ref vx, ref vy, ref vz, uAxis, u);
            SetAxis(ref vx, ref vy, ref vz, vAxis, v);
            SetAxis(ref vx, ref vy, ref vz, wAxis, w);

            nx = vx; ny = vy; nz = vz;

            if (sign == +1)
            {
                // voxel on negative side
                SetAxis(ref vx, ref vy, ref vz, wAxis, w - 1);
                // neighbor stays at w
            }
            else
            {
                // neighbor on negative side
                SetAxis(ref nx, ref ny, ref nz, wAxis, w - 1);
                // voxel stays at w
            }

            int vMat = c.InBounds(vx, vy, vz) ? c.Get(vx, vy, vz) : 0;
            int nMat = c.InBounds(nx, ny, nz) ? c.Get(nx, ny, nz) : 0;

            if (IsSolid(vMat) && !IsSolid(nMat))
                return vMat;

            return 0;
        }

        private static void SetAxis(ref int x, ref int y, ref int z, int axis, int val)
        {
            if (axis == 0) x = val;
            else if (axis == 1) y = val;
            else z = val;
        }

        private static void SetAxisD(ref double x, ref double y, ref double z, int axis, double val)
        {
            if (axis == 0) x = val;
            else if (axis == 1) y = val;
            else z = val;
        }

        private static int GetAxis(int x, int y, int z, int axis)
            => axis == 0 ? x : axis == 1 ? y : z;
    }
}
