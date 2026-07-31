using UnityEngine;

namespace RealtimeGI
{
    /// <summary>
    /// Optional per-object policy shared by normal Mesh and Nanite adapters. Nanite objects are
    /// discovered automatically; add this component only when the defaults need to be overridden.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GIObjectSettings : MonoBehaviour
    {
        public bool occluder = true;
        public bool contributor = true;
        public bool receiver = true;
        public bool twoSided;
        public bool alphaTested;
        public GIMobility mobility = GIMobility.Auto;
        [Min(0)] public int contentRevision;

        public GIInstanceFlags ResolveFlags(bool hasEmissive)
        {
            GIInstanceFlags flags = GIInstanceFlags.None;
            if (occluder) flags |= GIInstanceFlags.Occluder;
            if (contributor) flags |= GIInstanceFlags.Contributor;
            if (receiver) flags |= GIInstanceFlags.Receiver;
            if (twoSided) flags |= GIInstanceFlags.TwoSided;
            if (alphaTested) flags |= GIInstanceFlags.AlphaTested;
            if (hasEmissive) flags |= GIInstanceFlags.Emissive;

            bool dynamic = mobility == GIMobility.Dynamic ||
                           (mobility == GIMobility.Auto && !gameObject.isStatic);
            if (dynamic) flags |= GIInstanceFlags.Dynamic;
            return flags;
        }

        void OnValidate()
        {
            contentRevision = Mathf.Max(0, contentRevision);
            GISceneRegistry.NotifyChanged();
        }
    }
}
