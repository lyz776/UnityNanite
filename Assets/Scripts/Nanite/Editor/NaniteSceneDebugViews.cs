#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nanite.Editor
{
    [InitializeOnLoad]
    static class NaniteSceneDebugViews
    {
        const string Section = "Nanite";
        const string ClusterModeName = "Clusters";
        const string TriangleModeName = "Triangles";
        const string PageModeName = "Pages";
        static Material wireMaterial;
        static NaniteRuntimeProxy[] selectedWireframeProxies = Array.Empty<NaniteRuntimeProxy>();

        static NaniteSceneDebugViews()
        {
            SceneView.AddCameraMode(ClusterModeName, Section);
            SceneView.AddCameraMode(TriangleModeName, Section);
            SceneView.AddCameraMode(PageModeName, Section);
            SceneView.duringSceneGui += OnSceneGui;
            Selection.selectionChanged += CacheSelectedWireframeProxies;
            EditorApplication.delayCall += SyncWithActiveSceneView;
            EditorApplication.delayCall += CacheSelectedWireframeProxies;
        }

        static void SyncWithActiveSceneView()
        {
            SceneView activeView = SceneView.lastActiveSceneView;
            NaniteDebugVisualization.SetActiveMode(
                activeView != null
                    ? ToNaniteMode(activeView.cameraMode)
                    : NaniteDebugVisualizationMode.Disabled);
        }

        static NaniteDebugVisualizationMode ToNaniteMode(SceneView.CameraMode cameraMode)
        {
            if (cameraMode.section != Section)
                return NaniteDebugVisualizationMode.Disabled;

            switch (cameraMode.name)
            {
                case ClusterModeName:
                    return NaniteDebugVisualizationMode.Cluster;
                case TriangleModeName:
                    return NaniteDebugVisualizationMode.Triangle;
                case PageModeName:
                    return NaniteDebugVisualizationMode.Page;
                default:
                    return NaniteDebugVisualizationMode.Disabled;
            }
        }

        static void OnSceneGui(SceneView sceneView)
        {
            if (sceneView == SceneView.lastActiveSceneView)
                NaniteDebugVisualization.SetActiveMode(ToNaniteMode(sceneView.cameraMode));

            if (Event.current == null || Event.current.type != EventType.Repaint ||
                !IsWireframeMode(sceneView.cameraMode))
                return;

            DrawNaniteWireframes();
        }

        static bool IsWireframeMode(SceneView.CameraMode cameraMode)
        {
            return cameraMode.drawMode == DrawCameraMode.Wireframe ||
                   cameraMode.drawMode == DrawCameraMode.TexturedWire;
        }

        static void DrawNaniteWireframes()
        {
            Material material = GetWireMaterial();
            if (material == null)
                return;

            Color previousColor = Handles.color;
            CompareFunction previousDepthTest = Handles.zTest;
            Handles.color = new Color(0.08f, 0.9f, 1f, 0.95f);
            Handles.zTest = CompareFunction.LessEqual;
            material.SetColor("_Color", Handles.color);

            try
            {
                for (int proxyIndex = 0; proxyIndex < selectedWireframeProxies.Length; proxyIndex++)
                {
                    NaniteRuntimeProxy proxy = selectedWireframeProxies[proxyIndex];
                    if (proxy == null || !proxy.isActiveAndEnabled ||
                        !proxy.NaniteRenderingActive || proxy.naniteMesh == null)
                        continue;

                    Mesh sourceMesh = proxy.naniteMesh.sourceMesh;
                    if (sourceMesh == null)
                        continue;

                    Transform transform = proxy.transform;
                    for (int subMesh = 0; subMesh < sourceMesh.subMeshCount; subMesh++)
                    {
                        if (material.SetPass(0))
                        {
                            GL.wireframe = true;
                            Graphics.DrawMeshNow(sourceMesh, transform.localToWorldMatrix, subMesh);
                            GL.wireframe = false;
                        }
                    }
                }
            }
            finally
            {
                GL.wireframe = false;
                Handles.color = previousColor;
                Handles.zTest = previousDepthTest;
            }
        }

        static void CacheSelectedWireframeProxies()
        {
            selectedWireframeProxies = Selection.GetFiltered<NaniteRuntimeProxy>(SelectionMode.Editable);
            SceneView.RepaintAll();
        }

        static Material GetWireMaterial()
        {
            if (wireMaterial != null)
                return wireMaterial;

            Shader shader = Shader.Find("Hidden/Nanite/SceneWireframe");
            if (shader == null)
                return null;

            wireMaterial = new Material(shader)
            {
                name = "Nanite Scene Wireframe",
                hideFlags = HideFlags.HideAndDontSave
            };
            return wireMaterial;
        }
    }
}
#endif
