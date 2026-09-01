// Резолв стохастических отражений (см. SsrPass.cs) - две ступени, архитектура Stachowiak
// (референс: Xerxes1138/StochasticScreenSpaceReflection):
//
// 1. ПЕРЕИСПОЛЬЗОВАНИЕ ЛУЧЕЙ (ratio estimator): соседние пиксели стреляли СВОИ GGX-лучи, и их
//    хиты - валидные сэмплы нашего интеграла. Хит соседа трактуется как точечный «источник»:
//    вклад = цвет * BRDF(V, L, N_наш) / PDF_соседа. Веса сами дают физику лоба - у зеркала
//    чужие направления весят ~0 (резкость), у шероховатого лоб широкий и соседи весят честно
//    (гладкий glossy без ручного радиуса размытия). BRDF bias трейса (лучи поджаты к зеркалу)
//    компенсируется этим же весом.
// 2. ТЕМПОРАЛЬНАЯ аккумуляция: история репроецируется по буферу векторов движения и зажимается
//    в цветовой AABB окрестности текущего кадра (Playdead neighborhood clamp); вес истории
//    дополнительно гасится скоростью пикселя - на движении камеры отражение не мажется.
#include "SsrCommon.hlsl"

Texture2D<float> _DepthTex;
Texture2D _TraceTex;    // цвет хита + confidence (RT0 трейса)
Texture2D _RayHitTex;   // hit-буфер: uv/направление + pdf + маска (RT1 трейса)
Texture2D _NormalRoughTex;
Texture2D _HistoryTex;
SamplerState _HistoryTex_sampler;
Texture2D<float2> _MotionTex;

struct PSOutput
{
    float4 color : SV_TARGET;
};

