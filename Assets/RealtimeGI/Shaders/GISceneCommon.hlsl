#ifndef REALTIME_GI_SCENE_COMMON_INCLUDED
#define REALTIME_GI_SCENE_COMMON_INCLUDED

#define GI_SCENE_ABI_VERSION 3u

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
    uint padding;
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

// 96 bytes.
struct GIMaterialData
{
    float4 baseColor;
    float4 emissive;
    float4 surface; // roughness, metallic, opacity, alpha cutoff
    float4 baseMapST;
    float4 emissionMapST;
    uint materialId;
    uint flags;
    uint revision;
    uint textureIndex;
};

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

// 32 bytes. Instance-level emissive proposal plus Walker alias metadata.
struct GIEmissiveAliasData
{
    float4 worldBoundingSphere;
    float probability;
    float aliasProbability;
    uint aliasIndex;
    uint objectId;
};

#endif
