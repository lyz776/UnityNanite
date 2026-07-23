using UnityEngine;

namespace Nanite
{
    [DefaultExecutionOrder(-10000)]
    public class NaniteUrpCoreTimingSwitch : MonoBehaviour
    {
        [Header("A/B Fallback")]
        public bool useUrpCoreTiming = false;
        public bool logCoreTimingStats = false;

        void OnEnable()
        {
            Apply();
        }

        void OnDisable()
        {
            NaniteUrpCoreTimingDriver.Configure(false, false);
        }

        void OnValidate()
        {
            Apply();
        }

        void Apply()
        {
            NaniteUrpCoreTimingDriver.Configure(useUrpCoreTiming && isActiveAndEnabled, logCoreTimingStats);
        }
    }
}
