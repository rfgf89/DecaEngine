// Полукадровая размытая копия снимка сцены для SSR (см. SsrPass.cs) - один уровень «конусного»
// размытия: шероховатый луч читает её вместо резкого кадра (SsrSceneColor в SsrTracePS), и
// резолву остаётся меньше дисперсии. Гаусс 3x3 с шагом 2 пикселя ИСХОДНИКА поверх билинейного
// даунсэмпла - эффективное ядро ~6px полного разрешения, дальше добирает ratio estimator.
#include "SsrCommon.hlsl"

Texture2D _SceneTex;
SamplerState _SceneTex_sampler;

struct PSOutput
{
    float4 color : SV_TARGET;
};

static const float SsrBlurWeights[3] = { 0.27901, 0.44198, 0.27901 };

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    // Таргет полукадровый - UV строится по СВОЕМУ вьюпорту (viewport.zw защёлкнут рендер-
    // размером, пасс ставит свой), а сэмпл идёт нормированным UV - он одинаков в обоих
    // разрешениях.
    float2 halfSize = max(viewData.viewport.zw * 0.5, 1.0);
    float2 uv = input.pos.xy / halfSize;
    float2 texel = 1.0 / viewData.viewport.zw;

    float3 sum = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int y = 0; y < 3; y++)
    {
        [unroll]
        for (int x = 0; x < 3; x++)
        {
            float2 offset = float2((x - 1) * 2.0, (y - 1) * 2.0) * texel;
            float3 c = _SceneTex.SampleLevel(_SceneTex_sampler, saturate(uv + offset), 0.0).rgb;

            // Против «искр»: один яркий пиксель солнца в источнике разъезжался бы диском
            // размытия (тот же приём /(1+lum), что в резолве).
            c /= 1.0 + dot(c, float3(0.2126, 0.7152, 0.0722));
            sum += c * SsrBlurWeights[x] * SsrBlurWeights[y];
        }
    }

    sum /= max(1.0 - dot(sum, float3(0.2126, 0.7152, 0.0722)), 1e-3);
    output.color = float4(sum, 1.0);
    return output;
}
