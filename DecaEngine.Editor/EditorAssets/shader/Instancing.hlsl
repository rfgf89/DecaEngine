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
    // xyz - покомпонентный масштаб (зеркало DrawData.cs): центр кулинг-сферы обязан масштабироваться
    // им, а не максимумом из positionScale.w - см. комментарий в C#-структуре.
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

    // Кластеризованные punctual-света этой камеры: x - офсет сегмента камеры в пуле PunctualLights,
    // y - число светов сегмента, z/w - zNear/zFar экспоненциальных срезов кластерной сетки.
    // y == 0 - punctual-светов нет (превью, теневые каскады), кластерная ветка мёртвая.
    float4 ClusterParams;
};

// ----- Clustered punctual lights (point/spot) ----------------------------------------------------
// Фроксел-сетка вьюпорта: CLUSTER_GRID_X * CLUSTER_GRID_Y тайлов экрана, CLUSTER_GRID_Z
// экспоненциальных срезов глубины. Зеркалит LightClusters (LightData.cs) - менять только парой.
#define CLUSTER_GRID_X 16
#define CLUSTER_GRID_Y 8
#define CLUSTER_GRID_Z 24
#define CLUSTER_COUNT (CLUSTER_GRID_X * CLUSTER_GRID_Y * CLUSTER_GRID_Z)
#define CLUSTER_MAX_LIGHTS 32
// Тредов в группе кластеризации (LightClusterCS.hlsl): один тред - один кластер, и он же за батч
// подтягивает один свет в groupshared. Зеркалит LightClusters.CullGroupSize - им же считается
// число групп в ExecuteLightClustering.
#define CLUSTER_CULL_GROUP 64
#define PUNCTUAL_SHADOW_SLICES 16
// Сторона одного слайса теней punctual-света. Зеркалит LightClusters.ShadowMapSize (LightData.cs) -
// у каскадов солнца своё разрешение (4096), так что общей константы на все тени нет.
#define PUNCTUAL_SHADOW_MAP_SIZE 1024.0

// Зеркало PunctualLight (LightData.cs): позиция/направление МИРОВЫЕ, кластеризация переводит их во
// view сама (LightClusterCS.hlsl), шейдинг работает в мировом пространстве (UnlitInstancedPS.hlsl).
struct PunctualLight
{
    float4 PositionRange;  // xyz - мировая позиция, w - радиус действия
    float4 ColorIntensity; // rgb - линейный цвет, w - интенсивность
    float4 DirectionType;  // xyz - мировое направление конуса (спот), w - тип: 0 point, 1 spot
    float4 SpotAngles;     // x - cos внешнего полуугла, y - 1/(cosInner-cosOuter), z - sin внешнего
    float4 ShadowParams;   // x - первый слайс тени (-1 = нет; точечный: 6 граней подряд), y - сила,
                           // z - ближняя плоскость слайса (far = PositionRange.w),
                           // w - мировой радиус светящегося тела (0 = дефолт, полутень PCSS)
};

// Плоский индекс кластера: тайлы экрана идут строками, срезы глубины - самым старшим измерением.
// ОБЩАЯ для записи (LightClusterCS) и чтения (UnlitInstancedPS) - раскладка обязана совпадать.
uint ClusterFlatIndex(uint3 c)
{
    return (c.z * CLUSTER_GRID_Y + c.y) * CLUSTER_GRID_X + c.x;
}

RWStructuredBuffer<uint> BatchCounters;