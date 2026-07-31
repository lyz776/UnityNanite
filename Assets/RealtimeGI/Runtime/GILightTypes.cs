using System.Runtime.InteropServices;
using UnityEngine;

namespace RealtimeGI
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GIGpuLocalLightData
    {
        public Vector4 positionRange;
        public Vector4 colorIntensity;
        public Vector4 directionOuterCos;
        // x=type (0 point, 1 spot), y=inner cos, z=casts GI shadow, w=indirect multiplier
        public Vector4 parameters;
    }

    public static class GILightAbi
    {
        public const int LocalLightStride = 64;
    }
}
