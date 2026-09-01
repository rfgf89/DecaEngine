// Композит SSR в HDR-кадр (см. SsrPass.cs). Энергетически корректная ЗАМЕНА, а не сложение:
// форвард-пасс уже вложил в кадр env-спекуляр envColor * factor (factor записан в G-buffer,
// см. FEATURE_REFLECTION_GBUFFER в UnlitInstancedPS), поэтому здесь
//   out = scene + conf * factor * (ssr * intensity - envColor)
// - на confidence-долю пикселя префильтрованное окружение вычитается и вместо него встаёт
// трейс; при conf = 0 кадр не меняется вовсе (промахи честно остаются на env-карте).
#include "SsrCommon.hlsl"

Texture2D<float> _DepthTex;
Texture2D _SceneTex;
Texture2D _SsrTex;
Texture2D _NormalRoughTex;
Texture2D _EnvFactorTex;
Texture2D _EnvMap;
SamplerState _EnvMap_sampler;

struct PSOutput
{
    float4 color : SV_TARGET;
};

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 viewportSize = viewData.viewport.zw;
    int2 pixel = int2(input.pos.xy);

    float4 scene = _SceneTex.Load(int3(pixel, 0));
    float4 ssr = _SsrTex.Load(int3(pixel, 0));
    float4 gbuffer = _NormalRoughTex.Load(int3(pixel, 0));
    float4 envFactor = _EnvFactorTex.Load(int3(pixel, 0));
    float centerRaw = _DepthTex.Load(int3(pixel, 0));

    if (ssrDebugView > 3.5)
    {
        // Вид 4: альбедо RT-хитов; вид 5: карта цепочки отскоков (см. SsrTracePS - там же
        // расшифровка цветов). Оба приходят из трейса как есть.
        output.color = float4(ssr.rgb, 1.0);
        return output;
    }
    if (ssrDebugView > 2.5)
    {
        output.color = float4(gbuffer.xyz * 0.5 + 0.5, 1.0);
        return output;
    }
    if (ssrDebugView > 1.5)
    {
        output.color = float4(ssr.aaa, 1.0);
        return output;
    }
    if (ssrDebugView > 0.5)
    {
        output.color = float4(ssr.rgb * ssr.a, 1.0);
        return output;
    }

    float conf = ssr.a;
    if (centerRaw < 1e-6 || conf <= 0.0 || dot(gbuffer.xyz, gbuffer.xyz) < 0.5)
    {
        output.color = scene;
        return output;
    }

    // Зеркальный env-цвет, который вложил форвард-пасс, - восстанавливается из G-buffer той же
    // формулой (нормаль шейдинга, roughness-мип префильтрованной equirect-карты, тот же yaw).
    float3 nWorld = normalize(gbuffer.xyz);
    float3 P = SsrViewPos(pixel, centerRaw, viewportSize);
    float3 vView = -normalize(P);
    float3 nView = SsrWorldDirToView(nWorld);
    float3 rWorld = SsrViewDirToWorld(reflect(-vView, nView));
    float3 envColor = SsrSampleEnvironment(_EnvMap, _EnvMap_sampler, rWorld, gbuffer.a);

    // envFactor.rgb - множитель БЕЗ окклюзии неба, envFactor.a - сама окклюзия (см.
    // FEATURE_REFLECTION_GBUFFER в UnlitInstancedPS): вычитается ровно тот env-вклад, что
    // сложил форвард (factor * envOcclusion * envColor), а трейс подмешивается без неё -
    // экранная геометрия в отражении не «небо» и запечённой видимостью неба не глушится.
    //
    // НО только у зеркала: его один луч честно знает, что видит, и глушить его окклюзией
    // нельзя (зеркало в интерьере чернело - причина решения выше). У шероховатой поверхности
    // лоб широкий, трейс покрывает его парой стохастических направлений, а запечённая
    // окклюзия - честная средняя видимость по полусфере; без неё МАТОВЫЕ поверхности в
    // тёмных углах ДОБАВЛЯЛИ яркость там, где вычитать было нечего (envOcclusion ~ 0) -
    // «в тени всё слишком отражает».
    float occlusionGate = lerp(envFactor.a, 1.0, smoothstep(0.45, 0.1, gbuffer.a));
    float3 delta = envFactor.rgb * conf *
        (ssr.rgb * ssrIntensity * occlusionGate - envFactor.a * envColor);
    output.color = float4(max(scene.rgb + delta, 0.0), scene.a);
    return output;
}
