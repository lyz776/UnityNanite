using UnityEngine;

namespace Nanite
{
    /// <summary>
    /// Resources-backed ownership for compute assets used through string kernel
    /// lookup. Direct RendererFeature references remain the primary path; this
    /// asset prevents Player stripping and repairs stale renderer serialization.
    /// </summary>
    public sealed class NaniteRuntimeComputeResources : ScriptableObject
    {
        public ComputeShader gpuCullingShader;
        public ComputeShader visibleTriangleCompactShader;
        public ComputeShader pageTranscodeShader;
        public ComputeShader hzbBuilderShader;
        public ComputeShader materialTileClassifyShader;

        public static NaniteRuntimeComputeResources Load() =>
            Resources.Load<NaniteRuntimeComputeResources>(nameof(NaniteRuntimeComputeResources));
    }
}
