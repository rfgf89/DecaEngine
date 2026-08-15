// Звено мип-цепочки линейных глубин GTAO (XeGTAO_DepthMIPFilter): вдвое уменьшает предыдущий
// уровень. Главный пасс выбирает уровень по длине шага сэмпла - дальние сэмплы читают грубые
// мипы, и это не оптимизация «на глазок», а способ не промахиваться мимо геометрии: одиночный
// тап на большом расстоянии от точки попадает в случайный тексель, шумит от кадра к кадру и
// пропускает тонкие объекты между тапами, тогда как фильтрованный мип несёт уже усреднённую
// глубину всей накрытой области.
//
// Фильтр НЕ обычный бокс: усреднять глубины через силуэт бессмысленно (среднее между стеной и
// фоном не лежит ни на одной из поверхностей). Вместо этого четыре глубины взвешиваются по тому
// же falloff, что и сэмплы в главном пассе, относительно САМОЙ ДАЛЬНЕЙ из них: то, что дальше
// радиуса влияния от дальнего плана, из среднего фактически выпадает.
#include "Instancing.hlsl"
#include "GtaoShared.hlsl"

Texture2D _SourceTex;
SamplerState _SourceTex_sampler;

cbuffer View
{
    ViewData viewData;
}

// Зеркалит AoConstantsData (SsaoPass.cs) - тот же кбуфер, что у главного пасса: фильтру нужен
// мировой радиус, чтобы взвешивать глубины ровно тем же falloff.
cbuffer AoConstants
{
    float aoWorldRange;
    float aoPower;
    float aoFloor;
    float aoConstantsPad2;
}

// Размеры этого звена и его источника - зеркалит GtaoLevelData (SsaoPass.cs). Собственный размер
// таргета неоткуда взять: viewData.viewport несёт полное разрешение кадра и после SetViewport на
// звено не меняется.
cbuffer GtaoLevel
{
    float4 gtaoTargetSize; // xy - размер, zw - 1/xy
    float4 gtaoSourceSize;
}

// Радиус фильтра мипов чуть уже радиуса самого эффекта - подобрано эмпирически (XeGTAO,
// depthRangeScaleFactor): усреднять до самой границы влияния значит затягивать в мип геометрию,
// вклад которой в этой точке уже почти нулевой.
static const float DepthRangeScaleFactor = 0.75;

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

PSOutput Main(in VSOutput input)
{
    int2 dst = int2(input.pos.xy);
    int2 src = dst * 2;
    int2 maxSrc = int2(gtaoSourceSize.xy) - 1;

    float depth0 = _SourceTex.Load(int3(min(src + int2(0, 0), maxSrc), 0)).r;
    float depth1 = _SourceTex.Load(int3(min(src + int2(1, 0), maxSrc), 0)).r;
    float depth2 = _SourceTex.Load(int3(min(src + int2(0, 1), maxSrc), 0)).r;
    float depth3 = _SourceTex.Load(int3(min(src + int2(1, 1), maxSrc), 0)).r;

    float maxDepth = max(max(depth0, depth1), max(depth2, depth3));

    float effectRadius = DepthRangeScaleFactor * GtaoEffectRadius(maxDepth, viewData.viewport.w, aoWorldRange);
    float falloffRange = max(GtaoFalloffRange * effectRadius, 1e-5);
    float falloffFrom = effectRadius * (1.0 - GtaoFalloffRange);
    float falloffMul = -1.0 / falloffRange;
    float falloffAdd = falloffFrom / falloffRange + 1.0;

    float weight0 = saturate((maxDepth - depth0) * falloffMul + falloffAdd);
    float weight1 = saturate((maxDepth - depth1) * falloffMul + falloffAdd);
    float weight2 = saturate((maxDepth - depth2) * falloffMul + falloffAdd);
    float weight3 = saturate((maxDepth - depth3) * falloffMul + falloffAdd);

    // Самая дальняя глубина всегда имеет вес 1 (её разница с maxDepth нулевая), так что сумма
    // весов не выродится в ноль даже когда все четыре тапа разъехались за радиус.
    float weightSum = weight0 + weight1 + weight2 + weight3;
    float filtered = (weight0 * depth0 + weight1 * depth1 + weight2 * depth2 + weight3 * depth3) / weightSum;

    PSOutput output;
    output.color = float4(min(filtered, GtaoMaxViewDepth), 0.0, 0.0, 1.0);
    return output;
}
