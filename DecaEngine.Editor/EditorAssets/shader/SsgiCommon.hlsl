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

// Ручки SSGI - живые (окно Graphics -> SsgiPassResources.SetParams/SetWorldRange). Паддинг
// скалярами, НЕ float3: float3 по смещению 4 нарушает 16-байтное выравнивание std140/SPIR-V
// (см. историю в SsaoCommon.hlsl). Зеркалит GiConstantsData (SsgiPass.cs).
cbuffer GiConstants
{
    // Мировой радиус сбора bounce. 0 - легаси-режим: радиус в долях экрана, falloff в долях
    // глубины точки, как у SSAO (пока модель не кадрирована и ручка не пушилась).
    float giWorldRange;
    // Множитель итогового bounce.
    float giIntensity;
    // Число тапов на пиксель (кламп 4..GiMaxTaps): главный рычаг шум/цена.
    float giSampleCount;
    // Потолок ЯРКОСТИ одного тапа (firefly clamp). В HDR-конвейере солнечное пятно рядом с
    // затенённой стеной даёт тап в десятки единиц - один такой тап из восьми и превращал пасс в
    // цветной снег. <= 0 - без ограничения.
    float giMaxLuminance;
    // Насыщенность переносимого цвета: 1 - цвет отправителя как есть, 0 - серый отскок.
    float giSaturation;
    float giConstantsPad0;
    float giConstantsPad1;
    float giConstantsPad2;
}

static const float PI = 3.14159265359;
static const float TanHalfFov = 0.41421356; // tan(45deg / 2)
static const float NearPlane = 0.05;        // CameraData near в ModelViewportEnvironment

// Потолок цикла тапов: динамический счётчик из кбуфера развернуть нельзя, а неограниченный
// [loop] компилятор разворачивать отказывается - фиксируем верх и выходим по giSampleCount.
static const int GiMaxTaps = 32;

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