// Спираль соседей переиспользования (первый - сам пиксель); ssrRaysPerPixel выбирает, сколько
// пар из неё реально берётся (1..4 -> 2..8 тапов).
static const int SsrMaxReuseTaps = 8;
static const float2 SsrReuseOffsets[SsrMaxReuseTaps] =
{
    float2(0.0, 0.0), float2(2.0, -2.0), float2(-2.0, -2.0), float2(0.0, 2.0),
    float2(-2.0, 0.0), float2(2.0, 2.0), float2(0.0, -3.0), float2(3.0, 1.0)
};

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 viewportSize = viewData.viewport.zw;
    int2 pixel = int2(input.pos.xy);
    float2 uv = (pixel + 0.5) / viewportSize;

    float4 center = _TraceTex.Load(int3(pixel, 0));

    // Фон истории не имеет и не копит.
    float centerRaw = _DepthTex.Load(int3(pixel, 0));
    if (centerRaw < 1e-6)
    {
        output.color = center;
        return output;
    }

    float4 centerGbuffer = _NormalRoughTex.Load(int3(pixel, 0));
    float roughness = centerGbuffer.a;
    float3 nWorld = centerGbuffer.xyz;

    float4 current = center;
    if (dot(nWorld, nWorld) > 0.5)
    {
        float3 P = SsrViewPos(pixel, centerRaw, viewportSize);
        float3 N = SsrWorldDirToView(normalize(nWorld));
        float3 V = -normalize(P);

        // Поворот спирали - IGN с покадровой фазой: соседние пиксели и соседние кадры берут
        // взаимодополняющие наборы соседей.
        float ang = SsrNoise(float2(pixel), ssrFrameIndex * 0.618) * 2.0 * SsrPI;
        float2x2 rot = float2x2(cos(ang), sin(ang), -sin(ang), cos(ang));

        // АНИЗОТРОПНОЕ ядро (RTG гл.19, "BRDF-based reflection filter kernel"): глянцевое
        // отражение растягивается по экрану ВДОЛЬ проекции нормали (столбы света на мокром
        // полу), и там же лежат полезные сэмплы соседей - круглый диск половину тапов тратил
        // поперёк лоба, где вес BRDF ~ 0. Ось - экранная проекция нормали (view.y -> экранный
        // -y), вытяжка растёт к скользящим углам (доля нормали в плоскости экрана / NdotV),
        // общий масштаб - с шероховатостью (у зеркала диск сжимается: чужие направления - уже
        // другой блик).
        float NdotV = saturate(dot(N, V));
        float2 nScreen = float2(N.x, -N.y);
        float nScreenLen = length(nScreen);
        float2 majorDir = nScreenLen > 1e-3 ? nScreen / nScreenLen : float2(1.0, 0.0);
        float2 minorDir = float2(-majorDir.y, majorDir.x);
        float stretch = min(1.0 + 3.0 * nScreenLen * nScreenLen / max(NdotV, 0.25), 4.0);
        float kernelScale = lerp(0.5, 1.5, smoothstep(0.05, 0.5, roughness));

        // Вытяжка выведена для ПЛОСКОСТЕЙ (пол, доска): на сильно кривой поверхности ось
        // растяжения вращается от пикселя к пикселю, и эллипс мешает сэмплы разных направлений
        // лоба - на сферах вблизи выходили «завихрения». Кривизна оценивается по соседним
        // нормалям G-buffer-а; кривым поверхностям эллипс сжимается обратно к кругу.
        float3 nRight = _NormalRoughTex.Load(int3(clamp(pixel + int2(2, 0), int2(0, 0), int2(viewportSize) - 1), 0)).xyz;
        float3 nDown = _NormalRoughTex.Load(int3(clamp(pixel + int2(0, 2), int2(0, 0), int2(viewportSize) - 1), 0)).xyz;
        float curvature = length(nRight - nWorld) + length(nDown - nWorld);
        float flatness = saturate(1.0 - curvature * 8.0);
        stretch = 1.0 + (stretch - 1.0) * flatness;

        int taps = clamp((int)ssrRaysPerPixel, 1, 4) * 2;

        float3 colorSum = float3(0.0, 0.0, 0.0);
        float confSum = 0.0;
        float weightSum = 0.0;

        [loop]
        for (int t = 0; t < SsrMaxReuseTaps; t++)
        {
            if (t >= taps)
            {
                break;
            }

            // Спиральный офсет -> случайный поворот -> эллипс лоба (вытяжка вдоль majorDir).
            float2 spun = mul(rot, SsrReuseOffsets[t]) * kernelScale;
            float2 elliptic = majorDir * (spun.x * stretch) + minorDir * spun.y;
            int2 tap = clamp(pixel + int2(round(elliptic)),
                int2(0, 0), int2(viewportSize) - 1);

            float4 rayHit = _RayHitTex.Load(int3(tap, 0));
            if (rayHit.w < 0.25)
            {
                continue; // сосед промахнулся - переиспользовать нечего
            }

            // Направление чужого луча ИЗ НАШЕЙ точки: экранный хит - через view-позицию точки
            // пересечения (честный параллакс), RT-хит - октаэдрально упакованное направление.
            float3 L;
            if (rayHit.w > 0.75)
            {
                float2 hitUv = rayHit.xy;
                int2 hitPixel = clamp(int2(hitUv * viewportSize), int2(0, 0), int2(viewportSize) - 1);
                float hitRaw = _DepthTex.Load(int3(hitPixel, 0));
                if (hitRaw < 1e-6)
                {
                    continue;
                }

                float3 hitPos = SsrViewPos(hitPixel, hitRaw, viewportSize);
                float3 toHit = hitPos - P;
                if (dot(toHit, toHit) < 1e-6)
                {
                    continue;
                }
                L = normalize(toHit);
            }
            else
            {
                L = SsrOctDecode(rayHit.xy);
            }

            // Вес ratio estimator-а: BRDF нашего пикселя по направлению соседа / pdf соседа.
            // У ЗЕРКАЛЬНОГО соседа rayHit.z несёт длину луча (репроекция виртуального образа,
            // см. ниже), его pdf детерминирован - считается аналитически по его шероховатости.
            float tapRough = _NormalRoughTex.Load(int3(tap, 0)).a;
            float tapPdf = tapRough < SsrMirrorRoughness ? SsrMirrorPdf(tapRough) : rayHit.z;
            float weight = SsrBrdfWeight(V, L, N, roughness) / max(tapPdf, 1e-5);

            float4 s = _TraceTex.Load(int3(tap, 0));

            // Против «искр»: яркий тап приглушается при усреднении (/(1+lum)) и разжимается
            // обратно после - firefly-фильтр референса.
            float3 c = s.rgb / (1.0 + dot(s.rgb, float3(0.2126, 0.7152, 0.0722)));

            colorSum += c * weight;
            confSum += s.a * weight;
            weightSum += weight;
        }

        if (weightSum > 1e-5)
        {
            float3 c = colorSum / weightSum;
            c /= max(1.0 - dot(c, float3(0.2126, 0.7152, 0.0722)), 1e-3);
            current = float4(c, saturate(confSum / weightSum));
        }
    }

    // Цветовая AABB окрестности 3x3 сырого трейса - границы правдоподобной истории.
    float4 mn = center;
    float4 mx = center;
    [unroll]
    for (int dy = -1; dy <= 1; dy++)
    {
        [unroll]
        for (int dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0)
            {
                continue;
            }

            int2 tap = clamp(pixel + int2(dx, dy), int2(0, 0), int2(viewportSize) - 1);
            float4 s = _TraceTex.Load(int3(tap, 0));
            mn = min(mn, s);
            mx = max(mx, s);
        }
    }

    // Резолвнутое значение может законно выходить за AABB сырого трейса - расширяем границы им
    // самим, иначе кламп истории душил бы то, что резолв только что восстановил.
    mn = min(mn, current);
    mx = max(mx, current);

    float2 motion = _MotionTex.Load(int3(pixel, 0));
    float2 prevUv = uv + motion;

    // РЕПРОЕКЦИЯ ВИРТУАЛЬНОГО ОБРАЗА у зеркал (RTG гл.32, Reflection Motion Vectors):
    // отражение при движении камеры едет не как поверхность зеркала, а как точка ЗА ним -
    // виртуальный образ хита на глубине |P| + длина луча вдоль луча взгляда (плоское
    // приближение; кривизна по тонкой линзе - возможное продолжение). Прошлое положение
    // образа даёт viewProj прошлого кадра. Длина: экранный хит - из глубины по его uv,
    // RT-хит зеркала - из rayHit.z (трейс кладёт туда длину, pdf зеркала аналитический).
    float virtualBlend = 1.0 - smoothstep(SsrMirrorRoughness, 0.3, roughness);
    float historyMotionDamp = 8.0;
    if (virtualBlend > 0.0 && dot(nWorld, nWorld) > 0.5)
    {
        float4 centerHit = _RayHitTex.Load(int3(pixel, 0));
        float3 Pv = SsrViewPos(pixel, centerRaw, viewportSize);
        float hitLen = -1.0;
        if (centerHit.w > 0.75)
        {
            int2 hp = clamp(int2(centerHit.xy * viewportSize), int2(0, 0), int2(viewportSize) - 1);
            float hraw = _DepthTex.Load(int3(hp, 0));
            if (hraw >= 1e-6)
            {
                hitLen = length(SsrViewPos(hp, hraw, viewportSize) - Pv);
            }
        }
        else if (centerHit.w > 0.25 && roughness < SsrMirrorRoughness)
        {
            hitLen = centerHit.z;
        }

        if (hitLen > 0.0)
        {
            float3 surfaceWorld = viewData.CameraWorldPos + mul(Pv, transpose((float3x3)viewData.view));
            float3 toSurface = surfaceWorld - viewData.CameraWorldPos;
            float surfaceDist = max(length(toSurface), 1e-4);
            float3 virtualWorld = viewData.CameraWorldPos
                + toSurface / surfaceDist * (surfaceDist + hitLen);
            float4 prevClip = mul(float4(virtualWorld, 1.0), ssrPrevViewProj);
            if (prevClip.w > 1e-4)
            {
                float2 prevVirtualUv = float2(prevClip.x / prevClip.w * 0.5 + 0.5,
                    0.5 - prevClip.y / prevClip.w * 0.5);
                prevUv = lerp(prevUv, prevVirtualUv, virtualBlend);

                // Репроекция теперь честная - скоростное гашение истории зеркалу смягчается
                // (AABB-кламп прикрывает промахи репроекции).
                historyMotionDamp = lerp(8.0, 2.0, virtualBlend);
            }
        }
    }

    // Вес истории гасится скоростью (референс: response * (1 - |velocity| * 8)) - на движении
    // камеры протухшая история не мажет, в статике аккумуляция полная. Зеркалу с виртуальной
    // репроекцией история снова полезна (раньше гасилась до 0.45: репроекция по движению
    // ПОВЕРХНОСТИ мазала близкие зеркала шлейфом при орбите - теперь образ репроецируется сам).
    float historyByRoughness = lerp(0.8, 1.0, smoothstep(SsrMirrorRoughness, 0.3, roughness));
    float weight = ssrHistoryWeight * historyByRoughness
        * saturate(1.0 - length(motion) * historyMotionDamp);
    if (any(prevUv < 0.0) || any(prevUv > 1.0))
    {
        weight = 0.0; // пиксель въехал из-за кадра - истории нет
    }

    float4 history = _HistoryTex.SampleLevel(_HistoryTex_sampler, prevUv, 0.0);
    history = clamp(history, mn, mx);

    output.color = lerp(current, history, weight);
    return output;
}
