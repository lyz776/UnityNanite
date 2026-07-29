using UnityEditor;
using UnityEngine;

namespace UnityNanite.TOD.Editor
{
    internal static class TODGizmos
    {
        [DrawGizmo(GizmoType.Selected | GizmoType.Active)]
        private static void DrawOrbit(TODController controller, GizmoType gizmoType)
        {
            if (controller == null || controller.Profile == null)
                return;

            const int segments = 96;
            float radius = Mathf.Max(1f, controller.Profile.gizmoRadius);
            Handles.color = new Color(1f, 0.82f, 0.05f, 0.95f);

            Vector3 previous = controller.transform.position + controller.GetSunDirection(0f) * radius;
            for (int i = 1; i <= segments; i++)
            {
                float hour = 24f * i / segments;
                Vector3 next = controller.transform.position + controller.GetSunDirection(hour) * radius;
                if ((i & 1) == 0)
                    Handles.DrawAAPolyLine(2f, previous, next);
                previous = next;
            }

            Vector3 current = controller.transform.position +
                              controller.GetSunDirection(controller.CurrentTime) * radius;
            Handles.SphereHandleCap(0, current, Quaternion.identity, radius * 0.025f, EventType.Repaint);
            Handles.Label(current, $" Sun {controller.CurrentTime:00.00}h");
        }
    }
}
