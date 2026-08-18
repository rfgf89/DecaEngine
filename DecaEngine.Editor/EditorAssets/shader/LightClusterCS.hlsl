// Кластеризация punctual-светов (GPU-половина по-типового кулинга, CPU-половина - LightCulling.cs):
// один тред на фроксел-кластер, перебирает сегмент пула светов текущей камеры (границы - в
// lightData.ClusterParams) и пишет индексы попавших светов в свой отрезок ClusterIndices
// фиксированного шага CLUSTER_MAX_LIGHTS. У каждого типа света свой тест против кластера:
// точечный - сфера против AABB кластера, спот - ограничивающая сфера конуса против AABB кластера
// И конус против ограничивающей сферы кластера (тест Вронского); направленный в пул не попадает
// вовсе (идёт каскадным путём).
//
// Света идут батчами по CLUSTER_CULL_GROUP через groupshared: перевод свет->view делает ОДИН тред
// на свет за группу, а не каждый кластер заново (иначе на 3072 кластера это тот же mul() три тысячи
// раз на один и тот же свет). Отсюда же требование к структуре циклов: они обязаны быть
// одинаковыми у всех тредов группы - никаких ранних выходов между барьерами.

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

// View-space представление света для теста против кластера - то, что стоит считать один раз на
// группу. Ограничивающая сфера (BoundSphere) у точечного совпадает с самим светом, у спота это
// минимальная сфера вокруг конуса, поэтому дешёвый тест сфера-против-AABB общий для обоих типов.
groupshared float4 gsPosRange[CLUSTER_CULL_GROUP];   // xyz - позиция во view, w - радиус действия
groupshared float4 gsDirType[CLUSTER_CULL_GROUP];    // xyz - ось конуса во view, w - тип (0/1)
groupshared float4 gsBoundSphere[CLUSTER_CULL_GROUP]; // xyz - центр охватывающей сферы, w - радиус
groupshared float2 gsSpotAngles[CLUSTER_CULL_GROUP]; // x - cos внешнего полуугла, y - sin

// Сфера против AABB кластера - квадрат расстояния от центра до ближайшей точки AABB против квадрата
// радиуса (Ericson). Для точечного света это точный тест, для спота - консервативный по его
// охватывающей сфере.
bool SphereIntersectsAabb(float3 center, float radius, float3 aabbMin, float3 aabbMax)
{
    float3 closest = clamp(center, aabbMin, aabbMax);
    float3 d = center - closest;
    return dot(d, d) <= radius * radius;
}

