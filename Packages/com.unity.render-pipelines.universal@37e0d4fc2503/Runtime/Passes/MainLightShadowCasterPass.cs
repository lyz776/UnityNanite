using System;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;

namespace UnityEngine.Rendering.Universal.Internal
{
    /// <summary>
    /// Renders a shadow map for the main Light.
    /// </summary>
    public partial class MainLightShadowCasterPass : ScriptableRenderPass
    {
        // Internal
        internal RTHandle m_MainLightShadowmapTexture;

        // Private
        private int m_RenderTargetWidth;
        private int m_RenderTargetHeight;
        private int m_ShadowCasterCascadesCount;
        private bool m_CreateEmptyShadowmap;
        private bool m_SetKeywordForEmptyShadowmap;
        private bool m_HasExternalShadowCasters;
        private bool m_HasRendererShadowCasters;
        private bool m_LoggedExternalCascadeFallback;
        private bool m_LoggedCasterOwnership;
        private float m_CascadeBorder;
        private float m_MaxShadowDistanceSq;
        private RenderTextureDescriptor m_MainLightShadowDescriptor;
        private readonly Vector4[] m_CascadeSplitDistances;
        private readonly Matrix4x4[] m_MainLightShadowMatrices;
        private readonly ProfilingSampler m_ProfilingSetupSampler = new("Setup Main Shadowmap");
        private readonly ProfilingSampler m_ExternalShadowCullProfilingSampler = new("Nanite Main Light Shadow Cull");
        private readonly ShadowSliceData[] m_CascadeSlices;

        // Constants and Statics
        private const int k_EmptyShadowMapDimensions = 1;
        private const int k_MaxCascades = 4;
        private const int k_ShadowmapBufferBits = 16;
        private const string k_MainLightShadowMapTextureName = "_MainLightShadowmapTexture";
        private static Vector4 s_EmptyShadowParams = new(0f, 0f, 1f, 0f);
        private static readonly Vector4 s_EmptyShadowmapSize = new(k_EmptyShadowMapDimensions, 1f / k_EmptyShadowMapDimensions, k_EmptyShadowMapDimensions, k_EmptyShadowMapDimensions);

#if URP_COMPATIBILITY_MODE
        private bool m_EmptyShadowmapNeedsClear;
        private RTHandle m_EmptyMainLightShadowmapTexture;
        private const string k_EmptyMainLightShadowMapTextureName = "_EmptyMainLightShadowmapTexture";
        private PassData m_PassData;
#endif

        // Classes
        private static class MainLightShadowConstantBuffer
        {
            public static readonly int _WorldToShadow = Shader.PropertyToID("_MainLightWorldToShadow");
            public static readonly int _ShadowParams = Shader.PropertyToID("_MainLightShadowParams");
            public static readonly int _CascadeShadowSplitSpheres0 = Shader.PropertyToID("_CascadeShadowSplitSpheres0");
            public static readonly int _CascadeShadowSplitSpheres1 = Shader.PropertyToID("_CascadeShadowSplitSpheres1");
            public static readonly int _CascadeShadowSplitSpheres2 = Shader.PropertyToID("_CascadeShadowSplitSpheres2");
            public static readonly int _CascadeShadowSplitSpheres3 = Shader.PropertyToID("_CascadeShadowSplitSpheres3");
            public static readonly int _CascadeShadowSplitSphereRadii = Shader.PropertyToID("_CascadeShadowSplitSphereRadii");
            public static readonly int _ShadowOffset0 = Shader.PropertyToID("_MainLightShadowOffset0");
            public static readonly int _ShadowOffset1 = Shader.PropertyToID("_MainLightShadowOffset1");
            public static readonly int _ShadowmapSize = Shader.PropertyToID("_MainLightShadowmapSize");
            public static readonly int _MainLightShadowmapID = Shader.PropertyToID(k_MainLightShadowMapTextureName);
        }

        private class PassData
        {
            internal bool emptyShadowmap;
            internal bool setKeywordForEmptyShadowmap;
            internal bool externalOnly;
            internal UniversalRenderingData renderingData;
            internal UniversalCameraData cameraData;
            internal UniversalLightData lightData;
            internal UniversalShadowData shadowData;
            internal MainLightShadowCasterPass pass;
            internal TextureHandle shadowmapTexture;
            internal readonly RendererList[] shadowRendererLists = new RendererList[k_MaxCascades];
            internal readonly RendererListHandle[] shadowRendererListsHandle = new RendererListHandle[k_MaxCascades];
        }

        private class ExternalCullPassData
        {
            internal MainLightShadowCasterPass pass;
            internal Camera camera;
            internal UniversalLightData lightData;
            internal BufferHandle orderingFence;
        }

