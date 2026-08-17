using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealtimeGI
{
    /// <summary>Persistent sparse world-Brick allocation for one static or dynamic layer.</summary>
    public sealed class GIClipmapLayer : IDisposable
    {
        readonly int capacity;

        GraphicsBuffer pageTableBuffer;
        GraphicsBuffer occupancyBuffer;
        GraphicsBuffer surfaceBuffer;
        GraphicsBuffer surfaceUvBuffer;
        GraphicsBuffer surfaceIdentityBuffer;
        GraphicsBuffer surfaceStableIdBuffer;
        GraphicsBuffer surfaceKeyBuffer;
        GraphicsBuffer distanceBuffer;
        GraphicsBuffer dirtyBrickBuffer;
        GraphicsBuffer brickDataBuffer;
        GraphicsBuffer radianceBuffer;
        GraphicsBuffer validityBuffer;
        GraphicsBuffer irradianceBuffer;
        GraphicsBuffer irradianceValidityBuffer;
        GraphicsBuffer radianceDirtyBuffer;
        GraphicsBuffer lightCountBuffer;
        GraphicsBuffer lightIndexBuffer;
        GraphicsBuffer dirtyGenerationBuffer;
        GraphicsBuffer voxelWorkQueueBuffer;
        GraphicsBuffer voxelDispatchArgsBuffer;
        GraphicsBuffer requiredPageMaskBuffer;
        GraphicsBuffer requiredPageHashBuffer;
        GraphicsBuffer physicalPageHashBuffer;
        GraphicsBuffer physicalRadianceGenerationBuffer;
        GraphicsBuffer freePageListBuffer;
        GraphicsBuffer allocatorStateBuffer;
        GraphicsBuffer pageDispatchArgsBuffer;
        int voxelWorkCapacity;
        int lightsPerBrick;

        public GraphicsBuffer PageTableBuffer => pageTableBuffer;
        public GraphicsBuffer OccupancyBuffer => occupancyBuffer;
        public GraphicsBuffer SurfaceBuffer => surfaceBuffer;
        public GraphicsBuffer SurfaceUvBuffer => surfaceUvBuffer;
        public GraphicsBuffer SurfaceIdentityBuffer => surfaceIdentityBuffer;
        public GraphicsBuffer SurfaceStableIdBuffer => surfaceStableIdBuffer;
        public GraphicsBuffer SurfaceKeyBuffer => surfaceKeyBuffer;
        public GraphicsBuffer DistanceBuffer => distanceBuffer;
        public GraphicsBuffer DirtyBrickBuffer => dirtyBrickBuffer;
        public GraphicsBuffer BrickDataBuffer => brickDataBuffer;
        public GraphicsBuffer RadianceBuffer => radianceBuffer;
        public GraphicsBuffer ValidityBuffer => validityBuffer;
        public GraphicsBuffer IrradianceBuffer => irradianceBuffer;
        public GraphicsBuffer IrradianceValidityBuffer => irradianceValidityBuffer;
        public GraphicsBuffer RadianceDirtyBuffer => radianceDirtyBuffer;
        public GraphicsBuffer LightCountBuffer => lightCountBuffer;
        public GraphicsBuffer LightIndexBuffer => lightIndexBuffer;
        public GraphicsBuffer DirtyGenerationBuffer => dirtyGenerationBuffer;
        public GraphicsBuffer VoxelWorkQueueBuffer => voxelWorkQueueBuffer;
        public GraphicsBuffer VoxelDispatchArgsBuffer => voxelDispatchArgsBuffer;
        public GraphicsBuffer RequiredPageMaskBuffer => requiredPageMaskBuffer;
        public GraphicsBuffer RequiredPageHashBuffer => requiredPageHashBuffer;
        public GraphicsBuffer PhysicalPageHashBuffer => physicalPageHashBuffer;
        public GraphicsBuffer PhysicalRadianceGenerationBuffer => physicalRadianceGenerationBuffer;
        public GraphicsBuffer FreePageListBuffer => freePageListBuffer;
        public GraphicsBuffer AllocatorStateBuffer => allocatorStateBuffer;
        public GraphicsBuffer PageDispatchArgsBuffer => pageDispatchArgsBuffer;
        public int VoxelWorkCapacity => voxelWorkCapacity;
        public int LightsPerBrick => lightsPerBrick;
        public int Capacity => capacity;
        public bool GpuAllocatorInitialized { get; private set; }

        public GIClipmapLayer(int capacity, string debugName)
        {
            this.capacity = Mathf.Max(1, capacity);
            pageTableBuffer = NewBuffer(GIClipmapConstants.PageTableEntries, 4, debugName + " Page Table");
            occupancyBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.OccupancyWordsPerBrick, 4, debugName + " Occupancy");
            surfaceBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.SurfaceWordsPerBrick, 4, debugName + " Surface");
            surfaceUvBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.SurfaceUvWordsPerBrick, 4, debugName + " Surface UV0");
            surfaceIdentityBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.SurfaceIdentityWordsPerBrick, 4, debugName + " Surface Identity");
            surfaceStableIdBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.SurfaceStableIdEntriesPerBrick, 8,
                debugName + " Stable Object Surface ID");
            surfaceKeyBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.SurfaceKeyWordsPerBrick, 4, debugName + " Surface Candidate Key");
            distanceBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.DistanceWordsPerBrick, 4, debugName + " Distance");
            radianceBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.RadianceWordsPerBrick, 4, debugName + " Directional Radiance RGB9E5");
            validityBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.ValidityWordsPerBrick, 4, debugName + " Radiance Validity");
            irradianceBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.IrradianceWordsPerBrick, 4,
                debugName + " Free Space Irradiance RGB9E5");
            irradianceValidityBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.IrradianceProbeValidityWordsPerBrick, 4,
                debugName + " Free Space Irradiance Validity");
            brickDataBuffer = NewBuffer(this.capacity, GIClipmapConstants.BrickDataStride, debugName + " Brick Data");
            // GPU allocation produces both queues.  Allocate their hard capacity once so
            // no CPU count/readback is needed to resize them in the frame loop.
            dirtyBrickBuffer = NewBuffer(this.capacity, 4, debugName + " Dirty Bricks");
            radianceDirtyBuffer = NewBuffer(this.capacity, 4, debugName + " Radiance Dirty Bricks");
            dirtyGenerationBuffer = NewBuffer(this.capacity, 4, debugName + " Dirty Generation");
            requiredPageMaskBuffer = NewBuffer(
                GIClipmapConstants.PageTableEntries, 4, debugName + " Required Page Mask");
            requiredPageHashBuffer = NewBuffer(
                GIClipmapConstants.PageTableEntries, 4, debugName + " Required Page Hash");
            physicalPageHashBuffer = NewBuffer(
                this.capacity, 4, debugName + " Physical Page Hash");
            physicalRadianceGenerationBuffer = NewBuffer(
                this.capacity, 4, debugName + " Physical Radiance Generation");
            freePageListBuffer = NewBuffer(this.capacity, 4, debugName + " Free Page List");
            allocatorStateBuffer = NewBuffer(
                GIClipmapConstants.AllocatorStateWords, 4, debugName + " Allocator State");
            pageDispatchArgsBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                GIClipmapConstants.PageDispatchArgumentWords, 4)
            {
                name = debugName + " Page Dispatch Args"
            };
            // ResetPageAllocatorFrame initializes page ownership, free-list, hashes and
            // indirect arguments before any consumer dispatch. No CPU allocation payload is
            // uploaded here.
        }

        public void MarkGpuAllocatorRecorded() => GpuAllocatorInitialized = true;

        /// <summary>
        /// Bounded GPU queue. One uint4 entry represents one cooperative 1024-triangle
        /// instance/Page batch. Indirect xyz occupy the first three argument words, the
        /// fourth is the overflow counter and the fifth stores the accepted linear count.
        /// </summary>
        public void EnsureVoxelWorkQueue(int requestedCapacity, string debugName)
        {
            int next = Mathf.Clamp(requestedCapacity, 1024, 1048576);
            if (voxelWorkQueueBuffer != null && voxelDispatchArgsBuffer != null &&
                voxelWorkCapacity == next)
                return;
            voxelWorkQueueBuffer?.Release();
            voxelDispatchArgsBuffer?.Release();
            voxelWorkCapacity = next;
            voxelWorkQueueBuffer = NewBuffer(next, 16, debugName + " Voxel Work Queue");
            voxelDispatchArgsBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 5, 4)
            {
                name = debugName + " Voxel Dispatch Args"
            };
            voxelDispatchArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u, 0u });
        }

        static GraphicsBuffer NewBuffer(int count, int stride, string name) =>
            new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, count), stride) { name = name };

        public void EnsureLightLists(int requestedLightsPerBrick, string debugName)
        {
            int next = Mathf.Clamp(requestedLightsPerBrick, 8, 64);
            if (lightCountBuffer != null && lightIndexBuffer != null && lightsPerBrick == next)
                return;
            lightCountBuffer?.Release();
            lightIndexBuffer?.Release();
            lightsPerBrick = next;
            lightCountBuffer = NewBuffer(capacity, 4, debugName + " Light Counts");
            lightIndexBuffer = NewBuffer(
                capacity * lightsPerBrick, 4, debugName + " Light Indices");
            lightCountBuffer.SetData(new uint[capacity]);
        }

        public void Dispose()
        {
            pageTableBuffer?.Release();
            occupancyBuffer?.Release();
            surfaceBuffer?.Release();
            surfaceUvBuffer?.Release();
            surfaceIdentityBuffer?.Release();
            surfaceStableIdBuffer?.Release();
            surfaceKeyBuffer?.Release();
            distanceBuffer?.Release();
            dirtyBrickBuffer?.Release();
            brickDataBuffer?.Release();
            radianceBuffer?.Release();
            validityBuffer?.Release();
            irradianceBuffer?.Release();
            irradianceValidityBuffer?.Release();
            radianceDirtyBuffer?.Release();
            lightCountBuffer?.Release();
            lightIndexBuffer?.Release();
            dirtyGenerationBuffer?.Release();
            voxelWorkQueueBuffer?.Release();
            voxelDispatchArgsBuffer?.Release();
            requiredPageMaskBuffer?.Release();
            requiredPageHashBuffer?.Release();
            physicalPageHashBuffer?.Release();
            physicalRadianceGenerationBuffer?.Release();
            freePageListBuffer?.Release();
            allocatorStateBuffer?.Release();
            pageDispatchArgsBuffer?.Release();
            pageTableBuffer = null;
            occupancyBuffer = null;
            surfaceBuffer = null;
            surfaceUvBuffer = null;
            surfaceIdentityBuffer = null;
            surfaceStableIdBuffer = null;
            surfaceKeyBuffer = null;
            distanceBuffer = null;
            dirtyBrickBuffer = null;
            brickDataBuffer = null;
            radianceBuffer = null;
            validityBuffer = null;
            irradianceBuffer = null;
            irradianceValidityBuffer = null;
            radianceDirtyBuffer = null;
            lightCountBuffer = null;
            lightIndexBuffer = null;
            dirtyGenerationBuffer = null;
            voxelWorkQueueBuffer = null;
            voxelDispatchArgsBuffer = null;
            requiredPageMaskBuffer = null;
            requiredPageHashBuffer = null;
            physicalPageHashBuffer = null;
            physicalRadianceGenerationBuffer = null;
            freePageListBuffer = null;
            allocatorStateBuffer = null;
            pageDispatchArgsBuffer = null;
            GpuAllocatorInitialized = false;
            voxelWorkCapacity = 0;
            lightsPerBrick = 0;
        }
    }
}
