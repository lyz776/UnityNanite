using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityNanite.GI
{
    public sealed class GIScreenSurfaceData : ContextItem
    {
        public TextureHandle radiance = TextureHandle.nullHandle;

        public override void Reset() => radiance = TextureHandle.nullHandle;
    }

    public sealed class GIScreenProbeData : ContextItem
    {
        public TextureHandle receiverPosition = TextureHandle.nullHandle;
        public TextureHandle geometryNormal = TextureHandle.nullHandle;
        public TextureHandle receiverNormal = TextureHandle.nullHandle;
        public TextureHandle receiverPixel = TextureHandle.nullHandle;
        public TextureHandle primaryIrradiance = TextureHandle.nullHandle;
        public TextureHandle resolvedSpecular = TextureHandle.nullHandle;
        public TextureHandle resolvedIrradiance = TextureHandle.nullHandle;
        public TextureHandle debug = TextureHandle.nullHandle;
        public int width;
        public int height;

        public override void Reset()
        {
            receiverPosition = TextureHandle.nullHandle;
            geometryNormal = TextureHandle.nullHandle;
            receiverNormal = TextureHandle.nullHandle;
            receiverPixel = TextureHandle.nullHandle;
            primaryIrradiance = TextureHandle.nullHandle;
            resolvedSpecular = TextureHandle.nullHandle;
            resolvedIrradiance = TextureHandle.nullHandle;
            debug = TextureHandle.nullHandle;
            width = 0;
            height = 0;
        }
    }
}
