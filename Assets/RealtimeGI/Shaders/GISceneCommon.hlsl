#ifndef REALTIME_GI_SCENE_COMMON_INCLUDED
#define REALTIME_GI_SCENE_COMMON_INCLUDED

#define GI_SCENE_ABI_VERSION 5u

#define GI_INSTANCE_OCCLUDER      (1u << 0)
#define GI_INSTANCE_CONTRIBUTOR   (1u << 1)
#define GI_INSTANCE_RECEIVER      (1u << 2)
#define GI_INSTANCE_DYNAMIC       (1u << 3)
#define GI_INSTANCE_TWO_SIDED     (1u << 4)
#define GI_INSTANCE_ALPHA_TESTED  (1u << 5)
#define GI_INSTANCE_EMISSIVE      (1u << 6)

#define GI_GEOMETRY_UNITY_MESH       0u
#define GI_GEOMETRY_NANITE_MESH      1u
#define GI_GEOMETRY_ANALYTIC_SPHERE  2u
#define GI_GEOMETRY_ANALYTIC_BOX     3u
#define GI_GEOMETRY_ANALYTIC_CAPSULE 4u

#define GI_GEOMETRY_STREAM_NANITE_PROXY          (1u << 0)
#define GI_GEOMETRY_STREAM_NANITE_RESIDENT_PAGES (1u << 1)

// 176 bytes. Matrix layout is the same StructuredBuffer layout uploaded by Unity Matrix4x4.
struct GIInstanceData
{
    float4x4 localToWorld;
    float4x4 previousLocalToWorld;
    float4 worldBoundingSphere;
    uint objectId;
    uint geometryIndex;
    uint firstMaterialBinding;
    uint materialBindingCount;
    uint flags;
    uint adapterType;
    uint revision;
    uint transformSignature;
};

// 48 bytes.
struct GIGeometryData
{
    uint geometryId;
    uint kind;
    uint sourceObjectId;
    uint flags;
    float4 localBoundingSphere;
    uint vertexCount;
    uint indexCount;
    uint subMeshCount;
    uint sourceRevision;
};

#define GI_MATERIAL_DOUBLE_SIDED       (1u << 0)
#define GI_MATERIAL_EMISSIVE           (1u << 1)
#define GI_MATERIAL_ALPHA_TESTED       (1u << 2)
#define GI_MATERIAL_HAS_BASE_MAP       (1u << 3)
#define GI_MATERIAL_HAS_EMISSION_MAP   (1u << 4)
#define GI_MATERIAL_HAS_NORMAL_MAP     (1u << 5)
#define GI_MATERIAL_HAS_MASK_MAP       (1u << 6)
#define GI_MATERIAL_EXPLICIT_PROXY     (1u << 7)

#define GI_MASK_CHANNEL_CONSTANT 4u

// 176 bytes.
struct GIMaterialData
{
    float4 baseColor;
    float4 emissive;
    float4 surface; // roughness, metallic, opacity, alpha cutoff
    float4 baseMapST;
    float4 emissionMapST;
    float4 normalMapST;
    float4 maskMapST;
    float4 maskRemap0;
    float4 maskRemap1;
    uint materialId;
    uint flags;
    uint revision;
    uint geometryRevision;
    uint channels;
    uint padding0;
    uint padding1;
    uint padding2;
};

float GISelectMaterialChannel(float4 value, uint channel, float fallbackValue)
{
    if (channel == 0u) return value.r;
    if (channel == 1u) return value.g;
    if (channel == 2u) return value.b;
    if (channel == 3u) return value.a;
    return fallbackValue;
}

uint GIMaterialMetallicChannel(GIMaterialData material) { return material.channels & 7u; }
uint GIMaterialRoughnessChannel(GIMaterialData material) { return (material.channels >> 3u) & 7u; }
uint GIMaterialOpacityChannel(GIMaterialData material) { return (material.channels >> 6u) & 7u; }
uint GIMaterialAlphaSource(GIMaterialData material) { return (material.channels >> 9u) & 15u; }

// 16 bytes.
struct GIMaterialBindingData
{
    uint instanceIndex;
    uint subMeshIndex;
    uint materialIndex;
    uint padding;
};

// 32 bytes. References the GI-owned compact triangle stream, not Nanite's transient draw lists.
struct GIGeometryStreamData
{
    uint vertexOffset;
    uint indexOffset;
    uint triangleOffset;
    uint indexCount;
    uint triangleCount;
    uint flags;
    uint padding0;
    uint padding1;
};

struct GIBvhNode
{
    float3 boundsMin;
    uint leftFirst;
    float3 boundsMax;
    uint primitiveCount;
};

struct GIBvhPrimitive
{
    uint primitiveKey;
    uint firstTriangle;
    uint triangleCount;
    uint flags;
};

struct GIBvhRange
{
    uint rootNode;
    uint nodeCount;
    uint primitiveOffset;
    uint primitiveCount;
};

#define GI_BVH_PRIMITIVE_NANITE_PAGE (1u << 0)
#define GI_BVH_PRIMITIVE_INSTANCE    (1u << 1)

// 32 bytes. Instance-level emissive proposal plus Walker alias metadata.
struct GIEmissiveAliasData
{
    float4 worldBoundingSphere;
    float probability;
    float aliasProbability;
    uint aliasIndex;
    uint objectId;
};

// Canonical screen-path payload.  Persistent reservoirs serialize this across several
// textures, but all proposal/reconnection code operates on this complete representation.
// sourcePdf is the density that generated the path; targetPdf is evaluated at the receiver.
struct GIPathPayload
{
    float3 radiance;
    float pathLength;
    float3 throughput;
    float solidAnglePdf;
    float3 incomingDirection;
    float proposalPdf;
    float3 outgoingDirection;
    float targetPdf;
    float3 hitPosition;
    float hitDistance;
    float2 barycentrics;
    uint geometryId;
    uint triangleId;
    float3 geometricNormal;
    uint pathFlags;
    float3 shadingNormal;
    uint pathDepth;
    uint objectId;
    uint surfaceId;
    uint materialId;
    uint materialSlot;
    uint instanceRevision;
    uint geometryRevision;
    float weightSum;
    float proposalCount;
    uint age;
    uint visibilityAge;
    uint visibilityState;
};

#define GI_PATH_VALID             (1u << 0)
#define GI_PATH_FINITE_HIT        (1u << 1)
#define GI_PATH_TRIANGLE_REFINED  (1u << 2)
#define GI_PATH_VISIBILITY_VALID  (1u << 3)
#define GI_VISIBILITY_UNKNOWN 0u
#define GI_VISIBILITY_VISIBLE 1u
#define GI_VISIBILITY_BLOCKED 2u

#endif
