// Общее тело GTAO-пасса превью (см. GtaoPS.hlsl / GtaoMsaaPS.hlsl - обёртки определяют макрос
// DEPTH_FETCH под одиночный или мультисемпловый депт). Ground Truth Ambient Occlusion
// (Jimenez et al. 2016): вместо счёта заслоняющих тапов, как в SsaoCommon.hlsl, по каждому из
// нескольких экранных срезов (slice) ищутся углы горизонта и аналитически интегрируется
// косинус-взвешенная видимость полусферы - даёт более "физичное" затемнение без характерного
// для SSAO серого налёта на плоских поверхностях. Альтернатива SsaoCommon.hlsl, выбирается
// настройкой AO technique (см. AmbientOcclusionMode / SsaoPassResources).
//
// Реконструкция позиции/нормали и масштаб-инвариантность (радиус в долях экрана, falloff в
// долях глубины точки) - те же, что в SsaoCommon.hlsl: infinite reversed-Z (z_view = near /
// depth), фиксированный FOV 45 (см. ModelViewportEnvironment).
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
    float aoConstantsPad0;
    float aoConstantsPad1;
    float aoConstantsPad2;
}

static const float PI = 3.14159265359;
static const float HalfPI = 1.57079632679;
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
    // на силуэтах (тот же трюк, что в SsaoCommon.hlsl).
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

    // Вектор к камере (камера в начале координат вью-спейса, +z вглубь экрана).
    float3 V = -P / length(P);

    const int SliceCount = 3;
    const int StepsPerSide = 4;
    const float ScreenRadius = 0.06;    // доля высоты экрана
    const float RangeFraction = 0.22;   // дальность влияния в долях z точки
    const float Power = 1.5;            // контраст итоговой видимости (аналог Intensity SSAO)
    const float AoFloor = 0.12;         // нижний предел видимости: экранный AO не должен давать чёрный

    // Мировой режим: falloff идёт до aoWorldRange, а радиус поиска - его проекция в пиксели на
    // глубине точки (клэмп сверху, чтобы экстремальный зум не разгонял шаг сэмплов до всего
    // экрана, снизу - чтобы вдали поиск не вырождался). Легаси (aoWorldRange = 0) - как раньше.
    float range = aoWorldRange > 0.0 ? aoWorldRange : RangeFraction * P.z;
    float radiusPixels = aoWorldRange > 0.0
        ? clamp(aoWorldRange * viewportSize.y / (2.0 * TanHalfFov * P.z), 2.0, 0.25 * viewportSize.y)
        : ScreenRadius * viewportSize.y;
    float noiseSlice = frac(sin(dot(float2(pixel), float2(12.9898, 78.233))) * 43758.5453);
    float noiseStep = frac(sin(dot(float2(pixel), float2(39.3467, 11.1357))) * 24634.6345);

    float visibility = 0.0;
    [unroll]
    for (int s = 0; s < SliceCount; s++)
    {
        // Срез: плоскость, натянутая на V и экранное направление omega. Пиксельный y растёт
        // вниз, вью-спейсный - вверх, поэтому шаг по экрану идёт с перевёрнутым y.
        float phi = (s + noiseSlice) * PI / SliceCount;
        float2 omega = float2(cos(phi), sin(phi));
        float2 pixelDir = float2(omega.x, -omega.y);

        // Проекция нормали на плоскость среза и её угол n относительно V - центр дуги, которую
        // могут закрыть горизонты (стандартная схема GTAO/XeGTAO).
        float3 sliceDir = float3(omega, 0.0);
        float3 orthoDir = sliceDir - V * dot(sliceDir, V);
        float3 axis = normalize(cross(orthoDir, V));
        float3 projN = N - axis * dot(N, axis);
        float projLen = length(projN);

        float signN = sign(dot(orthoDir, projN));
        float cosN = saturate(dot(projN, V) / max(projLen, 1e-6));
        float n = signN * acos(cosN);

        // Поиск горизонтов по обе стороны среза: максимум косинуса угла между V и направлением
        // на сэмпл. Далёкие сэмплы через falloff стягиваются к -1 (не поднимают горизонт) -
        // это и есть ограничение радиуса влияния.
        float2 horizonCos = float2(-1.0, -1.0);
        [unroll]
        for (int j = 0; j < StepsPerSide; j++)
        {
            // Квадратичное распределение шагов: плотнее у точки, где контактное затемнение
            // важнее всего.
            float stepNorm = (j + noiseStep) / StepsPerSide;
            float2 offset = pixelDir * max(stepNorm * stepNorm * radiusPixels, 1.0);

            [unroll]
            for (int side = 0; side < 2; side++)
            {
                float dirSign = side == 0 ? -1.0 : 1.0;
                int2 tap = pixel + int2(round(dirSign * offset));

                float3 S = ViewPosAt(tap, viewportSize);
                float3 delta = S - P;
                float dist = max(length(delta), 1e-5);

                float sampleCos = dot(delta / dist, V);
                float falloff = saturate(1.0 - dist / range);
                horizonCos[side] = max(horizonCos[side], lerp(-1.0, sampleCos, falloff));
            }
        }

        // Углы горизонта, зажатые в полусферу вокруг проекции нормали, и аналитический
        // интеграл дуги видимости (формула из GTAO: a(h) = (cosN + 2h*sin(n) - cos(2h-n)) / 4).
        float h0 = n + max(-acos(clamp(horizonCos.x, -1.0, 1.0)) - n, -HalfPI);
        float h1 = n + min(acos(clamp(horizonCos.y, -1.0, 1.0)) - n, HalfPI);

        float arc0 = (cosN + 2.0 * h0 * sin(n) - cos(2.0 * h0 - n)) * 0.25;
        float arc1 = (cosN + 2.0 * h1 * sin(n) - cos(2.0 * h1 - n)) * 0.25;
        visibility += projLen * (arc0 + arc1);
    }

    float ao = pow(saturate(visibility / SliceCount), Power);

    // На скользящих углах (N почти перпендикулярна V) реконструкция нормали и горизонтов из
    // глубины ненадёжна: сэмплы ложатся вдоль склона самой поверхности и поднимают горизонты
    // до плоскости точки, "честная" видимость схлопывается в ноль - пол под острым углом
    // чернел (см. DragonAttenuation). Затухаем AO к 1 по мере ухода в grazing, плюс общий
    // нижний предел: экранный AO - косвенная оценка, полностью гасить свет он не вправе.
    float NdotV = saturate(dot(N, V));
    ao = lerp(1.0, ao, smoothstep(0.02, 0.25, NdotV));
    ao = max(ao, AoFloor);
    output.color = float4(ao.xxx, 1.0);
    return output;
}
