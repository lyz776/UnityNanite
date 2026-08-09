namespace Nanite
{
    /// <summary>
    /// Global Nanite diagnostic state. Editor tooling owns the UI, while the
    /// renderer reads this state for both Scene and Game cameras.
    /// </summary>
    public enum NaniteDebugVisualizationMode
    {
        Disabled = -1,
        Cluster = 0,
        Triangle = 1,
        Page = 2
    }

    public static class NaniteDebugVisualization
    {
        static NaniteDebugVisualizationMode activeMode = NaniteDebugVisualizationMode.Disabled;

        public static NaniteDebugVisualizationMode ActiveMode => activeMode;
        public static bool IsEnabled => activeMode != NaniteDebugVisualizationMode.Disabled;

        public static void SetActiveMode(NaniteDebugVisualizationMode mode)
        {
            if (activeMode == mode)
                return;

            activeMode = mode;
        }
    }
}
