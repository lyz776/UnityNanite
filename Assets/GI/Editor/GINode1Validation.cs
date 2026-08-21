#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityNanite.GI.Editor
{
    [InitializeOnLoad]
    public static class GINode1Validation
    {
        const string CaptureDirectory = "tmp/gi-node1-validation-20260818";
        static int stableFrames;
        static bool automaticCaptureRequested;

        static GINode1Validation()
        {
            EditorApplication.update -= UpdateAutomaticCapture;
            EditorApplication.update += UpdateAutomaticCapture;
        }

        static void UpdateAutomaticCapture()
        {
            if (!EditorApplication.isPlaying)
            {
                stableFrames = 0;
                automaticCaptureRequested = false;
                return;
            }
            if (automaticCaptureRequested || ++stableFrames < 120)
                return;
            automaticCaptureRequested = true;
            CaptureDebug();
        }

        [MenuItem("GI/Diagnostics/Capture Node 1 Debug %#g")]
        public static void CaptureDebug()
        {
            if (!EditorApplication.isPlaying)
            {
                Debug.LogError("[GI][Node1][Validation] Enter Play Mode before capturing Debug output.");
                return;
            }

            Directory.CreateDirectory(CaptureDirectory);
            string path = Path.GetFullPath(
                $"{CaptureDirectory}/gi-node1-{System.DateTime.Now:HHmmss}.png");
            ScreenCapture.CaptureScreenshot(path, 1);
            Debug.Log($"[GI][Node1][Validation] Requested Game output capture: {path}.");
        }
    }
}
#endif
