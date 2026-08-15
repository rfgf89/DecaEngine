// Общее тело SSAO-пасса превью (см. SsaoPS.hlsl / SsaoMsaaPS.hlsl - обёртки определяют макрос
// DEPTH_FETCH под одиночный или мультисемпловый депт). Экранное затемнение по глубине: даёт
// контактное затемнение между РАЗНЫМИ объектами (фигура на доске), которого запечённый AO из
// glTF дать не может. Полноэкранный треугольник (SkyBackgroundVS), выход - grayscale AO.
//
// Реконструкция позиции: проекция превью - infinite reversed-Z (см.
// RenderingComponents.MakePerspectiveReversedZ: z_clip = near, w = z_view), поэтому
// z_view = near / depth. FOV фиксирован (ModelViewportEnvironment.CameraFovDegrees = 45).
#include "Instancing.hlsl"

cbuffer View
{
    ViewData viewData;
}

// Мировой радиус влияния AO: доля габаритного радиуса модели, пушится после её кадрирования
// (см. ModelPreviewViewport.FrameAll -> SsaoPassResources.SetWorldRange). С ним контактная тень
// не схлопывается при приближении камеры - в легаси-режиме (0, никто не пушил: probe без модели,
// сторонние потребители) радиус живёт в долях экрана, а falloff в долях глубины точки, и при
// зуме нависающая геометрия (корона ферзя и т.п.) выпадала из радиуса поиска.
// Паддинг тремя скалярами, НЕ float3: float3 по смещению 4 нарушает 16-байтное выравнивание
// std140/SPIR-V - легализация шейдера на Vulkan падала ("Failed to legalize SPIR-V shader"),
// и AO-пасс работал как undefined behavior. Зеркалит AoConstantsData (SsaoPass.cs).
cbuffer AoConstants
{
    float aoWorldRange;
    // Живые ручки окна Graphics (см. SsaoPassResources.SetStrength): множитель интенсивности и
    // нижний предел видимости. 0 / отрицательное = дефолты ниже (кбуфер вне превью нулевой).
    float aoPower;
    float aoFloor;
    float aoConstantsPad2;
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

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 viewportSize = viewData.viewport.zw;
    int2 pixel = int2(input.pos.xy);

    // Фон (reversed-Z очищается нулём) не затеняется.
    float centerRaw = DEPTH_FETCH(pixel);
    if (centerRaw < 1e-6)
    {
        output.color = float4(1.0, 1.0, 1.0, 1.0);
        return output;
    }

    float3 P = ViewPosAt(pixel, viewportSize);

    // Нормаль из соседних глубин; из пар +/-1 берётся меньшая разница, чтобы не ловить обрывы
    // на силуэтах (классический трюк depth-normal реконструкции).
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

    // Спиральные тапы в экранном радиусе (масштаб-инвариантно: радиус в долях экрана, а
    // falloff - в долях глубины точки, так что модели любого размера затеняются одинаково).
    const int TapCount = 10;
    const float ScreenRadius = 0.045;   // доля высоты экрана
    const float RangeFraction = 0.18;   // дальность влияния в долях z точки
    const float Bias = 0.08;
    const float Intensity = 1.15;

    // Мировой режим: falloff идёт до aoWorldRange, а радиус спирали - его проекция в пиксели на
    // глубине точки (клэмп сверху, чтобы экстремальный зум не разгонял шаг тапов до всего
    // экрана, снизу - чтобы вдали спираль не вырождалась). Легаси (aoWorldRange = 0) - как раньше.
    float range = aoWorldRange > 0.0 ? aoWorldRange : RangeFraction * P.z;
    float radiusPixels = aoWorldRange > 0.0
        ? clamp(aoWorldRange * viewportSize.y / (2.0 * TanHalfFov * P.z), 2.0, 0.25 * viewportSize.y)
        : ScreenRadius * viewportSize.y;
    float noise = frac(sin(dot(float2(pixel), float2(12.9898, 78.233))) * 43758.5453) * 2.0 * PI;

    float occlusion = 0.0;
    [unroll]
    for (int t = 0; t < TapCount; t++)
    {
        float angle = noise + t * 2.39996; // золотой угол
        float radius = (t + 0.5) / TapCount * radiusPixels;
        int2 tap = pixel + int2(round(float2(cos(angle), sin(angle)) * radius));

        float3 S = ViewPosAt(tap, viewportSize);
        float3 v = S - P;
        float dist = max(length(v), 1e-5);

        float falloff = saturate(1.0 - dist / range);
        occlusion += saturate(dot(N, v / dist) - Bias) * falloff;
    }

    float ao = saturate(1.0 - occlusion / TapCount * (aoPower > 0.01 ? aoPower * Intensity : Intensity));
    ao = max(ao, aoFloor >= 0.0 ? aoFloor : 0.0);
    output.color = float4(ao.xxx, 1.0);
    return output;
}
