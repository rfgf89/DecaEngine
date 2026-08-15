// Кластеризация punctual-светов (GPU-половина по-типового кулинга, CPU-половина - LightCulling.cs):
// один тред на фроксел-кластер, перебирает сегмент пула светов текущей камеры (границы - в
// lightData.ClusterParams) и пишет индексы попавших светов в свой отрезок ClusterIndices
// фиксированного шага CLUSTER_MAX_LIGHTS. У каждого типа света свой тест против кластера:
// точечный - сфера против AABB кластера, спот - конус против ограничивающей сферы кластера
// (тест Вронского), направленный в пул не попадает вовсе (идёт каскадным путём).

#include "Instancing.hlsl"

cbuffer Constants
{
    CullData cullData;
}

cbuffer Light
{
    LightData lightData;
}

// RW, а не SRV, у всех трёх: DiligentComputeMaterial всегда биндит компьюту UAV-вью буфера.
RWStructuredBuffer<PunctualLight> PunctualLights;
RWStructuredBuffer<uint> ClusterCounts;
RWStructuredBuffer<uint> ClusterIndices;

// Точечный свет: view-сфера против AABB кластера - квадрат расстояния от центра до ближайшей
// точки AABB против квадрата радиуса.
bool PointLightIntersectsCluster(float3 centerView, float range, float3 aabbMin, float3 aabbMax)
{
    float3 closest = clamp(centerView, aabbMin, aabbMax);
    float3 d = centerView - closest;
    return dot(d, d) <= range * range;
}

// Спот: конус (апекс, ось, высота range, cos/sin внешнего полуугла) против ограничивающей сферы
// кластера - тест Вронского: расстояние от центра сферы до ближайшей точки поверхности конуса
// вдоль перпендикуляра к образующей, плюс отсечки по оси спереди/сзади.
bool SpotLightIntersectsCluster(float3 apexView, float3 dirView, float range, float cosOuter,
    float sinOuter, float3 sphereCenter, float sphereRadius)
{
    float3 v = sphereCenter - apexView;
    float vLenSq = dot(v, v);
    float v1Len = dot(v, dirView);
    float distClosest = cosOuter * sqrt(max(vLenSq - v1Len * v1Len, 0.0)) - v1Len * sinOuter;

    bool angleCull = distClosest > sphereRadius;
    bool frontCull = v1Len > sphereRadius + range;
    bool backCull = v1Len < -sphereRadius;
    return !(angleCull || frontCull || backCull);
}

[numthreads(64, 1, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    uint clusterIdx = DTid.x;
    if (clusterIdx >= CLUSTER_COUNT)
        return;

    uint lightCount = (uint)lightData.ClusterParams.y;
    // Ранний выход ДО геометрии кластера: при пустом сегменте ClusterParams.zw могут быть нулями,
    // и математика срезов дала бы NaN. Counts занулить обязательно - иначе шейдинг прочтёт
    // кластеры, оставшиеся от предыдущей камеры.
    if (lightCount == 0)
    {
        ClusterCounts[clusterIdx] = 0;
        return;
    }

    uint cx = clusterIdx % CLUSTER_GRID_X;
    uint cy = (clusterIdx / CLUSTER_GRID_X) % CLUSTER_GRID_Y;
    uint cz = clusterIdx / (CLUSTER_GRID_X * CLUSTER_GRID_Y);

    // Экспоненциальные срезы глубины zNear..zFar (view-space z, LH-камера смотрит в +Z).
    float zNear = lightData.ClusterParams.z;
    float zFar = lightData.ClusterParams.w;
    float z0 = zNear * pow(zFar / zNear, cz / (float)CLUSTER_GRID_Z);
    float z1 = zNear * pow(zFar / zNear, (cz + 1) / (float)CLUSTER_GRID_Z);

    // NDC-границы тайла: тайловый y растёт ВНИЗ по экрану, NDC y - вверх, поэтому y-края
    // перевёрнуты. Обязано зеркалить обратное отображение пиксель->тайл в UnlitInstancedPS.
    float ndcX0 = 2.0 * cx / CLUSTER_GRID_X - 1.0;
    float ndcX1 = 2.0 * (cx + 1) / CLUSTER_GRID_X - 1.0;
    float ndcY0 = 1.0 - 2.0 * (cy + 1) / CLUSTER_GRID_Y;
    float ndcY1 = 1.0 - 2.0 * cy / CLUSTER_GRID_Y;

    // View-space AABB фроксела: clip.x = view.x * P00, w = view.z => view.x = ndc * z / P00.
    // Углы на обеих глубинах - фроксел расширяется с z, берём охватывающий AABB.
    float x00 = ndcX0 * z0 / cullData.P00, x10 = ndcX1 * z0 / cullData.P00;
    float x01 = ndcX0 * z1 / cullData.P00, x11 = ndcX1 * z1 / cullData.P00;
    float y00 = ndcY0 * z0 / cullData.P11, y10 = ndcY1 * z0 / cullData.P11;
    float y01 = ndcY0 * z1 / cullData.P11, y11 = ndcY1 * z1 / cullData.P11;

    float3 aabbMin = float3(min(min(x00, x10), min(x01, x11)), min(min(y00, y10), min(y01, y11)), z0);
    float3 aabbMax = float3(max(max(x00, x10), max(x01, x11)), max(max(y00, y10), max(y01, y11)), z1);

    // Ограничивающая сфера AABB - для конусного теста спотов.
    float3 sphereCenter = (aabbMin + aabbMax) * 0.5;
    float sphereRadius = length(aabbMax - aabbMin) * 0.5;

    uint offset = (uint)lightData.ClusterParams.x;
    uint written = 0;

    for (uint i = 0; i < lightCount && written < CLUSTER_MAX_LIGHTS; i++)
    {
        PunctualLight light = PunctualLights[offset + i];
        float range = light.PositionRange.w;
        float3 posView = mul(float4(light.PositionRange.xyz, 1.0), cullData.view).xyz;

        bool intersects;
        if (light.DirectionType.w > 0.5)
        {
            float3 dirView = normalize(mul(float4(light.DirectionType.xyz, 0.0), cullData.view).xyz);
            intersects = SpotLightIntersectsCluster(posView, dirView, range,
                light.SpotAngles.x, light.SpotAngles.z, sphereCenter, sphereRadius);
        }
        else
        {
            intersects = PointLightIntersectsCluster(posView, range, aabbMin, aabbMax);
        }

        if (intersects)
        {
            ClusterIndices[clusterIdx * CLUSTER_MAX_LIGHTS + written] = offset + i;
            written++;
        }
    }

    ClusterCounts[clusterIdx] = written;
}
