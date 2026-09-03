struct CullData
{
    float4x4 view;

    float P00, P11, znear, zfar;
    float4 frustum;
    float lodTarget; // lod target error at z=1

    int drawCount;
    int cullFrustum; // 0 no cull, 1 cullFrustum, 2 cullLod
};

struct GPULodLevel
{
    float error;
    int firstIndex;
    int indexCount;
    int vertexOffset;
};

struct PerMeshData
{
    float4 bounds; // xyz: center, w: radius
    uint lodCount;
    uint physicalCommandOffset;
    uint pad1, pad2;
    GPULodLevel lods[8];
};

struct DrawData
{
    float4 positionScale;
    float4 orientation;
    // xyz = per-component scale (mirrors DrawData.cs); the culling sphere centre must scale by this,
    // not by the max in positionScale.w.
    float4 scale3;
};

struct DrawIndexedIndirectCommand
{
    uint numIndices;
    uint numInstances;
    uint firstIndexLocation;
    int baseVertex;
    uint firstInstanceLocation;
};

struct IndirectInstance
{
    int batchId;
    int objectId;
};

struct GPURenderInstance
{
    float4x4 modelMatrix;
};

struct ViewData
{
    float4x4 view;
    float4x4 viewProj;
    float4 viewport;
    float3 CameraWorldPos;
    float pad;
};

struct LightData
{
    float4 LightPos; // w component is light type
    float4 LightColor; // w is intensity
    float4 LightDirection;
    float4 SpotAngles; // x is inner, y is outer, z is shadow strength

    float4x4 CascadeMatrix[4];
    float4 CascadeSplits;
    float4 CascadeSizes;
    float4 CascadeNearPlanes;

    // x = this camera's segment offset in the PunctualLights pool, y = its light count (0 disables
    // the clustered branch), zw = zNear/zFar of the exponential cluster slices.
    float4 ClusterParams;
};

// ----- Clustered punctual lights (point/spot) ----------------------------------------------------
// Froxel grid: screen tiles x exponential depth slices. Mirrors LightClusters (LightData.cs) - change both.
#define CLUSTER_GRID_X 16
#define CLUSTER_GRID_Y 8
#define CLUSTER_GRID_Z 24
#define CLUSTER_COUNT (CLUSTER_GRID_X * CLUSTER_GRID_Y * CLUSTER_GRID_Z)
#define CLUSTER_MAX_LIGHTS 32
// One thread per cluster; mirrors LightClusters.CullGroupSize, which sizes the dispatch.
#define CLUSTER_CULL_GROUP 64
#define PUNCTUAL_SHADOW_SLICES 16
// Mirrors LightClusters.ShadowMapSize; sun cascades use their own resolution.
#define PUNCTUAL_SHADOW_MAP_SIZE 1024.0

// Mirrors PunctualLight (LightData.cs). Positions and directions are WORLD space; clustering
// converts to view itself, shading stays in world space.
struct PunctualLight
{
    float4 PositionRange;  // xyz = world position, w = range
    float4 ColorIntensity; // rgb = linear color, w = intensity
    float4 DirectionType;  // xyz = world cone direction, w = type: 0 point, 1 spot
    float4 SpotAngles;     // x = cos outer half-angle, y = 1/(cosInner-cosOuter), z = sin outer
    float4 ShadowParams;   // x = first shadow slice (-1 = none; point lights use 6 in a row),
                           // y = strength, z = slice near plane (far = PositionRange.w),
                           // w = world radius of the emitter (0 = default PCSS penumbra)
};

// Flat cluster index: screen tiles by rows, depth slices as the outermost dimension. Shared by the
// writer (LightClusterCS) and the reader (UnlitInstancedPS) - the layout must match.
uint ClusterFlatIndex(uint3 c)
{
    return (c.z * CLUSTER_GRID_Y + c.y) * CLUSTER_GRID_X + c.x;
}

RWStructuredBuffer<uint> BatchCounters;