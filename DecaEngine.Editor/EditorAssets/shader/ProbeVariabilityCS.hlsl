// Свёртка изменчивости проб в число на объём - GPU-часть приёма «Probe Variability» из RTXGI-DDGI
// (там это ReductionCS.hlsl в два прохода по текстуре). Задача: узнать, сошёлся ли объём целиком,
// чтобы перестать тратить на него лучи вовсе, пока сцену и свет не тронут.
//
// Считает это ОТДЕЛЬНЫЙ шейдер, а не раунд проб, по двум причинам. Во-первых, раунд режется на
// порции и растягивается на кадры (см. ProbeRoundGpu.RunRound), а свёртка обязана видеть все пробы
// разом - её место строго после последней порции. Во-вторых, раунд компилируется под трассировку
// (кейворд SCENE_TRACE_HARDWARE, шейдерная модель 6.5, компилятор DXC), а здесь не нужно ни
// ускоряющих структур, ни BVH - незачем тянуть их в конвейер, которому они не сдались.
//
// Проходов, в отличие от эталона, ОДИН: он сворачивает пробы до PROBE_VARIABILITY_GROUPS частичных
// сумм, а последние PROBE_VARIABILITY_GROUPS чисел складывает CPU при вычитывании. Второй проход
// на GPU ради шестидесяти четырёх сложений не окупает ни своего диспатча, ни барьера.

// x = сумма коэффициентов вариации, y = сумма весов (см. _ProbeVariability в ProbeRoundCS).
StructuredBuffer<float2>   _ProbeVariability;
RWStructuredBuffer<float2> _ProbeVariabilitySum;

cbuffer ProbeVariabilityParams
{
    // x = сколько всего проб, yzw - резерв.
    float4 VariabilityParams;
};

#define PROBE_VARIABILITY_THREADS 64
#define PROBE_VARIABILITY_GROUPS  64

groupshared float2 SharedSum[PROBE_VARIABILITY_THREADS];

[numthreads(PROBE_VARIABILITY_THREADS, 1, 1)]
void mainVariability(uint3 groupId : SV_GroupID, uint threadId : SV_GroupIndex)
{
    uint probeCount = (uint)VariabilityParams.x;
    uint stride = PROBE_VARIABILITY_THREADS * PROBE_VARIABILITY_GROUPS;

    // Шаг по СЕТКЕ целиком, а не сплошным куском на группу: соседние потоки читают соседние пробы,
    // то есть выборка остаётся слитной, а нагрузка ровной независимо от того, как изменчивость
    // распределена по объёму (пробы одного угла сцены лежат в буфере рядом).
    float2 sum = float2(0.0, 0.0);
    for (uint i = groupId.x * PROBE_VARIABILITY_THREADS + threadId; i < probeCount; i += stride)
    {
        sum += _ProbeVariability[i];
    }

    SharedSum[threadId] = sum;
    GroupMemoryBarrierWithGroupSync();

    // Обычное дерево вдвое: 64 -> 32 -> ... -> 1.
    [unroll]
    for (uint s = PROBE_VARIABILITY_THREADS / 2; s > 0; s >>= 1)
    {
        if (threadId < s)
        {
            SharedSum[threadId] += SharedSum[threadId + s];
        }

        GroupMemoryBarrierWithGroupSync();
    }

    if (threadId == 0)
    {
        _ProbeVariabilitySum[groupId.x] = SharedSum[0];
    }
}
