#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Nanite.Editor
{
    [CustomEditor(typeof(NaniteRuntimeProxy)), CanEditMultipleObjects]
    sealed class NaniteRuntimeProxyEditor : UnityEditor.Editor
    {
        SerializedProperty renderingMode;
        SerializedProperty naniteMesh;
        SerializedProperty resolveMaterials;
        readonly Renderer[] outlineRenderers = new Renderer[1];

        void OnEnable()
        {
            renderingMode = serializedObject.FindProperty("renderingMode");
            naniteMesh = serializedObject.FindProperty("naniteMesh");
            resolveMaterials = serializedObject.FindProperty("resolveMaterials");
            EnsureSourceBindings();
            PrepareSelectionRenderer();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(naniteMesh, new GUIContent("Nanite Mesh"));
            EditorGUILayout.PropertyField(renderingMode, new GUIContent("Rendering Mode"));
            EditorGUILayout.PropertyField(resolveMaterials, new GUIContent("Materials"), true);

            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                foreach (UnityEngine.Object item in targets)
                {
                    if (item is not NaniteRuntimeProxy proxy)
                        continue;
                    NaniteAssetBaker.EnsureSourceBindings(proxy.naniteMesh);
                    proxy.SynchronizeSourceRendererBindings();
                    proxy.MarkRenderDataDirty();
                    proxy.PrepareEditorSelectionRenderer();
                    EditorUtility.SetDirty(proxy);
                }
                SceneView.RepaintAll();
            }
            else
            {
                serializedObject.ApplyModifiedProperties();
            }

            DrawMaterialCompatibility();
        }

        void OnSceneGUI()
        {
            var proxy = target as NaniteRuntimeProxy;
            if (proxy == null || !proxy.PrepareEditorSelectionRenderer())
                return;
            var renderer = proxy.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                EditorUtility.SetSelectedRenderState(renderer, EditorSelectedRenderState.Highlight);
                if (Event.current != null && Event.current.type == EventType.Repaint)
                {
                    outlineRenderers[0] = renderer;
                    Handles.DrawOutline(outlineRenderers, Handles.selectedColor, 0f);
                }
            }
        }

        void EnsureSourceBindings()
        {
            foreach (UnityEngine.Object item in targets)
            {
                if (item is not NaniteRuntimeProxy proxy)
                    continue;
                if (NaniteAssetBaker.EnsureSourceBindings(proxy.naniteMesh))
                    AssetDatabase.SaveAssetIfDirty(proxy.naniteMesh);
                proxy.SynchronizeSourceRendererBindings();
            }
        }

        void PrepareSelectionRenderer()
        {
            foreach (UnityEngine.Object item in targets)
            {
                if (item is NaniteRuntimeProxy proxy)
                    proxy.PrepareEditorSelectionRenderer();
            }
        }

        void DrawMaterialCompatibility()
        {
            if (targets.Length != 1 || target is not NaniteRuntimeProxy proxy)
                return;
            Material[] materials = ResolveMaterials(proxy);
            for (int index = 0; index < materials.Length; index++)
            {
                Material material = materials[index];
                if (NaniteSceneVisibilityBufferBackend.SupportsFormalResolveMaterial(material))
                    continue;
                string shaderName = material != null && material.shader != null
                    ? material.shader.name
                    : "<null>";
                EditorGUILayout.HelpBox(
                    $"Material {index} uses unsupported shader '{shaderName}'. Register an " +
                    "INaniteMaterialResolveFamily or use Raster mode.",
                    MessageType.Warning);
            }
        }

        public static void AuditEditorInteractionBatch()
        {
            NaniteMesh mesh = AssetDatabase.LoadAssetAtPath<NaniteMesh>("Assets/toyota_ft1_mesh.asset");
            if (mesh == null || mesh.sourceMesh == null)
                throw new InvalidOperationException("Proxy editor audit mesh is unavailable.");
            NaniteAssetBaker.EnsureSourceBindings(mesh);
            if (mesh.sourceMaterials == null || mesh.sourceMaterials.Length == 0)
                throw new InvalidOperationException("Baked FBX source-material binding is unavailable.");

            var host = new GameObject("Nanite proxy editor interaction audit");
            Material sourceMaterial = null;
            try
            {
                MeshFilter filter = host.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh.sourceMesh;
                MeshRenderer renderer = host.AddComponent<MeshRenderer>();
                renderer.enabled = false;
                var proxy = host.AddComponent<NaniteRuntimeProxy>();
                proxy.naniteMesh = mesh;
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null)
                    throw new InvalidOperationException("Proxy editor audit material shader is unavailable.");
                sourceMaterial = new Material(shader);
                proxy.resolveMaterials = new[] { sourceMaterial };
                if (!proxy.PrepareEditorSelectionRenderer() || !renderer.enabled ||
                    !renderer.forceRenderingOff || filter.sharedMesh != mesh.sourceMesh ||
                    renderer.sharedMaterial != sourceMaterial || proxy.rasterFallbackMesh != null)
                {
                    throw new InvalidOperationException(
                        "Edit-mode source Mesh/Material binding or non-rendering selection contract failed.");
                }

                if (!NaniteScenePicker.IsStationaryClick(Vector2.zero, new Vector2(1f, 1f)) ||
                    NaniteScenePicker.IsStationaryClick(Vector2.zero, new Vector2(8f, 0f)))
                    throw new InvalidOperationException("Click-versus-transform-drag guard failed.");

                Bounds bounds = mesh.sourceMesh.bounds;
                float distance = Mathf.Max(1f, bounds.extents.magnitude * 3f);
                Ray ray = new Ray(bounds.center + Vector3.forward * distance, Vector3.back);
                if (!NaniteScenePicker.TryIntersectMesh(
                        ray,
                        mesh.sourceMesh,
                        Matrix4x4.identity,
                        out float hitDistance) || hitDistance <= 0f)
                {
                    throw new InvalidOperationException("Nanite Scene picking ray missed the source mesh.");
                }

                proxy.enabled = false;
                if (renderer.enabled || renderer.forceRenderingOff)
                    throw new InvalidOperationException("Selection Renderer state was not restored on disable.");
                AuditLegacyRenderingModeMigration();
                Debug.Log(
                    "[Nanite][ProxyEditorAudit] concise Inspector, FBX Mesh/Material binding, visible " +
                    "outline submission, Transform-handle ownership, legacy rendering-mode migration, " +
                    "CPU fallback picking and state restoration passed.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                if (sourceMaterial != null)
                    UnityEngine.Object.DestroyImmediate(sourceMaterial);
            }
        }

        static void AuditLegacyRenderingModeMigration()
        {
            AssertLegacyMode(false, true, NaniteRenderingMode.Raster);
            AssertLegacyMode(true, false, NaniteRenderingMode.Nanite);
            AssertLegacyMode(false, false, NaniteRenderingMode.Auto);
            AssertLegacyMode(true, true, NaniteRenderingMode.Auto);
        }

        static void AssertLegacyMode(
            bool forceNanite,
            bool forceRaster,
            NaniteRenderingMode expected)
        {
            var host = new GameObject("Nanite legacy mode migration audit");
            try
            {
                var proxy = host.AddComponent<NaniteRuntimeProxy>();
                var serialized = new SerializedObject(proxy);
                serialized.FindProperty("legacyForceNaniteRendering").boolValue = forceNanite;
                serialized.FindProperty("legacyForceRasterRendering").boolValue = forceRaster;
                serialized.FindProperty("renderingModeSerializationVersion").intValue = 0;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                proxy.OnAfterDeserialize();
                if (proxy.renderingMode != expected)
                {
                    throw new InvalidOperationException(
                        $"Legacy rendering mode migration produced {proxy.renderingMode}, expected {expected}.");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        internal static Material[] ResolveMaterials(NaniteRuntimeProxy proxy)
        {
            if (proxy == null)
                return Array.Empty<Material>();
            if (proxy.resolveMaterials != null && proxy.resolveMaterials.Length > 0)
                return proxy.resolveMaterials;
            Renderer renderer = proxy.GetComponent<Renderer>();
            if (renderer == null)
                renderer = proxy.GetComponentInChildren<Renderer>();
            if (renderer != null && renderer.sharedMaterials != null && renderer.sharedMaterials.Length > 0)
                return renderer.sharedMaterials;
            return proxy.naniteMesh != null && proxy.naniteMesh.sourceMaterials != null
                ? proxy.naniteMesh.sourceMaterials
                : Array.Empty<Material>();
        }
    }

    [InitializeOnLoad]
    static class NaniteEditorSourceBindingRepair
    {
        static NaniteEditorSourceBindingRepair() => EditorApplication.delayCall += RepairLoadedProxies;

        static void RepairLoadedProxies()
        {
            NaniteRuntimeProxy[] proxies = Resources.FindObjectsOfTypeAll<NaniteRuntimeProxy>();
            var repairedAssets = new HashSet<int>();
            for (int index = 0; index < proxies.Length; index++)
            {
                NaniteRuntimeProxy proxy = proxies[index];
                if (proxy == null || EditorUtility.IsPersistent(proxy))
                    continue;
                NaniteMesh mesh = proxy.naniteMesh;
                if (mesh != null && repairedAssets.Add(mesh.GetInstanceID()) &&
                    NaniteAssetBaker.EnsureSourceBindings(mesh))
                    AssetDatabase.SaveAssetIfDirty(mesh);
                proxy.SynchronizeSourceRendererBindings();
            }
            SceneView.RepaintAll();
        }
    }

    [InitializeOnLoad]
    static class NaniteEditorMaterialChangeMonitor
    {
        static readonly Dictionary<int, int> dirtyCounts = new Dictionary<int, int>(128);
        static double nextPoll;

        static NaniteEditorMaterialChangeMonitor() => EditorApplication.update += Poll;

        internal static void InvalidateAll()
        {
            dirtyCounts.Clear();
            PublishChange();
        }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextPoll)
                return;
            nextPoll = EditorApplication.timeSinceStartup + 0.1;

            bool changed = false;
            var live = new HashSet<int>();
            IReadOnlyList<NaniteRuntimeProxy> proxies = NaniteRuntimeRegistry.ActiveProxies;
            for (int proxyIndex = 0; proxyIndex < proxies.Count; proxyIndex++)
            {
                Material[] materials = NaniteRuntimeProxyEditor.ResolveMaterials(proxies[proxyIndex]);
                for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    Material material = materials[materialIndex];
                    if (material == null)
                        continue;
                    int id = material.GetInstanceID();
                    int dirty = EditorUtility.GetDirtyCount(material);
                    live.Add(id);
                    if (dirtyCounts.TryGetValue(id, out int previous) && previous != dirty)
                        changed = true;
                    dirtyCounts[id] = dirty;
                }
            }

            if (dirtyCounts.Count > live.Count)
            {
                var stale = new List<int>();
                foreach (int id in dirtyCounts.Keys)
                {
                    if (!live.Contains(id))
                        stale.Add(id);
                }
                for (int index = 0; index < stale.Count; index++)
                    dirtyCounts.Remove(stale[index]);
            }

            if (changed)
                PublishChange();
        }

        static void PublishChange()
        {
            IReadOnlyList<NaniteRuntimeProxy> proxies = NaniteRuntimeRegistry.ActiveProxies;
            if (proxies.Count > 0 && proxies[0] != null)
                proxies[0].MarkMaterialsDirty();
            SceneView.RepaintAll();
        }
    }

    sealed class NaniteMaterialAssetPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (ContainsMaterialOrTexture(importedAssets) ||
                ContainsMaterialOrTexture(movedAssets) ||
                ContainsMaterialOrTexture(deletedAssets))
            {
                NaniteEditorMaterialChangeMonitor.InvalidateAll();
            }
        }

        static bool ContainsMaterialOrTexture(string[] paths)
        {
            if (paths == null)
                return false;
            for (int index = 0; index < paths.Length; index++)
            {
                string extension = System.IO.Path.GetExtension(paths[index]).ToLowerInvariant();
                if (extension == ".mat" || extension == ".png" || extension == ".jpg" ||
                    extension == ".jpeg" || extension == ".tga" || extension == ".dds" ||
                    extension == ".exr" || extension == ".hdr" || extension == ".psd")
                    return true;
            }
            return false;
        }
    }

    [InitializeOnLoad]
    static class NaniteScenePicker
    {
        sealed class MeshPickData
        {
            internal int vertexCount;
            internal int indexCount;
            internal Vector3[] vertices;
            internal int[] triangles;
        }

        readonly struct Hit
        {
            internal readonly NaniteRuntimeProxy proxy;
            internal readonly float distance;
            internal Hit(NaniteRuntimeProxy proxy, float distance)
            {
                this.proxy = proxy;
                this.distance = distance;
            }
        }

        static Vector2 lastPosition = new Vector2(float.MinValue, float.MinValue);
        static Vector2 mouseDownPosition;
        static bool mouseDownPending;
        static double lastClickTime;
        static int cycleIndex;
        static readonly Dictionary<int, MeshPickData> meshCache = new Dictionary<int, MeshPickData>();

        static NaniteScenePicker() => SceneView.duringSceneGui += DuringSceneGui;

        static void DuringSceneGui(SceneView sceneView)
        {
            Event current = Event.current;
            if (current == null)
                return;

            if (current.type == EventType.MouseDown)
            {
                mouseDownPending = current.button == 0 &&
                                   !current.alt && !current.control && !current.command;
                mouseDownPosition = current.mousePosition;
                // Do not consume MouseDown. Unity's translate/rotate/scale tools must
                // be allowed to claim hotControl and process the complete drag.
                return;
            }

            if (current.type != EventType.MouseUp || current.button != 0 || !mouseDownPending)
                return;
            mouseDownPending = false;
            if (current.alt || current.control || current.command ||
                !IsStationaryClick(mouseDownPosition, current.mousePosition))
                return;

            GameObject nativePick = HandleUtility.PickGameObject(current.mousePosition, false);
            if (nativePick != null)
                return;

            Ray ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
            var hits = new List<Hit>();
            IReadOnlyList<NaniteRuntimeProxy> proxies = NaniteRuntimeRegistry.ActiveProxies;
            for (int index = 0; index < proxies.Count; index++)
            {
                NaniteRuntimeProxy proxy = proxies[index];
                if (proxy == null || !proxy.isActiveAndEnabled || proxy.naniteMesh == null)
                    continue;
                Mesh mesh = proxy.naniteMesh.sourceMesh;
                if (!TryIntersectMesh(
                        ray,
                        mesh,
                        proxy.transform.localToWorldMatrix,
                        out float distance))
                    continue;
                hits.Add(new Hit(proxy, distance));
            }

            if (hits.Count == 0)
                return;
            hits.Sort((left, right) => left.distance.CompareTo(right.distance));
            bool repeat = (current.mousePosition - lastPosition).sqrMagnitude <= 16f &&
                          EditorApplication.timeSinceStartup - lastClickTime <= 1.0;
            cycleIndex = repeat ? (cycleIndex + 1) % hits.Count : 0;
            lastPosition = current.mousePosition;
            lastClickTime = EditorApplication.timeSinceStartup;
            Selection.activeGameObject = hits[cycleIndex].proxy.gameObject;
            current.Use();
            sceneView.Repaint();
        }

        internal static bool IsStationaryClick(Vector2 down, Vector2 up) =>
            (up - down).sqrMagnitude <= 16f;

        internal static bool TryIntersectMesh(
            Ray worldRay,
            Mesh mesh,
            Matrix4x4 localToWorld,
            out float worldDistance)
        {
            worldDistance = float.MaxValue;
            if (mesh == null || !mesh.isReadable)
                return false;

            Matrix4x4 worldToLocal = localToWorld.inverse;
            Vector3 localOrigin = worldToLocal.MultiplyPoint3x4(worldRay.origin);
            Vector3 localDirection = worldToLocal.MultiplyVector(worldRay.direction);
            float directionLength = localDirection.magnitude;
            if (directionLength <= 1e-8f)
                return false;
            localDirection /= directionLength;
            var localRay = new Ray(localOrigin, localDirection);
            if (!mesh.bounds.IntersectRay(localRay))
                return false;

            int meshId = mesh.GetInstanceID();
            int indexCount = 0;
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                indexCount += (int)mesh.GetIndexCount(subMesh);
            if (!meshCache.TryGetValue(meshId, out MeshPickData data) ||
                data.vertexCount != mesh.vertexCount || data.indexCount != indexCount)
            {
                data = new MeshPickData
                {
                    vertexCount = mesh.vertexCount,
                    indexCount = indexCount,
                    vertices = mesh.vertices,
                    triangles = mesh.triangles
                };
                meshCache[meshId] = data;
            }

            float nearest = float.MaxValue;
            for (int index = 0; index + 2 < data.triangles.Length; index += 3)
            {
                Vector3 a = data.vertices[data.triangles[index + 0]];
                Vector3 b = data.vertices[data.triangles[index + 1]];
                Vector3 c = data.vertices[data.triangles[index + 2]];
                if (IntersectTriangle(localRay, a, b, c, out float distance) && distance < nearest)
                    nearest = distance;
            }
            if (!float.IsFinite(nearest) || nearest == float.MaxValue)
                return false;

            Vector3 worldHit = localToWorld.MultiplyPoint3x4(localRay.GetPoint(nearest));
            worldDistance = Vector3.Distance(worldRay.origin, worldHit);
            return true;
        }

        static bool IntersectTriangle(
            Ray ray,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            out float distance)
        {
            Vector3 edge1 = b - a;
            Vector3 edge2 = c - a;
            Vector3 p = Vector3.Cross(ray.direction, edge2);
            float determinant = Vector3.Dot(edge1, p);
            if (Mathf.Abs(determinant) <= 1e-8f)
            {
                distance = 0f;
                return false;
            }

            float inverse = 1f / determinant;
            Vector3 t = ray.origin - a;
            float u = Vector3.Dot(t, p) * inverse;
            if (u < 0f || u > 1f)
            {
                distance = 0f;
                return false;
            }
            Vector3 q = Vector3.Cross(t, edge1);
            float v = Vector3.Dot(ray.direction, q) * inverse;
            if (v < 0f || u + v > 1f)
            {
                distance = 0f;
                return false;
            }
            distance = Vector3.Dot(edge2, q) * inverse;
            return distance >= 0f;
        }
    }
}
#endif