// Спот: конус (апекс, ось, высота range, cos/sin внешнего полуугла) против ограничивающей сферы
// кластера - тест Вронского: расстояние от центра сферы до ближайшей точки поверхности конуса
// вдоль перпендикуляра к образующей, плюс отсечки по оси спереди/сзади. Сам по себе он слишком
// щедрый (сфера кластера заметно больше фроксела), поэтому идёт В ПАРЕ с SphereIntersectsAabb.
bool ConeIntersectsSphere(float3 apexView, float3 dirView, float range, float cosOuter,
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

// Минимальная охватывающая сфера конуса высоты range с полууглом a - зеркалит CPU-версию в
// LightCulling.IsSpotLightVisible: до 45 градусов (sin <= cos) сфера проходит через апекс и кромку
// основания, дальше - описана вокруг основания.
float4 ConeBoundingSphere(float3 apex, float3 dir, float range, float cosOuter, float sinOuter)
{
    if (sinOuter <= cosOuter)
    {
        float t = range / max(2.0 * cosOuter * cosOuter, 1e-4);
        return float4(apex + dir * t, t);
    }

    return float4(apex + dir * range, range * sinOuter / max(cosOuter, 1e-4));
}

[numthreads(CLUSTER_CULL_GROUP, 1, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID, uint3 GTid : SV_GroupThreadID)
{
    uint clusterIdx = DTid.x;
    // Хвостовая группа (если сетка перестанет делиться на CLUSTER_CULL_GROUP) не выходит здесь по
    // return: её треды обязаны дойти до общих барьеров. Лишний тред просто ничего не пишет.
    bool validCluster = clusterIdx < CLUSTER_COUNT;

    uint lightCount = (uint)lightData.ClusterParams.y;
    // Ранний выход ДО геометрии кластера: при пустом сегменте ClusterParams.zw могут быть нулями,
    // и математика срезов дала бы NaN. Условие однородно для всего диспатча (значение из кбуфера),
    // так что барьеры ниже не рассинхронизируются. Counts занулить обязательно - иначе шейдинг
    // прочтёт кластеры, оставшиеся от предыдущей камеры.
    if (lightCount == 0)
    {
        if (validCluster)
            ClusterCounts[clusterIdx] = 0;
        return;
    }

    uint cx = clusterIdx % CLUSTER_GRID_X;
    uint cy = (clusterIdx / CLUSTER_GRID_X) % CLUSTER_GRID_Y;
    uint cz = (clusterIdx / (CLUSTER_GRID_X * CLUSTER_GRID_Y)) % CLUSTER_GRID_Z;

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
    uint written = 0; // сколько реально влезло в отрезок кластера
    uint hits = 0;    // сколько попало ВСЕГО, включая не влезшие - это и уходит в ClusterCounts

    for (uint base = 0; base < lightCount; base += CLUSTER_CULL_GROUP)
    {
        // Барьер ПЕРЕД записью батча, а не только после: иначе быстрый тред затрёт свет, который
        // отстающие ещё читают из предыдущей итерации.
        GroupMemoryBarrierWithGroupSync();

        uint loadIdx = base + GTid.x;
        if (loadIdx < lightCount)
        {
            PunctualLight light = PunctualLights[offset + loadIdx];
            float range = light.PositionRange.w;
            float3 posView = mul(float4(light.PositionRange.xyz, 1.0), cullData.view).xyz;
            float type = light.DirectionType.w;

            float3 dirView = float3(0.0, 0.0, 1.0);
            float4 bound = float4(posView, range);
            if (type > 0.5)
            {
                dirView = normalize(mul(float4(light.DirectionType.xyz, 0.0), cullData.view).xyz);
                bound = ConeBoundingSphere(posView, dirView, range, light.SpotAngles.x, light.SpotAngles.z);
            }

            gsPosRange[GTid.x] = float4(posView, range);
            gsDirType[GTid.x] = float4(dirView, type);
            gsBoundSphere[GTid.x] = bound;
            gsSpotAngles[GTid.x] = float2(light.SpotAngles.x, light.SpotAngles.z);
        }

        GroupMemoryBarrierWithGroupSync();

        uint batch = min((uint)CLUSTER_CULL_GROUP, lightCount - base);
        for (uint j = 0; j < batch; j++)
        {
            float4 bound = gsBoundSphere[j];
            if (!SphereIntersectsAabb(bound.xyz, bound.w, aabbMin, aabbMax))
                continue;

            float4 posRange = gsPosRange[j];
            float4 dirType = gsDirType[j];
            if (dirType.w > 0.5 && !ConeIntersectsSphere(posRange.xyz, dirType.xyz, posRange.w,
                    gsSpotAngles[j].x, gsSpotAngles[j].y, sphereCenter, sphereRadius))
                continue;

            hits++;
            // Переполнение отрезка не обрывает перебор: счётчик должен остаться честным, иначе по
            // ClusterCounts не отличить "ровно 32 света" от "их 200 и 168 потеряно" (диагностика -
            // канал 14 в UnlitInstancedPS). Шейдинг сам клампит счётчик при чтении.
            if (written < CLUSTER_MAX_LIGHTS)
            {
                if (validCluster)
                    ClusterIndices[clusterIdx * CLUSTER_MAX_LIGHTS + written] = offset + base + j;
                written++;
            }
        }
    }

    if (validCluster)
        ClusterCounts[clusterIdx] = hits;
}