        /// <summary>
        /// Creates a new <c>MainLightShadowCasterPass</c> instance.
        /// </summary>
        /// <param name="evt">The <c>RenderPassEvent</c> to use.</param>
        /// <seealso cref="RenderPassEvent"/>
        public MainLightShadowCasterPass(RenderPassEvent evt)
        {
            profilingSampler = new ProfilingSampler("Draw Main Light Shadowmap");
            renderPassEvent = evt;

            m_MainLightShadowMatrices = new Matrix4x4[k_MaxCascades + 1];
            m_CascadeSlices = new ShadowSliceData[k_MaxCascades];
            m_CascadeSplitDistances = new Vector4[k_MaxCascades];

#if URP_COMPATIBILITY_MODE
            m_PassData = new PassData();
            m_EmptyShadowmapNeedsClear = true;
#endif
        }

        /// <summary>
        /// Cleans up resources used by the pass.
        /// </summary>
        public void Dispose()
        {
            m_MainLightShadowmapTexture?.Release();

#if URP_COMPATIBILITY_MODE
            m_EmptyMainLightShadowmapTexture?.Release();
#endif
        }

        /// <summary>
        /// Sets up the pass.
        /// </summary>
        /// <param name="renderingData"></param>
        /// <returns>True if the pass should be enqueued, otherwise false.</returns>
        /// <seealso cref="RenderingData"/>
        public bool Setup(ref RenderingData renderingData)
        {
            ContextContainer frameData = renderingData.frameData;
            UniversalRenderingData universalRenderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            UniversalShadowData shadowData = frameData.Get<UniversalShadowData>();
            return Setup(universalRenderingData, cameraData, lightData, shadowData);
        }

        /// <summary>
        /// Sets up the pass.
        /// </summary>
        /// <param name="renderingData">Data containing rendering settings.</param>
        /// <param name="cameraData">Data containing camera settings.</param>
        /// <param name="lightData">Data containing light settings.</param>
        /// <param name="shadowData">Data containing shadow settings.</param>
        /// <returns>True if the pass should be enqueued, otherwise false.</returns>
        /// <seealso cref="RenderingData"/>
        public bool Setup(UniversalRenderingData renderingData, UniversalCameraData cameraData, UniversalLightData lightData, UniversalShadowData shadowData)
        {
            bool shadowsEnabled = shadowData.mainLightShadowsEnabled;
            bool shadowsSupported = shadowData.supportsMainLightShadows;

#if UNITY_EDITOR
            if (CoreUtils.IsSceneLightingDisabled(cameraData.camera))
                return false;
#endif

            using var profScope = new ProfilingScope(m_ProfilingSetupSampler);

            bool stripShadowsOffVariants = cameraData.renderer.stripShadowsOffVariants;

            Clear();
            m_HasExternalShadowCasters = false;
            m_HasRendererShadowCasters = false;
            int shadowLightIndex = lightData.mainLightIndex;
            if (shadowLightIndex == -1 || (cameraData.camera.targetTexture != null && cameraData.camera.targetTexture.format == RenderTextureFormat.Depth))
            {
                if (shadowsEnabled)
                    return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, null, cameraData, shadowData);
                else
                    return false;
            }

            VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];
            Light light = shadowLight.light;
            if (shadowsSupported && light.shadows == LightShadows.None)
                return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);

            if (!shadowsEnabled)
            {
                // If (realtime) shadows are disabled, but the light casts baked shadows, we need to do empty rendering to setup the _MainLightShadowParams uniform,
                // which is also used when sampling baked shadows. This allows for using baked shadows even when realtime shadows are completely disabled.
                if (light.shadows != LightShadows.None &&
                    light.bakingOutput.isBaked &&
                    light.bakingOutput.mixedLightingMode != MixedLightingMode.IndirectOnly &&
                    light.bakingOutput.lightmapBakeType == LightmapBakeType.Mixed)
                {
                    return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);
                }

                return false;
            }

            if (!shadowsSupported)
                return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, null, cameraData, shadowData);

            if (shadowLight.lightType != LightType.Directional)
            {
                Debug.LogWarning("Only directional lights are supported as main light.");
            }

            bool hasRendererShadowCasters =
                renderingData.cullResults.GetShadowCasterBounds(shadowLightIndex, out Bounds _);
            bool hasExternalShadowCasters =
                ExternalShadowCasterRegistry.HasMainLightShadowCasters(cameraData.camera, light);
            if (!hasRendererShadowCasters && !hasExternalShadowCasters)
                return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);

            m_HasExternalShadowCasters = hasExternalShadowCasters;
            m_HasRendererShadowCasters = hasRendererShadowCasters;

            if (!m_LoggedCasterOwnership)
            {
                m_LoggedCasterOwnership = true;
                Debug.Log(
                    $"[Nanite][Shadow] main-light caster ownership: " +
                    $"nativeRendererList={hasRendererShadowCasters}, " +
                    $"externalNanite={hasExternalShadowCasters}, " +
                    $"cascades={shadowData.mainLightShadowCascadesCount}. " +
                    "Each cascade renders the native RendererList first, then appends external Nanite draws.");
            }

            m_ShadowCasterCascadesCount = shadowData.mainLightShadowCascadesCount;
            m_RenderTargetWidth = shadowData.mainLightRenderTargetWidth;
            m_RenderTargetHeight = shadowData.mainLightRenderTargetHeight;

            ref readonly URPLightShadowCullingInfos shadowCullingInfos = ref shadowData.visibleLightsShadowCullingInfos.UnsafeElementAt(shadowLightIndex);

            bool usedExternalCascadeFallback = false;
            for (int cascadeIndex = 0; cascadeIndex < m_ShadowCasterCascadesCount; ++cascadeIndex)
            {
                if (shadowCullingInfos.IsSliceValid(cascadeIndex))
                {
                    ref readonly ShadowSliceData sliceData = ref shadowCullingInfos.slices.UnsafeElementAt(cascadeIndex);
                    m_CascadeSplitDistances[cascadeIndex] = sliceData.splitData.cullingSphere;
                    m_CascadeSlices[cascadeIndex] = sliceData;
                    continue;
                }

                if (!hasExternalShadowCasters ||
                    !TryBuildExternalDirectionalShadowSlice(
                        cameraData,
                        shadowData,
                        ref shadowLight,
                        cascadeIndex,
                        out ShadowSliceData externalSlice))
                {
                    return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);
                }

                m_CascadeSplitDistances[cascadeIndex] = externalSlice.splitData.cullingSphere;
                m_CascadeSlices[cascadeIndex] = externalSlice;
                usedExternalCascadeFallback = true;
            }

            if (usedExternalCascadeFallback && !m_LoggedExternalCascadeFallback)
            {
                m_LoggedExternalCascadeFallback = true;
                Debug.Log(
                    $"[Nanite][Shadow] external directional cascade fallback active: " +
                    $"cascades={m_ShadowCasterCascadesCount}, resolution={shadowData.mainLightShadowResolution}.");
            }

            UpdateTextureDescriptorIfNeeded();

            m_MaxShadowDistanceSq = cameraData.maxShadowDistance * cameraData.maxShadowDistance;
            m_CascadeBorder = shadowData.mainLightShadowCascadeBorder;
            m_CreateEmptyShadowmap = false;
#if URP_COMPATIBILITY_MODE
            useNativeRenderPass = true;
