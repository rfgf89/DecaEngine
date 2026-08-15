// Сверочный прогон трассировки: тот же набор лучей, что гоняет CPU-трассировщик из ProbeGiBaker,
// прогоняется через SceneTrace.hlsl, и результаты сравниваются на CPU (см. PreviewProbe). Смысл в
// том, что CPU-путь уже рабочий и служит эталоном - без такой сверки расхождение в обходе BVH
// вылезло бы позже, в виде необъяснимо кривого GI.

#include "SceneTrace.hlsl"

cbuffer TraceTestParams
{
    // x = сколько лучей в буферах (хвост последней группы обязан выйти без записи).
    uint4 TraceTestCount;
};

// xyz = начало луча, w = предельная дальность.
StructuredBuffer<float4> _TestRayOrigin;
// xyz = направление (нормализовано вызывающим).
StructuredBuffer<float4> _TestRayDirection;

// x = дистанция попадания (< 0 - промах), y = 1 при попадании в заднюю грань, zw - резерв.
// Нормаль и альбедо тут не сверяются: они однозначно выводятся из попавшего треугольника, так что
// совпадение дистанции уже означает, что обход нашёл ТОТ ЖЕ треугольник.
RWStructuredBuffer<float4> _TestResult;

[numthreads(64, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint index = threadId.x;
    if (index >= TraceTestCount.x)
    {
        return;
    }

    float4 origin = _TestRayOrigin[index];
    float3 direction = _TestRayDirection[index].xyz;

    SceneHit hit = SceneTraceClosest(origin.xyz, direction, origin.w);

    // z - маркер «шейдер сюда дошёл», w - число узлов BVH, как их видит шейдер. Вместе они
    // отличают «диспатч не отработал / буфер не привязан» (нули) от «обход отработал, но неверно».
    uint nodeCount, nodeStride;
    _SceneBvhNodes.GetDimensions(nodeCount, nodeStride);
    _TestResult[index] = float4(hit.hit ? hit.t : -1.0, hit.backface ? 1.0 : 0.0,
        777.0, (float)nodeCount);
}
