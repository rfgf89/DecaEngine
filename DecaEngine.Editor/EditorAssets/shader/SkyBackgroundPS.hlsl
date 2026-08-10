#include "Instancing.hlsl"

// Environment background for the model preview: samples the same prefiltered equirect panorama
// the PBR materials reflect (see PreviewEnvironmentMap / UnlitInstancedPS.hlsl), so what shows
// up in a chrome sphere is exactly what the backdrop shows behind it. Drawn as the first pass
// of the frame (depth test off, see ModelViewportEnvironment's sky PSO) and then overdrawn by
// geometry.
Texture2D    _EnvMap;
SamplerState _EnvMap_sampler;

cbuffer View
{
    ViewData viewData;
}

// Пользовательский поворот энвайронмента вокруг Y (ползунок света в превью, см.
// SkyPassResources.SetEnvironmentYaw) - тот же сдвиг equirect-U, что в UnlitInstancedPS.hlsl
// (PbrEnvYaw), чтобы фон и отражения вращались одинаково. 0 (zero-init) = без поворота.
cbuffer SkySettings
{
    float SkyEnvYaw;
    float SkyPad0, SkyPad1, SkyPad2;
}

static const float PI = 3.14159265359;

// Must match ModelViewportEnvironment.CameraFovDegrees (45): tan(45deg / 2).
static const float TanHalfFov = 0.41421356;

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
    PSOutput output;

    // Восстанавливаем мировое направление луча камеры: NDC -> view-простр. направление через
    // известный FOV/aspect, затем поворот view->world транспонированной (обратной для чистого
    // поворота) частью view-матрицы.
    float aspect = viewData.viewport.z / max(viewData.viewport.w, 1.0);
    float3 dirView = normalize(float3(input.ndc.x * TanHalfFov * aspect, input.ndc.y * TanHalfFov, 1.0));
    float3 dirWorld = mul(dirView, transpose((float3x3)viewData.view));

    float2 uv = float2(atan2(dirWorld.z, dirWorld.x) / (2.0 * PI) + 0.5 + SkyEnvYaw / (2.0 * PI),
                       acos(clamp(dirWorld.y, -1.0, 1.0)) / PI);

    // Слегка размытый мип - фон в превью-вьюверах традиционно мягче зеркальных отражений, чтобы
    // не спорить с моделью за внимание.
    float3 sky = _EnvMap.SampleLevel(_EnvMap_sampler, uv, 1.5).rgb;

    // Тот же ручной display-энкод, что в Lighting-режиме (таргет не *_SRGB).
    output.color = float4(pow(sky, 1.0 / 2.2), 1.0);
    return output;
}