#endif

            return true;
        }

        static bool TryBuildExternalDirectionalShadowSlice(
            UniversalCameraData cameraData,
            UniversalShadowData shadowData,
            ref VisibleLight shadowLight,
            int cascadeIndex,
            out ShadowSliceData slice)
        {
            slice = default;
            Camera camera = cameraData.camera;
            int cascadeCount = shadowData.mainLightShadowCascadesCount;
            int resolution = shadowData.mainLightShadowResolution;
            if (camera == null || cascadeCount <= 0 || resolution <= 0)
                return false;

            float nearClip = Mathf.Max(0.001f, camera.nearClipPlane);
            float farClip = Mathf.Min(camera.farClipPlane, cameraData.maxShadowDistance);
            if (farClip <= nearClip)
                return false;

            Vector3 splits = shadowData.mainLightShadowCascadesSplit;
            float splitStart = cascadeIndex <= 0
                ? 0.0f
                : GetCascadeEnd(cascadeIndex - 1, cascadeCount, splits);
            float splitEnd = GetCascadeEnd(cascadeIndex, cascadeCount, splits);
            float sliceNear = Mathf.Lerp(nearClip, farClip, splitStart);
            float sliceFar = Mathf.Lerp(nearClip, farClip, splitEnd);

            Transform cameraTransform = camera.transform;
            Vector3 center = cameraTransform.position +
                             cameraTransform.forward * ((sliceNear + sliceFar) * 0.5f);
            float halfDepth = (sliceFar - sliceNear) * 0.5f;
            float radius;
            if (camera.orthographic)
            {
                float halfHeight = camera.orthographicSize;
                float halfWidth = halfHeight * Mathf.Max(0.001f, camera.aspect);
                radius = Mathf.Sqrt(
                    halfWidth * halfWidth +
                    halfHeight * halfHeight +
                    halfDepth * halfDepth);
            }
            else
            {
                float tanHalfFov = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                float aspect = Mathf.Max(0.001f, camera.aspect);
                float farHalfHeight = sliceFar * tanHalfFov;
                float farHalfWidth = farHalfHeight * aspect;
                float nearHalfHeight = sliceNear * tanHalfFov;
                float nearHalfWidth = nearHalfHeight * aspect;
                float farRadiusSq =
                    halfDepth * halfDepth +
                    farHalfWidth * farHalfWidth +
                    farHalfHeight * farHalfHeight;
                float nearRadiusSq =
                    halfDepth * halfDepth +
                    nearHalfWidth * nearHalfWidth +
                    nearHalfHeight * nearHalfHeight;
                radius = Mathf.Sqrt(Mathf.Max(farRadiusSq, nearRadiusSq));
            }

            radius = Mathf.Max(0.01f, radius * 1.005f);
            Vector3 lightForward = shadowLight.localToWorldMatrix.GetColumn(2);
            if (lightForward.sqrMagnitude < 1e-8f)
                return false;
            lightForward.Normalize();
            Vector3 lightUp = Mathf.Abs(Vector3.Dot(lightForward, Vector3.up)) > 0.99f
                ? Vector3.right
                : Vector3.up;
            Quaternion lightRotation = Quaternion.LookRotation(lightForward, lightUp);

            float texelWorldSize = (2.0f * radius) / resolution;
            Quaternion inverseLightRotation = Quaternion.Inverse(lightRotation);
            Vector3 centerLight = inverseLightRotation * center;
            centerLight.x = Mathf.Round(centerLight.x / texelWorldSize) * texelWorldSize;
            centerLight.y = Mathf.Round(centerLight.y / texelWorldSize) * texelWorldSize;
            center = lightRotation * centerLight;
            radius += texelWorldSize * 2.0f;

            float shadowNearPlane = Mathf.Max(0.001f, shadowLight.light.shadowNearPlane);
            float eyeDistance = radius * 2.0f + shadowNearPlane;
            Vector3 lightPosition = center - lightForward * eyeDistance;
            Matrix4x4 lightLocalToWorld = Matrix4x4.TRS(lightPosition, lightRotation, Vector3.one);
            slice.viewMatrix =
                Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f)) * lightLocalToWorld.inverse;
            slice.projectionMatrix = Matrix4x4.Ortho(
                -radius,
                radius,
                -radius,
                radius,
                shadowNearPlane,
                radius * 4.0f + shadowNearPlane);
            slice.offsetX = (cascadeIndex % 2) * resolution;
            slice.offsetY = (cascadeIndex / 2) * resolution;
            slice.resolution = resolution;
            slice.splitData = new ShadowSplitData
            {
                cullingSphere = new Vector4(center.x, center.y, center.z, radius),
                shadowCascadeBlendCullingFactor = 1.0f
            };
            slice.shadowTransform = ShadowUtils.GetShadowTransform(
                slice.projectionMatrix,
                slice.viewMatrix);
            if (cascadeCount > 1)
            {
                ShadowUtils.ApplySliceTransform(
                    ref slice,
                    shadowData.mainLightRenderTargetWidth,
                    shadowData.mainLightRenderTargetHeight);
            }

            return true;
        }

        static float GetCascadeEnd(int cascadeIndex, int cascadeCount, Vector3 splits)
        {
            if (cascadeIndex >= cascadeCount - 1)
                return 1.0f;
            if (cascadeIndex <= 0)
                return Mathf.Clamp01(splits.x);
            if (cascadeIndex == 1)
                return Mathf.Clamp01(splits.y);
            return Mathf.Clamp01(splits.z);
        }

        private void UpdateTextureDescriptorIfNeeded()
        {
            if (   m_MainLightShadowDescriptor.width != m_RenderTargetWidth
                || m_MainLightShadowDescriptor.height != m_RenderTargetHeight
                || m_MainLightShadowDescriptor.depthBufferBits != k_ShadowmapBufferBits
                || m_MainLightShadowDescriptor.colorFormat != RenderTextureFormat.Shadowmap)
            {
                m_MainLightShadowDescriptor = new RenderTextureDescriptor(m_RenderTargetWidth, m_RenderTargetHeight, RenderTextureFormat.Shadowmap, k_ShadowmapBufferBits);
            }
        }

        bool SetupForEmptyRendering(bool stripShadowsOffVariants, bool shadowsEnabled, Light light, UniversalCameraData cameraData, UniversalShadowData shadowData)
        {
            if (!stripShadowsOffVariants)
                return false;

            m_CreateEmptyShadowmap = true;
#if URP_COMPATIBILITY_MODE
            useNativeRenderPass = false;
#endif

            m_SetKeywordForEmptyShadowmap = shadowsEnabled;

            // Even though there are not real-time shadows, the light might be using shadowmasks,
            // which is why we need to update the shadow parameters, for example so shadow strength can be used.

            if (light == null)
            {
                s_EmptyShadowParams = new Vector4(0, 0, 1, 0);
            }
            else
            {
                bool supportsSoftShadows = shadowData.supportsSoftShadows;
                float maxShadowDistanceSq = cameraData.maxShadowDistance;
                float mainLightShadowCascadeBorder = shadowData.mainLightShadowCascadeBorder;

                bool softShadows = light.shadows == LightShadows.Soft && supportsSoftShadows;
                float softShadowsProp = ShadowUtils.SoftShadowQualityToShaderProperty(light, softShadows);
                ShadowUtils.GetScaleAndBiasForLinearDistanceFade(maxShadowDistanceSq, mainLightShadowCascadeBorder, out float shadowFadeScale, out float shadowFadeBias);
                s_EmptyShadowParams =  new Vector4(light.shadowStrength, softShadowsProp, shadowFadeScale, shadowFadeBias);
            }

            return true;
        }