// Interleaved gradient noise (Jimenez): в отличие от sin-хеша даёт РАЗНЫЙ поворот спирали у
// каждого из соседей в окне билатерального размытия композита, поэтому те усредняют
// взаимодополняющие направления, а не случайные одинаковые - при том же числе тапов заметно
// меньше остаточного шума.
float GiDitherNoise(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
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

    // Сбор идёт по НАПРАВЛЕНИЯМ полусферы точки, а не по экранному диску вокруг пикселя.
    // Экранный диск (как в SsaoCommon.hlsl) для GI не годится: у пикселя пола все его соседи -
    // тот же пол, dir лежит в плоскости, dot(N, dir) = 0, и тап отбрасывается - пол не получал
    // ничего вообще, а деление на полное число тапов гасило и то, что успевало собраться.
    // Здесь каждый тап - косинусно-взвешенное направление d полусферы вокруг N; вдоль него
    // берётся точка на случайном расстоянии, проецируется в экран, и глубина в этом пикселе
    // говорит, есть ли по этому направлению поверхность (одношаговый screen-space рей-марш).
    // Оценка получается самонормируемой: направление, где ничего нет, честно даёт ноль.
    const float RangeFraction = 0.45; // дальность в долях z точки (легаси-режим, радиус не пушен)

    int tapCount = (int)clamp(giSampleCount, 4.0, (float)GiMaxTaps);
    float intensity = giIntensity > 0.0 ? giIntensity : 1.0;
    float range = giWorldRange > 0.0 ? giWorldRange : RangeFraction * P.z;

    // Толщина блокера в долях радиуса: поверхность, оказавшаяся НАМНОГО ближе точки сэмпла,
    // это не сосед по полусфере, а посторонняя геометрия переднего плана (колонна перед стеной) -
    // без отсечки она обводила бы силуэты ореолом своего цвета.
    const float ThicknessFraction = 0.6;

    // Тангенциальный базис полусферы: любой вектор, не коллинеарный N.
    float3 up = abs(N.z) < 0.9 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
    float3 tangent = normalize(cross(up, N));
    float3 bitangent = cross(N, tangent);

    float dither = GiDitherNoise(float2(pixel));
    float aspect = viewportSize.x / max(viewportSize.y, 1.0);

    float3 bounce = float3(0.0, 0.0, 0.0);
    [loop]
    for (int t = 0; t < GiMaxTaps; t++)
    {
        if (t >= tapCount)
        {
            break;
        }

        // Косинусно-взвешенная полусфера (проекция диска Фибоначчи): косинус приёмника и 1/PI
        // при таком распределении сокращаются - взвешивать тап отдельно уже не нужно.
        float u1 = (t + dither) / tapCount;
        float discR = sqrt(saturate(u1));
        float discA = (t + dither) * 2.39996; // золотой угол
        float3 d = tangent * (discR * cos(discA))
                 + bitangent * (discR * sin(discA))
                 + N * sqrt(saturate(1.0 - u1));

        // Расстояние шага - своя стратификация (не та же u1, иначе направление и дальность
        // жёстко связаны и выборка ложится спиралью в пространстве).
        float u2 = frac(dither + t * 0.7548776662);
        float3 S = P + d * (range * max(u2, 0.05));
        if (S.z <= NearPlane)
        {
            continue; // точка сэмпла ушла за камеру - о ней экран ничего не знает
        }

        // Проекция точки сэмпла в экран (тот же fixed-FOV, что в ViewPosAt, только наоборот).
        float2 sUv = float2(
            S.x / (S.z * TanHalfFov * aspect) * 0.5 + 0.5,
            0.5 - S.y / (S.z * TanHalfFov) * 0.5);
        if (any(sUv < 0.0) || any(sUv > 1.0))
        {
            continue; // за кадром - главное ограничение экранного GI, света оттуда не знаем
        }

        int2 tap = clamp(int2(sUv * viewportSize), int2(0, 0), int2(viewportSize) - 1);
        float tapRaw = DEPTH_FETCH(tap);
        if (tapRaw < 1e-6)
        {
            continue; // по этому направлению фон (небо) - отскока нет, вклад ноль
        }

        float3 B = ViewPosAt(tap, viewportSize);
        if (B.z > S.z)
        {
            continue; // поверхность ДАЛЬШЕ точки сэмпла - направление свободно, вклада нет
        }
        if (S.z - B.z > ThicknessFraction * range)
        {
            continue; // блокер слишком «толстый»/чужой - см. ThicknessFraction
        }

        float3 v = B - P;
        float dist = length(v);
        if (dist > range || dist < 1e-4)
        {
            continue;
        }
        if (dot(N, v) <= 0.0)
        {
            continue; // блокер под плоскостью точки - светить на неё он не может
        }

        // Косинус ОТПРАВИТЕЛЯ не реконструируется (NormalAt на каждый тап - это +4 чтения
        // глубины, и именно они разгоняли пасс до срыва кадра на больших вьюпортах: D3D12
        // "Timeout elapsed while waiting for the frame waitable object"). Экранные сендеры и так
        // обращены к камере.
        float falloff = saturate(1.0 - dist / range);
        float3 tapColor = _SceneTex.Load(int3(tap, 0)).rgb;

        // Firefly clamp ДО взвешивания: ограничиваем яркость, сохраняя оттенок (иначе цветной
        // пересвет ушёл бы в белый).
        if (giMaxLuminance > 0.0)
        {
            float lum = dot(tapColor, float3(0.2126, 0.7152, 0.0722));
            tapColor *= min(1.0, giMaxLuminance / max(lum, 1e-4));
        }

        // Насыщенность отскока: яркие цветные ткани иначе светят как неоновые лампы.
        float tapLum = dot(tapColor, float3(0.2126, 0.7152, 0.0722));
        tapColor = lerp(tapLum.xxx, tapColor, saturate(giSaturation));

        bounce += tapColor * falloff;
    }

    output.color = float4(bounce / tapCount * intensity, 1.0);
    return output;
}
