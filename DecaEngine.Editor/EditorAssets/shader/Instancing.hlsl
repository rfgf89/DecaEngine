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
};

RWStructuredBuffer<uint> BatchCounters;