#if URP_COMPATIBILITY_MODE
        /// <inheritdoc />
        [Obsolete(DeprecationMessage.CompatibilityScriptingAPIObsoleteFrom2023_3)]
        public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
        {
            // Disable obsolete warning for internal usage
            #pragma warning disable CS0618

            if (m_CreateEmptyShadowmap)
            {
                // Required for scene view camera(URP renderer not initialized)
                if (ShadowUtils.ShadowRTReAllocateIfNeeded(ref m_EmptyMainLightShadowmapTexture, k_EmptyShadowMapDimensions, k_EmptyShadowMapDimensions, k_ShadowmapBufferBits, name: k_EmptyMainLightShadowMapTextureName))
                    m_EmptyShadowmapNeedsClear = true;

                if (!m_EmptyShadowmapNeedsClear)
                    return;

                ConfigureTarget(m_EmptyMainLightShadowmapTexture);
                m_EmptyShadowmapNeedsClear = false;
            }
            else
            {
                ShadowUtils.ShadowRTReAllocateIfNeeded(ref m_MainLightShadowmapTexture, m_RenderTargetWidth, m_RenderTargetHeight, k_ShadowmapBufferBits, name: k_MainLightShadowMapTextureName);
                ConfigureTarget(m_MainLightShadowmapTexture);
            }

            ConfigureClear(ClearFlag.All, Color.black);

            #pragma warning restore CS0618
        }

        /// <inheritdoc/>
        [Obsolete(DeprecationMessage.CompatibilityScriptingAPIObsoleteFrom2023_3)]
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            ContextContainer frameData = renderingData.frameData;
            UniversalRenderingData universalRenderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            UniversalShadowData shadowData = frameData.Get<UniversalShadowData>();

            RasterCommandBuffer rasterCommandBuffer = CommandBufferHelpers.GetRasterCommandBuffer(renderingData.commandBuffer);
            if (m_CreateEmptyShadowmap)
            {
                if (m_SetKeywordForEmptyShadowmap)
                    rasterCommandBuffer.EnableKeyword(ShaderGlobalKeywords.MainLightShadows);
                SetShadowParamsForEmptyShadowmap(rasterCommandBuffer);
                universalRenderingData.commandBuffer.SetGlobalTexture(MainLightShadowConstantBuffer._MainLightShadowmapID, m_EmptyMainLightShadowmapTexture.nameID);
                return;
            }

            InitPassData(ref m_PassData, universalRenderingData, cameraData, lightData, shadowData);
            InitRendererLists(ref m_PassData, context, default(RenderGraph), false);

            RenderMainLightCascadeShadowmap(rasterCommandBuffer, ref m_PassData, false);
            universalRenderingData.commandBuffer.SetGlobalTexture(MainLightShadowConstantBuffer._MainLightShadowmapID, m_MainLightShadowmapTexture.nameID);
        }
