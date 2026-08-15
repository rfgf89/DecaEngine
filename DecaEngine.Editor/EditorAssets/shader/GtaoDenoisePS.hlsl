// Краесохраняющий денойзер GTAO (XeGTAO_Denoise) - третье и последнее звено конвейера
// (см. GtaoCommon.hlsl). Читает оценку видимости и упакованные «рёбра» из одного RGBA8-таргета,
// пишет отфильтрованную видимость, уже домноженную обратно на GtaoOcclusionTermScale.
//
// Почему не билатеральный блюр по глубине, как у композита SSAO (SsaoCompositeCommon.hlsl):
// главный пасс УЖЕ посчитал рёбра - меру принадлежности соседа той же поверхности - причём по той
// же схеме, по которой строил нормаль, с поправкой на склон. Веса из них и точнее (наклонная
// плоскость не выглядит обрывом), и дешевле: не нужно ни повторно читать глубину, ни
// реконструировать её допуск.
//
// Фильтр несимметричен по построению: у пикселя A правое ребро и у его правого соседа B левое
// ребро считались независимо и не обязаны совпадать. Умножение центральных рёбер на встречные
// рёбра соседей делает связь симметричной - иначе AO протекало бы через силуэт в одну сторону.
#include "Instancing.hlsl"
#include "GtaoShared.hlsl"

// Сырой результат главного пасса: .r - видимость / GtaoOcclusionTermScale, .g - упакованные рёбра.
Texture2D _AoTex;
SamplerState _AoTex_sampler;

cbuffer View
{
    ViewData viewData;
}

// Зеркалит AoConstantsData (SsaoPass.cs) - денойзеру нужен только нижний предел видимости:
// применять его до фильтрации бессмысленно, среднее всё равно ушло бы ниже.
cbuffer AoConstants
{
    float aoWorldRange;
    float aoPower;
    float aoFloor;
    float aoConstantsPad2;
}

// Вес диагональных соседей: половина ортогонального (диагональ вдвое дальше), плюс поправка 0.85
// из XeGTAO.
static const float DiagWeight = 0.85 * 0.5;

// Небольшая «протечка» AO там, где у пикселя закрыты три-четыре стороны: изолированный пиксель
// (тонкая ветка, у которой все соседи - фон) иначе остался бы один на один со своим шумом, и это
// видно и как пространственный, и как временной алиасинг.
static const float LeakThreshold = 2.5;
static const float LeakStrength = 0.5;

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

float2 LoadAo(int2 pixel, float2 viewportSize)
{
    pixel = clamp(pixel, int2(0, 0), int2(viewportSize) - 1);
    return _AoTex.Load(int3(pixel, 0)).rg;
}

float4 LoadEdges(int2 pixel, float2 viewportSize)
{
    return GtaoUnpackEdges(LoadAo(pixel, viewportSize).g);
}

PSOutput Main(in VSOutput input)
{
    float2 viewportSize = viewData.viewport.zw;
    int2 pixel = int2(input.pos.xy);

    float4 edgesC = LoadEdges(pixel, viewportSize);
    float4 edgesL = LoadEdges(pixel + int2(-1, 0), viewportSize);
    float4 edgesR = LoadEdges(pixel + int2(1, 0), viewportSize);
    float4 edgesT = LoadEdges(pixel + int2(0, -1), viewportSize);
    float4 edgesB = LoadEdges(pixel + int2(0, 1), viewportSize);

    // Симметризация: центральное левое ребро домножается на правое ребро левого соседа и т.д.
    edgesC *= float4(edgesL.y, edgesR.x, edgesT.w, edgesB.z);

    float edginess = (saturate(4.0 - LeakThreshold - dot(edgesC, float4(1.0, 1.0, 1.0, 1.0))) / (4.0 - LeakThreshold)) * LeakStrength;
    edgesC = saturate(edgesC + edginess);

    // Диагональ доступна только если к ней ведёт хотя бы один непрерывный путь по ортогоналям -
    // «обход за угол» через два ребра. Так AO не перепрыгивает диагональный силуэт.
    float weightTL = DiagWeight * (edgesC.x * edgesL.z + edgesC.z * edgesT.x);
    float weightTR = DiagWeight * (edgesC.z * edgesT.y + edgesC.y * edgesR.z);
    float weightBL = DiagWeight * (edgesC.w * edgesB.x + edgesC.x * edgesL.w);
    float weightBR = DiagWeight * (edgesC.y * edgesR.w + edgesC.w * edgesB.y);

    float sumWeight = GtaoDenoiseBlurBeta;
    float sum = LoadAo(pixel, viewportSize).r * sumWeight;

    sum += edgesC.x * LoadAo(pixel + int2(-1, 0), viewportSize).r;
    sum += edgesC.y * LoadAo(pixel + int2(1, 0), viewportSize).r;
    sum += edgesC.z * LoadAo(pixel + int2(0, -1), viewportSize).r;
    sum += edgesC.w * LoadAo(pixel + int2(0, 1), viewportSize).r;
    sumWeight += dot(edgesC, float4(1.0, 1.0, 1.0, 1.0));

    sum += weightTL * LoadAo(pixel + int2(-1, -1), viewportSize).r;
    sum += weightTR * LoadAo(pixel + int2(1, -1), viewportSize).r;
    sum += weightBL * LoadAo(pixel + int2(-1, 1), viewportSize).r;
    sum += weightBR * LoadAo(pixel + int2(1, 1), viewportSize).r;
    sumWeight += weightTL + weightTR + weightBL + weightBR;

    float ao = saturate(sum / sumWeight * GtaoOcclusionTermScale);

    // Нижний предел - ручка окна Graphics (см. SsaoPassResources.SetStrength); отрицательное
    // значение означает «дефолт шейдера» (кбуфер вне превью нулевой).
    ao = max(ao, aoFloor >= 0.0 ? aoFloor : GtaoDefaultFloor);

    PSOutput output;
    output.color = float4(ao.xxx, 1.0);
    return output;
}
