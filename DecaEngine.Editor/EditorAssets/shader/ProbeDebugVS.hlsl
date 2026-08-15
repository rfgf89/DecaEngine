// Дебаг-вид probe-GI: сфера-октаэдр на каждую пробу, В ФАКТИЧЕСКОЙ позиции - узел сетки плюс
// смещение релокации из атласа (см. ProbeGiBakeResult.Offset). Ровно для того и рисуется: глазами
// проверить, вышли ли пробы из стен и колонн, и увидеть, ЧТО каждая из них накопила.
//
// Ни вершинного, ни индексного буфера, ни инстансинга: один Draw(24 * probeCount), номер пробы и
// угол октаэдра восстанавливаются из SV_VertexID. Октаэдр - минимальный многогранник, читающийся
// как «шарик» (8 треугольников); на тысячах проб это единственно разумная цена.
#include "Instancing.hlsl"

cbuffer View
{
    ViewData viewData;
}

cbuffer ProbeDebugParams
{
    // xyz = угол сетки в мире, w = радиус сферы в мировых единицах.
    float4 DebugGridOrigin;
    // xyz = шаг сетки проб, w = число проб.
    float4 DebugGridCell;
    // x = кирпичей в ряду пула атласов (раскладка texel-а пробы), yzw = цветовая метка ОБЪЁМА:
    // каскады подкрашиваются, чтобы их шарики отличались от базовых (они мельче и стоят среди них).
    float4 DebugPool;
};

// Углы кирпичей - тот же буфер, что у compute-раунда (см. ProbeRoundGpu): xyz = угол в координатах
// виртуальной сетки, w = уровень подразделения.
StructuredBuffer<int4> _ProbeBrickOrigin;

// Атласы проб: смещение релокации, поле L0 (цвет) и валидность (альфа Sh1).
Texture2D _ProbeOffset;
Texture2D _ProbeSh0;
Texture2D _ProbeSh1;

#define PROBE_BRICK_PROBES 4

struct PSInput
{
    float4 pos      : SV_POSITION;
    float3 normal   : NORMAL;
    // rgb = SH L0 пробы (что она накопила), a = валидность (0 = проба считает себя в стене).
    float4 color    : COLOR0;
    // Длина смещения релокации в долях радиуса - PS подкрашивает переехавшие пробы.
    float  offsetLen : TEXCOORD0;
    // Цветовая метка объёма (см. DebugPool.yzw).
    float3 tint      : TEXCOORD1;
};

// Октаэдр из 8 треугольников: 6 вершин ±X/±Y/±Z, обход наружу. Нормаль на дебаг-шарике берётся
// по позиции вершины - для гранёного «шарика» этого достаточно.
static const float3 OctaVerts[24] =
{
    float3(0, 1, 0), float3(1, 0, 0), float3(0, 0, 1),
    float3(0, 1, 0), float3(0, 0, 1), float3(-1, 0, 0),
    float3(0, 1, 0), float3(-1, 0, 0), float3(0, 0, -1),
    float3(0, 1, 0), float3(0, 0, -1), float3(1, 0, 0),
    float3(0, -1, 0), float3(0, 0, 1), float3(1, 0, 0),
    float3(0, -1, 0), float3(-1, 0, 0), float3(0, 0, 1),
    float3(0, -1, 0), float3(0, 0, -1), float3(-1, 0, 0),
    float3(0, -1, 0), float3(1, 0, 0), float3(0, 0, -1),
};

// Тексель пробы в пуле - зеркало ProbeGiBaker.ProbeTexel / ProbeAtlasTexel в ProbeRoundCS.hlsl.
int2 DebugProbeTexel(uint slot)
{
    const uint perBrick = PROBE_BRICK_PROBES * PROBE_BRICK_PROBES * PROBE_BRICK_PROBES;
    uint brick = slot / perBrick;
    uint local = slot - brick * perBrick;
    uint poolColumns = max((uint)DebugPool.x, 1u);

    return int2(
        (brick % poolColumns) * PROBE_BRICK_PROBES + local % PROBE_BRICK_PROBES,
        (brick / poolColumns) * PROBE_BRICK_PROBES * PROBE_BRICK_PROBES
            + (local / (PROBE_BRICK_PROBES * PROBE_BRICK_PROBES)) * PROBE_BRICK_PROBES
            + (local / PROBE_BRICK_PROBES % PROBE_BRICK_PROBES));
}

PSInput Main(uint vid : SV_VertexID)
{
    uint probe = vid / 24;
    uint corner = vid - probe * 24;

    // Мировая позиция узла - зеркало mainProbe в ProbeRoundCS.hlsl.
    const uint perBrick = PROBE_BRICK_PROBES * PROBE_BRICK_PROBES * PROBE_BRICK_PROBES;
    uint brick = probe / perBrick;
    uint local = probe - brick * perBrick;
    int4 brickOrigin = _ProbeBrickOrigin[brick];
    int step = 1 << brickOrigin.w;
    int3 cell = brickOrigin.xyz + int3(
        (int)(local % PROBE_BRICK_PROBES) * step,
        (int)(local / PROBE_BRICK_PROBES % PROBE_BRICK_PROBES) * step,
        (int)(local / (PROBE_BRICK_PROBES * PROBE_BRICK_PROBES)) * step);

    int3 texel = int3(DebugProbeTexel(probe), 0);
    float3 offset = _ProbeOffset.Load(texel).rgb;
    float3 probePos = DebugGridOrigin.xyz + (float3)cell * DebugGridCell.xyz + offset;

    float radius = DebugGridOrigin.w;
    float3 n = normalize(OctaVerts[corner]);

    PSInput result;
    result.pos = mul(float4(probePos + OctaVerts[corner] * radius, 1.0), viewData.viewProj);
    result.normal = n;
    result.color = float4(_ProbeSh0.Load(texel).rgb, _ProbeSh1.Load(texel).a);
    result.offsetLen = length(offset) / max(radius, 1e-4);
    result.tint = DebugPool.yzw;
    return result;
}
