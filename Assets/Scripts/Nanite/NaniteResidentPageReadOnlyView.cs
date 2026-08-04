using UnityEngine;

namespace Nanite
{
    /// <summary>
    /// Frame-scoped immutable view of the currently published Nanite resident-page front table.
    /// Consumers must import these buffers into the same RenderGraph frame and accept only pages
    /// whose PageTable/ResidentTable generations match and whose geometry-ready flag is set.
    /// No ownership of the buffers is transferred to the consumer.
    /// </summary>
    public readonly struct NaniteResidentPageReadOnlyView
    {
        public readonly GraphicsBuffer residentVertices;
        public readonly GraphicsBuffer residentIndices;
        public readonly GraphicsBuffer residentTriangleSubMeshes;
        public readonly GraphicsBuffer pageTable;
        public readonly GraphicsBuffer residentPageTable;
        public readonly GraphicsBuffer residencyBits;
        public readonly int pageCount;
        public readonly int residentPageCount;
        public readonly int residentVertexCapacity;
        public readonly int residentIndexCapacity;
        public readonly int residencyWordCount;
        public readonly int vertexStride;
        public readonly int indexStride;
        public readonly int pageTableStride;
        public readonly int residentPageTableStride;
        public readonly uint poolGeneration;
        public readonly ulong tableEpoch;
        public readonly int publishedFrame;
        public readonly uint geometryReadyFlag;

        internal NaniteResidentPageReadOnlyView(
            GraphicsBuffer residentVertices,
            GraphicsBuffer residentIndices,
            GraphicsBuffer residentTriangleSubMeshes,
            GraphicsBuffer pageTable,
            GraphicsBuffer residentPageTable,
            GraphicsBuffer residencyBits,
            int pageCount,
            int residentPageCount,
            uint poolGeneration,
            ulong tableEpoch,
            int publishedFrame)
        {
            this.residentVertices = residentVertices;
            this.residentIndices = residentIndices;
            this.residentTriangleSubMeshes = residentTriangleSubMeshes;
            this.pageTable = pageTable;
            this.residentPageTable = residentPageTable;
            this.residencyBits = residencyBits;
            this.pageCount = pageCount;
            this.residentPageCount = residentPageCount;
            residentVertexCapacity = residentVertices != null ? residentVertices.count : 0;
            residentIndexCapacity = residentIndices != null ? residentIndices.count : 0;
            residencyWordCount = residencyBits != null ? residencyBits.count : 0;
            vertexStride = NaniteGpuPagePool.ResidentVertexBytes;
            indexStride = sizeof(uint);
            pageTableStride = NaniteGpuPagePool.PageTableEntryBytes;
            residentPageTableStride = NaniteGpuPagePool.ResidentPageEntryBytes;
            this.poolGeneration = poolGeneration;
            this.tableEpoch = tableEpoch;
            this.publishedFrame = publishedFrame;
            geometryReadyFlag = NaniteGpuPagePool.FlagResidentGeometryReady;
        }

        public bool IsValid =>
            residentVertices != null && residentIndices != null &&
            residentTriangleSubMeshes != null &&
            pageTable != null && residentPageTable != null && residencyBits != null &&
            pageCount > 0 && vertexStride == 48 && indexStride == 4;
    }
}
