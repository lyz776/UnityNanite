using UnityEngine;

namespace UnityNanite.GI
{
    /// <summary>
    /// Marks a non-Rigidbody renderer whose Transform must be tracked by the GI world cache.
    /// Unmarked renderers are treated as static after scene collection.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Unity Nanite/GI/Transform Tracked")]
    public sealed class GITransformTracked : MonoBehaviour
    {
    }
}