#endif

        void Clear()
        {
            for (int i = 0; i < m_MainLightShadowMatrices.Length; ++i)
                m_MainLightShadowMatrices[i] = Matrix4x4.identity;

            for (int i = 0; i < m_CascadeSplitDistances.Length; ++i)
                m_CascadeSplitDistances[i] = new Vector4(0.0f, 0.0f, 0.0f, 0.0f);

            for (int i = 0; i < m_CascadeSlices.Length; ++i)
                m_CascadeSlices[i].Clear();
        }

        internal static void SetShadowParamsForEmptyShadowmap(RasterCommandBuffer rasterCommandBuffer)
        {
            rasterCommandBuffer.SetGlobalVector(MainLightShadowConstantBuffer._ShadowmapSize, s_EmptyShadowmapSize);
            rasterCommandBuffer.SetGlobalVector(MainLightShadowConstantBuffer._ShadowParams, s_EmptyShadowParams);
        }

        void RenderMainLightCascadeShadowmap(RasterCommandBuffer cmd, ref PassData data, bool isRenderGraph)
        {
            var lightData = data.lightData;

            int shadowLightIndex = lightData.mainLightIndex;
            if (shadowLightIndex == -1)
                return;

            VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];

            using (new ProfilingScope(cmd, ProfilingSampler.Get(URPProfileId.MainLightShadow)))
            {
                // Need to start by setting the Camera position and worldToCamera Matrix as that is not set for passes executed before normal rendering
                ShadowUtils.SetCameraPosition(cmd, data.cameraData.worldSpaceCameraPos);

                // For non-RG, need set the worldToCamera Matrix as that is not set for passes executed before normal rendering,
                // otherwise shadows will behave incorrectly when Scene and Game windows are open at the same time (UUM-63267).
                if (!isRenderGraph)
                    ShadowUtils.SetWorldToCameraAndCameraToWorldMatrices(cmd, data.cameraData.GetViewMatrix());

                for (int cascadeIndex = 0; cascadeIndex < m_ShadowCasterCascadesCount; ++cascadeIndex)
                {
                    Vector4 shadowBias = ShadowUtils.GetShadowBias(ref shadowLight, shadowLightIndex, data.shadowData, m_CascadeSlices[cascadeIndex].projectionMatrix, m_CascadeSlices[cascadeIndex].resolution);
                    ShadowUtils.SetupShadowCasterConstantBuffer(cmd, ref shadowLight, shadowBias);
                    cmd.SetKeyword(ShaderGlobalKeywords.CastingPunctualLightShadow, false);
                    if (!data.externalOnly)
                    {
                        RendererList shadowRendererList = isRenderGraph
                            ? data.shadowRendererListsHandle[cascadeIndex]
                            : data.shadowRendererLists[cascadeIndex];
                        ShadowUtils.RenderShadowSlice(
                            cmd,
                            ref m_CascadeSlices[cascadeIndex],
                            ref shadowRendererList,
                            m_CascadeSlices[cascadeIndex].projectionMatrix,
                            m_CascadeSlices[cascadeIndex].viewMatrix);
                    }

                    ShadowSliceData slice = m_CascadeSlices[cascadeIndex];
                    ExternalShadowCasterRegistry.DrawMainLightShadowCasters(
                        new ExternalMainLightShadowDrawContext(
                            cmd,
                            data.cameraData.camera,
                            shadowLight,
                            cascadeIndex,
                            m_ShadowCasterCascadesCount,
                            slice.viewMatrix,
                            slice.projectionMatrix,
                            shadowBias,
                            new Rect(slice.offsetX, slice.offsetY, slice.resolution, slice.resolution),
                            m_RenderTargetWidth,
                            m_RenderTargetHeight));
                }

                data.shadowData.isKeywordSoftShadowsEnabled = shadowLight.light.shadows == LightShadows.Soft && data.shadowData.supportsSoftShadows;
                cmd.SetKeyword(ShaderGlobalKeywords.MainLightShadows, data.shadowData.mainLightShadowCascadesCount == 1);
                cmd.SetKeyword(ShaderGlobalKeywords.MainLightShadowCascades, data.shadowData.mainLightShadowCascadesCount > 1);
                ShadowUtils.SetSoftShadowQualityShaderKeywords(cmd, data.shadowData);

                SetupMainLightShadowReceiverConstants(cmd, ref shadowLight, data.shadowData);
            }
        }

        void SetupMainLightShadowReceiverConstants(RasterCommandBuffer cmd, ref VisibleLight shadowLight, UniversalShadowData shadowData)
        {
            Light light = shadowLight.light;
            bool softShadows = shadowLight.light.shadows == LightShadows.Soft && shadowData.supportsSoftShadows;

            int cascadeCount = m_ShadowCasterCascadesCount;
            for (int i = 0; i < cascadeCount; ++i)
                m_MainLightShadowMatrices[i] = m_CascadeSlices[i].shadowTransform;

            // We setup and additional a no-op WorldToShadow matrix in the last index
            // because the ComputeCascadeIndex function in Shadows.hlsl can return an index
            // out of bounds. (position not inside any cascade) and we want to avoid branching
            Matrix4x4 noOpShadowMatrix = Matrix4x4.zero;
            noOpShadowMatrix.m22 = (SystemInfo.usesReversedZBuffer) ? 1.0f : 0.0f;
            for (int i = cascadeCount; i <= k_MaxCascades; ++i)
                m_MainLightShadowMatrices[i] = noOpShadowMatrix;

            float invShadowAtlasWidth = 1.0f / m_RenderTargetWidth;
            float invShadowAtlasHeight = 1.0f / m_RenderTargetHeight;
            float invHalfShadowAtlasWidth = 0.5f * invShadowAtlasWidth;
            float invHalfShadowAtlasHeight = 0.5f * invShadowAtlasHeight;
            float softShadowsProp = ShadowUtils.SoftShadowQualityToShaderProperty(light, softShadows);

            ShadowUtils.GetScaleAndBiasForLinearDistanceFade(m_MaxShadowDistanceSq, m_CascadeBorder, out float shadowFadeScale, out float shadowFadeBias);

            cmd.SetGlobalMatrixArray(MainLightShadowConstantBuffer._WorldToShadow, m_MainLightShadowMatrices);
            cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowParams,
                new Vector4(light.shadowStrength, softShadowsProp, shadowFadeScale, shadowFadeBias));

            if (m_ShadowCasterCascadesCount > 1)
            {
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSpheres0,
                    m_CascadeSplitDistances[0]);
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSpheres1,
                    m_CascadeSplitDistances[1]);
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSpheres2,
                    m_CascadeSplitDistances[2]);
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSpheres3,
                    m_CascadeSplitDistances[3]);
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSphereRadii, new Vector4(
                    m_CascadeSplitDistances[0].w * m_CascadeSplitDistances[0].w,
                    m_CascadeSplitDistances[1].w * m_CascadeSplitDistances[1].w,
                    m_CascadeSplitDistances[2].w * m_CascadeSplitDistances[2].w,
                    m_CascadeSplitDistances[3].w * m_CascadeSplitDistances[3].w));
            }

            // Inside shader soft shadows are controlled through global keyword.
            // If any additional light has soft shadows it will force soft shadows on main light too.
            // As it is not trivial finding out which additional light has soft shadows, we will pass main light properties if soft shadows are supported.
            // This workaround will be removed once we will support soft shadows per light.
            if (shadowData.supportsSoftShadows)
            {
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowOffset0,
                    new Vector4(-invHalfShadowAtlasWidth, -invHalfShadowAtlasHeight,
                        invHalfShadowAtlasWidth, -invHalfShadowAtlasHeight));
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowOffset1,
                    new Vector4(-invHalfShadowAtlasWidth, invHalfShadowAtlasHeight,
                        invHalfShadowAtlasWidth, invHalfShadowAtlasHeight));

                cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowmapSize, new Vector4(invShadowAtlasWidth,
                    invShadowAtlasHeight,
                    m_RenderTargetWidth, m_RenderTargetHeight));
            }
        }

        private void InitPassData(
            ref PassData passData,
            UniversalRenderingData renderingData,
            UniversalCameraData cameraData,
            UniversalLightData lightData,
            UniversalShadowData shadowData)
        {
            passData.pass = this;
            passData.emptyShadowmap = m_CreateEmptyShadowmap;
            passData.setKeywordForEmptyShadowmap = m_SetKeywordForEmptyShadowmap;
            passData.externalOnly = m_HasExternalShadowCasters && !m_HasRendererShadowCasters;
            passData.renderingData = renderingData;
            passData.cameraData = cameraData;
            passData.lightData = lightData;
            passData.shadowData = shadowData;
        }

        private void InitRendererLists(ref PassData passData, ScriptableRenderContext context, RenderGraph renderGraph, bool useRenderGraph)
        {
            int shadowLightIndex = passData.lightData.mainLightIndex;
            if (!m_CreateEmptyShadowmap && !passData.externalOnly && shadowLightIndex != -1)
            {
                ShadowDrawingSettings settings = new (passData.renderingData.cullResults, shadowLightIndex) {
                    useRenderingLayerMaskTest = UniversalRenderPipeline.asset.useRenderingLayers
                };

                for (int cascadeIndex = 0; cascadeIndex < m_ShadowCasterCascadesCount; ++cascadeIndex)
                {
                    if (useRenderGraph)
                        passData.shadowRendererListsHandle[cascadeIndex] = renderGraph.CreateShadowRendererList(ref settings);
                    else
                        passData.shadowRendererLists[cascadeIndex] = context.CreateShadowRendererList(ref settings);
                }
            }
        }

        private void CullExternalMainLightShadowCasters(
            UnsafeCommandBuffer cmd,
            Camera camera,
            UniversalLightData lightData)
        {
            if (cmd == null || camera == null || lightData == null)
                return;

            int shadowLightIndex = lightData.mainLightIndex;
            if (shadowLightIndex < 0 || shadowLightIndex >= lightData.visibleLights.Length)
                return;

            VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];
            ExternalShadowCasterRegistry.CullMainLightShadowCastersBatch(
                new ExternalMainLightShadowCullBatchContext(
                    cmd,
                    camera,
                    shadowLight,
                    m_CascadeSlices,
                    m_ShadowCasterCascadesCount));
            for (int cascadeIndex = 0; cascadeIndex < m_ShadowCasterCascadesCount; ++cascadeIndex)
            {
                ShadowSliceData slice = m_CascadeSlices[cascadeIndex];
                ExternalShadowCasterRegistry.CullMainLightShadowCasters(
                    new ExternalMainLightShadowCullContext(
                        cmd,
                        camera,
                        shadowLight,
                        cascadeIndex,
                        m_ShadowCasterCascadesCount,
                        slice.viewMatrix,
                        slice.projectionMatrix,
                        slice.splitData.cullingSphere,
                        slice.resolution));
            }
        }

        internal TextureHandle Render(RenderGraph graph, ContextContainer frameData)
        {
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            UniversalShadowData shadowData = frameData.Get<UniversalShadowData>();

            TextureHandle shadowTexture;
            BufferHandle externalCullFence = default;

            if (!m_CreateEmptyShadowmap && m_HasExternalShadowCasters)
            {
                externalCullFence = graph.CreateBuffer(new BufferDesc
                {
                    name = "MainLightExternalShadowCullFence",
                    count = 1,
                    stride = sizeof(uint),
                    target = GraphicsBuffer.Target.Structured
                });

                using (var builder = graph.AddUnsafePass<ExternalCullPassData>(
                           "Cull External Main Light Shadows",
                           out var cullPassData,
                           m_ExternalShadowCullProfilingSampler))
                {
                    cullPassData.pass = this;
                    cullPassData.camera = cameraData.camera;
                    cullPassData.lightData = lightData;
                    cullPassData.orderingFence = externalCullFence;
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.UseBuffer(externalCullFence, AccessFlags.Write);
                    builder.SetRenderFunc((ExternalCullPassData data, UnsafeGraphContext context) =>
                    {
                        data.pass.CullExternalMainLightShadowCasters(
                            context.cmd,
                            data.camera,
                            data.lightData);
                    });
                }
            }

            using (var builder = graph.AddRasterRenderPass<PassData>(passName, out var passData, profilingSampler))
            {
                InitPassData(ref passData, renderingData, cameraData, lightData, shadowData);
                InitRendererLists(ref passData, default(ScriptableRenderContext), graph, true);

                if (!m_CreateEmptyShadowmap)
                {
                    if (!passData.externalOnly)
                    {
                        for (int cascadeIndex = 0; cascadeIndex < m_ShadowCasterCascadesCount; ++cascadeIndex)
                            builder.UseRendererList(passData.shadowRendererListsHandle[cascadeIndex]);
                    }

                    shadowTexture = UniversalRenderer.CreateRenderGraphTexture(graph, m_MainLightShadowDescriptor, k_MainLightShadowMapTextureName, true, ShadowUtils.m_ForceShadowPointSampling ? FilterMode.Point : FilterMode.Bilinear);
                    builder.SetRenderAttachmentDepth(shadowTexture, AccessFlags.Write);
                }
                else
                {
                    shadowTexture = graph.defaultResources.defaultShadowTexture;
                }

                builder.AllowGlobalStateModification(true);
                if (m_HasExternalShadowCasters)
                {
                    builder.AllowPassCulling(false);
                    if (externalCullFence.IsValid())
                        builder.UseBuffer(externalCullFence, AccessFlags.Read);
                }

                if (shadowTexture.IsValid())
                    builder.SetGlobalTextureAfterPass(shadowTexture, MainLightShadowConstantBuffer._MainLightShadowmapID);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    RasterCommandBuffer rasterCommandBuffer = context.cmd;
                    if (!data.emptyShadowmap)
                    {
                        data.pass.RenderMainLightCascadeShadowmap(rasterCommandBuffer, ref data, true);
                    }
                    else
                    {
                        if (data.setKeywordForEmptyShadowmap)
                            rasterCommandBuffer.EnableKeyword(ShaderGlobalKeywords.MainLightShadows);
                        SetShadowParamsForEmptyShadowmap(rasterCommandBuffer);
                    }
                });
            }

            return shadowTexture;
        }
    };
}
