using System.Collections.Generic;

namespace RealtimeGI
{
    public static class GISceneRegistry
    {
        static readonly List<GIMeshInstance> meshInstances = new List<GIMeshInstance>(256);
        static readonly List<GISkinnedMeshInstance> skinnedMeshInstances = new List<GISkinnedMeshInstance>(64);
        static int revision = 1;

        public static IReadOnlyList<GIMeshInstance> MeshInstances => meshInstances;
        public static IReadOnlyList<GISkinnedMeshInstance> SkinnedMeshInstances => skinnedMeshInstances;
        public static int Revision => revision;

        internal static void Register(GIMeshInstance instance)
        {
            if (instance == null || meshInstances.Contains(instance))
                return;
            meshInstances.Add(instance);
            revision++;
        }

        internal static void Unregister(GIMeshInstance instance)
        {
            if (instance != null && meshInstances.Remove(instance))
                revision++;
        }

        internal static void Register(GISkinnedMeshInstance instance)
        {
            if (instance == null || skinnedMeshInstances.Contains(instance))
                return;
            skinnedMeshInstances.Add(instance);
            revision++;
        }

        internal static void Unregister(GISkinnedMeshInstance instance)
        {
            if (instance != null && skinnedMeshInstances.Remove(instance))
                revision++;
        }

        public static void NotifyChanged()
        {
            revision++;
        }
    }
}
