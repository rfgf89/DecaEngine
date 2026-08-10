// Общее тело SSGI-пасса превью (см. SsgiPS.hlsl / SsgiMsaaPS.hlsl - обёртки определяют макрос
// DEPTH_FETCH под одиночный или мультисемпловый депт). Экранная глобальная иллюминация: один
// отскок света, собранный из уже отрисованного кадра (_SceneTex - копия цветового таргета,
// см. SsgiPass) - освещённая поверхность подсвечивает соседнюю геометрию своим цветом
// (color bleeding), чего прямой свет и IBL дать не могут. Полноэкранный треугольник
// (SkyBackgroundVS), выход - RGB-накопленный bounce.
//
// Реконструкция позиции/нормали - та же, что в SsaoCommon.hlsl: infinite reversed-Z
// (z_view = near / depth), FOV фиксирован (ModelViewportEnvironment.CameraFovDegrees = 45).
#include "Instancing.hlsl"

Texture2D _SceneTex;

cbuffer View
{
    ViewData viewData;
}

// Мировой радиус сбора GI: пушится после кадрирования модели (см.
// ModelPreviewViewport.FrameAll -> SsgiPassResources.SetWorldRange) - радиус шире AO-шного,
// bounce тянется дальше контактной тени. Легаси-режим (0, никто не пушил) - радиус в долях
// экрана, falloff в долях глубины точки, как у SSAO.
// Паддинг тремя скалярами, НЕ float3: float3 по смещению 4 нарушает 16-байтное выравнивание
// std140/SPIR-V (см. историю в SsaoCommon.hlsl). Зеркалит GiConstantsData (SsgiPass.cs).
cbuffer GiConstants
{
    float giWorldRange;
    float giConstantsPad0;
    float giConstantsPad1;
    float giConstantsPad2;
}

static const float PI = 3.14159265359;
static const float TanHalfFov = 0.41421356; // tan(45deg / 2)
static const float NearPlane = 0.05;        // CameraData near в ModelViewportEnvironment

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

float ViewDepthAt(int2 pixel, float2 viewportSize)
{
    pixel = clamp(pixel, int2(0, 0), int2(viewportSize) - 1);
    float d = DEPTH_FETCH(pixel);
    return NearPlane / max(d, 1e-7);
}

float3 ViewPosAt(int2 pixel, float2 viewportSize)
{
    float zView = ViewDepthAt(pixel, viewportSize);
    float2 uv = (pixel + 0.5) / viewportSize;
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    float aspect = viewportSize.x / max(viewportSize.y, 1.0);
    return float3(ndc.x * TanHalfFov * aspect * zView, ndc.y * TanHalfFov * zView, zView);
}

// Нормаль из соседних глубин - тот же трюк depth-normal реконструкции, что в SsaoCommon.hlsl
// (из пар +/-1 берётся меньшая разница, чтобы не ловить обрывы на силуэтах).
float3 NormalAt(int2 pixel, float3 P, float2 viewportSize)
{
    float3 dxA = ViewPosAt(pixel + int2(1, 0), viewportSize) - P;
    float3 dxB = P - ViewPosAt(pixel - int2(1, 0), viewportSize);
    float3 dyA = ViewPosAt(pixel + int2(0, 1), viewportSize) - P;
    float3 dyB = P - ViewPosAt(pixel - int2(0, 1), viewportSize);
    float3 dx = abs(dxA.z) < abs(dxB.z) ? dxA : dxB;
    float3 dy = abs(dyA.z) < abs(dyB.z) ? dyA : dyB;
    float3 N = normalize(cross(dy, dx));
    if (N.z > 0.0)
    {
        N = -N;
    }
    return N;
}

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 viewportSize = viewData.viewport.zw;
    int2 pixel = int2(input.pos.xy);

    // Фон (reversed-Z очищается нулём) bounce не получает.
    float centerRaw = DEPTH_FETCH(pixel);
    if (centerRaw < 1e-6)
    {
        output.color = float4(0.0, 0.0, 0.0, 1.0);
        return output;
    }

    float3 P = ViewPosAt(pixel, viewportSize);
    float3 N = NormalAt(pixel, P, viewportSize);

    // Спиральные тапы (та же схема, что в SsaoCommon.hlsl), но радиус шире и вместо счёта
    // заслонения копится перенос света: цвет тапа * косинус приёмника * косинус отправителя.
    const int TapCount = 8;
    const float ScreenRadius = 0.12;   // доля высоты экрана - шире AO-шных 0.045
    const float RangeFraction = 0.45;  // дальность влияния в долях z точки (легаси-режим)
    const float Intensity = 1.1;

    float range = giWorldRange > 0.0 ? giWorldRange : RangeFraction * P.z;
    float radiusPixels = giWorldRange > 0.0
        ? clamp(giWorldRange * viewportSize.y / (2.0 * TanHalfFov * P.z), 4.0, 0.5 * viewportSize.y)
        : ScreenRadius * viewportSize.y;
    float noise = frac(sin(dot(float2(pixel), float2(12.9898, 78.233))) * 43758.5453) * 2.0 * PI;

    float3 bounce = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int t = 0; t < TapCount; t++)
    {
        float angle = noise + t * 2.39996; // золотой угол
        float radius = (t + 0.5) / TapCount * radiusPixels;
        int2 tap = pixel + int2(round(float2(cos(angle), sin(angle)) * radius));
        tap = clamp(tap, int2(0, 0), int2(viewportSize) - 1);

        // Тап на фоне света не переносит.
        float tapRaw = DEPTH_FETCH(tap);
        if (tapRaw < 1e-6)
        {
            continue;
        }

        float3 S = ViewPosAt(tap, viewportSize);
        float3 v = S - P;
        float dist = max(length(v), 1e-5);
        float3 dir = v / dist;

        // Косинус приёмника: свет приходит только из верхней полусферы точки.
        float receiverCos = saturate(dot(N, dir));
        if (receiverCos < 1e-3)
        {
            continue;
        }

        // Косинус отправителя НЕ реконструируется (NormalAt на каждый тап - это +4 чтения
        // глубины, и именно они разгоняли пасс до срыва кадра на больших вьюпортах: D3D12
        // "Timeout elapsed while waiting for the frame waitable object"). Экранные сендеры и
        // так обращены к камере, а копланарный кейс (плоский пол сам себя не подсвечивает)
        // уже отсекает receiverCos - dir лежит в плоскости точки и dot(N, dir) = 0.
        float falloff = saturate(1.0 - dist / range);
        float3 tapColor = _SceneTex.Load(int3(tap, 0)).rgb;
        bounce += tapColor * (receiverCos * falloff * falloff);
    }

    output.color = float4(bounce / TapCount * Intensity, 1.0);
    return output;
}
