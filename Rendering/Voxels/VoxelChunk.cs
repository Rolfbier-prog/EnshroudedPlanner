#nullable enable
using System;

namespace EnshroudedPlanner.Rendering.Voxels
{
    public sealed class VoxelChunk
    {
        public readonly int SizeX;
        public readonly int SizeY;
        public readonly int SizeZ;

        // Welt-Offset (Chunk origin) in Voxeln
        public readonly int OriginX;
        public readonly int OriginY;
        public readonly int OriginZ;

        // 0 = leer, sonst MaterialId (int)
        private readonly int[,,] _vox;

        public VoxelChunk(int originX, int originY, int originZ, int sx, int sy, int sz)
        {
            OriginX = originX; OriginY = originY; OriginZ = originZ;
            SizeX = sx; SizeY = sy; SizeZ = sz;
            _vox = new int[sx, sy, sz];
        }

        public int Get(int x, int y, int z) => _vox[x, y, z];
        public void Set(int x, int y, int z, int matId) => _vox[x, y, z] = matId;

        public bool InBounds(int x, int y, int z)
            => x >= 0 && y >= 0 && z >= 0 && x < SizeX && y < SizeY && z < SizeZ;
    }
}
