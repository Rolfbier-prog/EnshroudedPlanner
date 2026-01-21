#nullable enable
using System.Windows.Media.Media3D;
using EnshroudedPlanner.Rendering.Materials;

namespace EnshroudedPlanner.Rendering.Voxels
{
    public static class VoxelChunkVisual
    {
        public static Model3DGroup BuildModel(VoxelChunk chunk, MaterialLookup mats)
        {
            var group = new Model3DGroup();

            var built = VoxelMesherGreedy.Build(chunk);

            foreach (var kv in built.Meshes)
            {
                int matId = kv.Key;
                MeshGeometry3D mesh = kv.Value;

                if (matId <= 0) continue;

                var mat = mats.Get((MaterialId)(matId - 1));

                var gm = new GeometryModel3D
                {
                    Geometry = mesh,
                    Material = mat,
                    BackMaterial = mat
                };

                group.Children.Add(gm);
            }

            if (group.CanFreeze) group.Freeze();
            return group;
        }
    }
}
