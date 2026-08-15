// Главный пасс GTAO (Ground Truth Ambient Occlusion, Jimenez et al. 2016) в редакции XeGTAO
// (Intel, Strugar/Mccalla). Вместо счёта заслоняющих тапов, как в SsaoCommon.hlsl, по каждому из
// нескольких экранных срезов (slice) ищутся углы горизонта и аналитически интегрируется
// косинус-взвешенная видимость полусферы - отсюда более «физичное» затемнение без характерного
// для SSAO серого налёта на плоских поверхностях. Альтернатива SsaoCommon.hlsl, выбирается
// настройкой AO technique (см. AmbientOcclusionMode / SsaoPassResources).
//
// Пасс - ВТОРОЕ звено конвейера из трёх, и сам по себе не рисуется:
//   1. GtaoDepthPrefilterCommon.hlsl + GtaoDepthMipPS.hlsl - линейная глубина и её мип-цепочка;
//   2. этот пасс - оценка видимости плюс «рёбра» для денойзера, обе величины в один RGBA8-таргет;
//   3. GtaoDenoisePS.hlsl - краесохраняющая фильтрация результата.
// Глубину пасс читает ТОЛЬКО из цепочки (_AoDepth0.._AoDepth4), а не из депт-буфера: выбор мипа
// по дальности сэмпла - штатная часть алгоритма, а не оптимизация, см. GtaoDepthMipPS.hlsl. Как
// следствие MSAA этого пасса вообще не касается - мультисемпловый депт видит только префильтр.
//
// Реконструкция позиции и масштаб-инвариантность - те же, что в SsaoCommon.hlsl: infinite
// reversed-Z, фиксированный FOV 45 (см. ModelViewportEnvironment).
#include "Instancing.hlsl"
#include "GtaoShared.hlsl"

// Цепочка линейных вью-спейсных глубин: [0] - полное разрешение, дальше каждый вдвое меньше.
// Отдельными текстурами, а не мипами одной: IRenderTarget движка не умеет рисовать в конкретный
// мип-уровень (см. IGraphicsApi.CreateRenderTarget), поэтому уровень выбирается ветвлением, а не
// параметром SampleLevel. Сэмплеры ТОЧЕЧНЫЕ: билинейная фильтрация смешала бы соседние глубины
// внутри уровня и наврала бы по наклону поверхности.
Texture2D _AoDepth0;
SamplerState _AoDepth0_sampler;
Texture2D _AoDepth1;
SamplerState _AoDepth1_sampler;
Texture2D _AoDepth2;
SamplerState _AoDepth2_sampler;
Texture2D _AoDepth3;
SamplerState _AoDepth3_sampler;
Texture2D _AoDepth4;
SamplerState _AoDepth4_sampler;

cbuffer View
{
    ViewData viewData;
}

// Мировой радиус влияния AO: доля габаритного радиуса модели, пушится после её кадрирования
// (см. ModelPreviewViewport.FrameAll -> SsaoPassResources.SetWorldRange). С ним контактная тень
// не схлопывается при приближении камеры; 0 = легаси (радиус в долях экрана, см.
// GtaoEffectRadius). Плюс живые ручки окна Graphics: контраст видимости и её нижний предел.
//
// Паддинг тремя скалярами, НЕ float3: float3 по смещению 4 нарушает 16-байтное выравнивание
// std140/SPIR-V - легализация шейдера на Vulkan падала ("Failed to legalize SPIR-V shader"),
// и AO-пасс работал как undefined behavior. Зеркалит AoConstantsData (SsaoPass.cs).
cbuffer AoConstants
{
    float aoWorldRange;
    float aoPower;
    float aoFloor;
    float aoConstantsPad2;
}

// Качество: срезов больше канонических трёх (XeGTAO High), потому что здесь нет TAA - временного
// накопления, за счёт которого XeGTAO обходится тремя. Шагов на срез - три, как в High: их число
// влияет в первую очередь на дальние окклюдеры, которые всё равно читаются из грубых мипов.
static const int SliceCount = 5;
static const int StepsPerSlice = 3;

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

/// Индекс пикселя на кривой Гильберта 64x64. Основа пространственного шума: R2-последовательность,
// прогнанная вдоль кривой Гильберта, раскладывает направления срезов так, что СОСЕДНИЕ пиксели
// получают максимально непохожие ориентации, но любая небольшая окрестность покрывает весь набор
// равномерно. Именно на это рассчитан краесохраняющий денойзер (GtaoDenoisePS.hlsl): он усредняет
// 3x3, и белый хеш-шум, в отличие от этого, оставляет в такой окрестности случайные сгустки.
uint GtaoHilbertIndex(uint2 pos)
{
    // Кривая периодична с шагом 64, а разворот ниже (63 - pos) верен только внутри одного периода.
    pos &= 63;

    uint index = 0;
    [unroll]
    for (uint level = 32; level > 0; level /= 2)
    {
        uint regionX = (pos.x & level) > 0 ? 1 : 0;
        uint regionY = (pos.y & level) > 0 ? 1 : 0;
        index += level * level * ((3 * regionX) ^ regionY);
        if (regionY == 0)
        {
            if (regionX == 1)
            {
                pos.x = 63 - pos.x;
                pos.y = 63 - pos.y;
            }

            uint temp = pos.x;
            pos.x = pos.y;
            pos.y = temp;
        }
    }

    return index;
}

/// Пара шумов на пиксель: x крутит ориентацию срезов, y - положение шагов вдоль среза.
float2 GtaoSpatialNoise(uint2 pixel)
{
    uint index = GtaoHilbertIndex(pixel);

    // R2 - двумерная низкодискрепансная последовательность (Roberts): сдвиги по золотым сечениям
    // высшего порядка. Она и раскладывает индекс кривой в две «равномерно перемешанные» доли.
    return frac(0.5 + index * float2(0.75487766624669276005, 0.5698402909980532659114));
}

float LoadViewDepth(int2 pixel, float2 viewportSize)
{
    pixel = clamp(pixel, int2(0, 0), int2(viewportSize) - 1);
    return _AoDepth0.Load(int3(pixel, 0)).r;
}

/// Глубина на выбранном уровне цепочки. Уровень - целый: SampleLevel по одной мип-цепочке здесь
// недоступен (уровни живут в разных текстурах), а точечный сэмплер и так отбросил бы дробную часть.
float SampleDepthMip(float2 uv, int mip)
{
    if (mip <= 0)
    {
        return _AoDepth0.SampleLevel(_AoDepth0_sampler, uv, 0).r;
    }

    if (mip == 1)
    {
        return _AoDepth1.SampleLevel(_AoDepth1_sampler, uv, 0).r;
    }

    if (mip == 2)
    {
        return _AoDepth2.SampleLevel(_AoDepth2_sampler, uv, 0).r;
    }

    if (mip == 3)
    {
        return _AoDepth3.SampleLevel(_AoDepth3_sampler, uv, 0).r;
    }

    return _AoDepth4.SampleLevel(_AoDepth4_sampler, uv, 0).r;
}

float3 ViewPosFromUV(float2 uv, float viewZ, float aspect)
{
    float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    return float3(ndc.x * GtaoTanHalfFov * aspect * viewZ, ndc.y * GtaoTanHalfFov * viewZ, viewZ);
}

float3 ViewPosAt(int2 pixel, float2 viewportSize, float aspect)
{
    float z = LoadViewDepth(pixel, viewportSize);
    return ViewPosFromUV((pixel + 0.5) / viewportSize, z, aspect);
}

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 viewportSize = viewData.viewport.zw;
    float2 invViewportSize = 1.0 / viewportSize;
    float aspect = viewportSize.x / max(viewportSize.y, 1.0);
    int2 pixel = int2(input.pos.xy);
    float2 centerUV = (pixel + 0.5) * invViewportSize;

    // Рёбра считаются ВСЕГДА, включая фон: денойзер читает их безусловно, и «дыра» на фоне
    // означала бы нулевые веса у его соседей на силуэте. На фоне глубина упирается в потолок,
    // разницы с соседями относительно неё ничтожны, и все четыре ребра выходят единицами - то
    // есть фон свободно смешивается сам с собой и никуда не течёт.
    float viewspaceZ = LoadViewDepth(pixel, viewportSize);
    float leftZ = LoadViewDepth(pixel + int2(-1, 0), viewportSize);
    float rightZ = LoadViewDepth(pixel + int2(1, 0), viewportSize);
    float topZ = LoadViewDepth(pixel + int2(0, -1), viewportSize);
    float bottomZ = LoadViewDepth(pixel + int2(0, 1), viewportSize);

    float4 edgesLRTB = GtaoCalculateEdges(viewspaceZ, leftZ, rightZ, topZ, bottomZ);
    float packedEdges = GtaoPackEdges(edgesLRTB);

    // Фон не затеняется. Видимость пакуется поделённой на GtaoOcclusionTermScale - ровно как всё
    // остальное, что пишет этот пасс, иначе денойзер домножил бы фон до 1.5.
    if (viewspaceZ >= GtaoMaxViewDepth * 0.99)
    {
        output.color = float4(1.0 / GtaoOcclusionTermScale, packedEdges, 0.0, 1.0);
        return output;
    }

    // Нормаль по схеме XeGTAO (XeGTAO_CalculateNormal): четыре крест-произведения соседей,
    // взвешенные теми же рёбрами. Вариант с одной парой производных по минимальной z-разнице на
    // скользящих поверхностях (уходящий вдаль пол) заметно врал по наклону: горизонт считался
    // относительно неверной касательной плоскости, и пол затенял сам себя.
    float3 center = ViewPosFromUV(centerUV, viewspaceZ, aspect);
    float3 posL = ViewPosAt(pixel + int2(-1, 0), viewportSize, aspect);
    float3 posR = ViewPosAt(pixel + int2(1, 0), viewportSize, aspect);
    float3 posT = ViewPosAt(pixel + int2(0, -1), viewportSize, aspect);
    float3 posB = ViewPosAt(pixel + int2(0, 1), viewportSize, aspect);

    float4 acceptedNormals = saturate(float4(edgesLRTB.x * edgesLRTB.z,
                                             edgesLRTB.z * edgesLRTB.y,
                                             edgesLRTB.y * edgesLRTB.w,
                                             edgesLRTB.w * edgesLRTB.x) + 0.01);

    float3 dirL = normalize(posL - center);
    float3 dirR = normalize(posR - center);
    float3 dirT = normalize(posT - center);
    float3 dirB = normalize(posB - center);

    float3 viewspaceNormal = acceptedNormals.x * cross(dirL, dirT)
                           + acceptedNormals.y * cross(dirT, dirR)
                           + acceptedNormals.z * cross(dirR, dirB)
                           + acceptedNormals.w * cross(dirB, dirL);
    viewspaceNormal = normalize(viewspaceNormal);
    if (viewspaceNormal.z > 0.0)
    {
        viewspaceNormal = -viewspaceNormal;
    }

    // Центральная точка чуть придвигается к камере (XeGTAO: "Move center pixel slightly towards
    // camera to avoid imprecision artifacts due to depth buffer imprecision"). Без этого соседние
    // тапы ТОЙ ЖЕ плоскости из-за квантования глубины оказываются на волосок ВЫШЕ центра,
    // поднимают горизонт над касательной плоскостью - и ровная поверхность затеняет сама себя.
    // Множитель - под half-цепочку глубин (XE_GTAO_FP32_DEPTHS выключен).
    viewspaceZ *= 0.99920;

    float3 pixCenterPos = ViewPosFromUV(centerUV, viewspaceZ, aspect);

    // Вектор к камере: камера в начале координат вью-спейса, +z вглубь экрана.
    float3 viewVec = normalize(-pixCenterPos);

    // Заваливаем нормали, смотрящие ОТ камеры, обратно в видимую полусферу. В XeGTAO эта строка
    // есть, но закомментирована - там нормаль приходит из G-буфера и такого не бывает по
    // построению. У нас она реконструируется из глубины, и на скользящих ракурсах крест-произведения
    // соседей дают вектор, глядящий за поверхность.
    viewspaceNormal = normalize(viewspaceNormal + max(0.0, -dot(viewspaceNormal, viewVec)) * viewVec);

    float NdotV = saturate(dot(viewspaceNormal, viewVec));

    float pixelWorldSize = GtaoPixelWorldSize(viewspaceZ, viewportSize.y);
    float effectRadius = GtaoEffectRadius(viewspaceZ, viewportSize.y, aoWorldRange);
    float falloffRange = max(GtaoFalloffRange * effectRadius, 1e-6);
    float falloffFrom = effectRadius * (1.0 - GtaoFalloffRange);

    // Предвычисленный falloff: вес = 1 до falloffFrom и линейно к нулю на самом радиусе. Схема
    // XeGTAO; прежнее saturate(1 - dist/range) начинало гасить сэмплы сразу от точки, то есть
    // занижало вклад ближних окклюдеров - тех самых, что дают контактное затемнение.
    float falloffMul = -1.0 / falloffRange;
    float falloffAdd = falloffFrom / falloffRange + 1.0;

    float screenspaceRadius = max(effectRadius / pixelWorldSize, 1e-3);

    // Затухание эффекта на крошечных экранных радиусах (дальний план, сильное отдаление): там
    // сэмплы всё равно попадают в те же один-два текселя, и «оценка» вырождается в шум - лучше
    // честно отдать половину видимости, чем мерцающий мусор.
    float visibility = saturate((10.0 - screenspaceRadius) / 100.0) * 0.5;

    // Минимальный отступ шага: тап вплотную к центру не несёт полезной информации, зато исправно
    // ловит квантование глубины и поднимает горизонт на ровной поверхности.
    float minS = GtaoPixelTooCloseThreshold / screenspaceRadius;

    float2 noise = GtaoSpatialNoise(uint2(pixel));

    [loop]
    for (int slice = 0; slice < SliceCount; slice++)
    {
        // Срез: плоскость, натянутая на viewVec и экранное направление omega. Пиксельный y растёт
        // вниз, вью-спейсный - вверх, отсюда минус у sinPhi в экранном направлении.
        float sliceK = (slice + noise.x) / SliceCount;
        float phi = sliceK * GtaoPI;
        float cosPhi = cos(phi);
        float sinPhi = sin(phi);
        float2 omega = float2(cosPhi, -sinPhi) * screenspaceRadius;

        float3 directionVec = float3(cosPhi, sinPhi, 0.0);
        float3 orthoDirectionVec = directionVec - dot(directionVec, viewVec) * viewVec;

        // Ось среза ортогональна и направлению, и взгляду - на неё проецируется нормаль.
        float3 axisVec = normalize(cross(orthoDirectionVec, viewVec));
        float3 projectedNormalVec = viewspaceNormal - axisVec * dot(viewspaceNormal, axisVec);

        float signNorm = sign(dot(orthoDirectionVec, projectedNormalVec));
        float projectedNormalVecLength = length(projectedNormalVec);
        float cosNorm = saturate(dot(projectedNormalVec, viewVec) / max(projectedNormalVecLength, 1e-6));

        // Угол проекции нормали относительно взгляда - центр дуги, которую могут закрыть горизонты.
        float n = signNorm * GtaoFastACos(cosNorm);

        // Нижняя граница горизонта - НЕ -1, как в исходной статье, а горизонт на уровне
        // касательной плоскости точки: под горизонтом «вес» значит уже другое, раз он отсчитывается
        // от нормали. Это и есть штатная защита от самозатенения плоскости - сэмплы, лежащие в
        // касательной плоскости (а на скользящем ракурсе таких большинство: экранная сетка тапов
        // ложится вдоль поверхности), горизонт над ней не поднимают.
        float lowHorizonCos0 = cos(n + GtaoHalfPI);
        float lowHorizonCos1 = cos(n - GtaoHalfPI);

        float horizonCos0 = lowHorizonCos0;
        float horizonCos1 = lowHorizonCos1;

        [unroll]
        for (int step = 0; step < StepsPerSlice; step++)
        {
            // R1-последовательность по (срез, шаг): сдвиг золотым сечением даёт разным шагам
            // разного среза непересекающиеся позиции вместо решётки, кратной одному шагу.
            float stepBaseNoise = float(slice + step * StepsPerSlice) * 0.6180339887498948482;
            float stepNoise = frac(noise.y + stepBaseNoise);

            float s = (step + stepNoise) / StepsPerSlice;
            s = pow(s, GtaoSampleDistributionPower);
            s += minS;

            float2 sampleOffset = s * omega;
            float sampleOffsetLength = length(sampleOffset);

            // Уровень цепочки по длине шага: чем дальше сэмпл, тем грубее (и тем шире усреднённая
            // им область) - см. GtaoDepthMipPS.hlsl.
            int mipLevel = (int)clamp(round(log2(sampleOffsetLength) - GtaoDepthMipSamplingOffset),
                                      0, GTAO_DEPTH_MIP_LEVELS - 1);

            // Привязка к центру текселя: без неё позиция сэмпла не совпадает с центром той тексели,
            // из которой прочитана глубина, и реконструированная точка «съезжает» по наклону.
            sampleOffset = round(sampleOffset) * invViewportSize;

            float2 sampleUV0 = centerUV + sampleOffset;
            float2 sampleUV1 = centerUV - sampleOffset;

            float sz0 = SampleDepthMip(sampleUV0, mipLevel);
            float sz1 = SampleDepthMip(sampleUV1, mipLevel);

            float3 samplePos0 = ViewPosFromUV(sampleUV0, sz0, aspect);
            float3 samplePos1 = ViewPosFromUV(sampleUV1, sz1, aspect);

            float3 sampleDelta0 = samplePos0 - pixCenterPos;
            float3 sampleDelta1 = samplePos1 - pixCenterPos;
            float sampleDist0 = max(length(sampleDelta0), 1e-6);
            float sampleDist1 = max(length(sampleDelta1), 1e-6);

            float3 sampleHorizonVec0 = sampleDelta0 / sampleDist0;
            float3 sampleHorizonVec1 = sampleDelta1 / sampleDist1;

            // Компенсация тонких окклюдеров: экранный горизонт как бегущий максимум неявно считает,
            // что окклюдер тянется вглубь бесконечно (штора толщиной в сантиметр затеняла бы стену
            // за собой как монолит). Растягивая z в мере расстояния, мы заставляем сэмплы, ушедшие
            // ЗА точку, выпадать из радиуса раньше боковых. При нулевой компенсации (дефолт XeGTAO)
            // выражение тождественно обычному расстоянию.
            float falloffBase0 = length(float3(sampleDelta0.x, sampleDelta0.y,
                                               sampleDelta0.z * (1.0 + GtaoThinOccluderCompensation)));
            float falloffBase1 = length(float3(sampleDelta1.x, sampleDelta1.y,
                                               sampleDelta1.z * (1.0 + GtaoThinOccluderCompensation)));
            float weight0 = saturate(falloffBase0 * falloffMul + falloffAdd);
            float weight1 = saturate(falloffBase1 * falloffMul + falloffAdd);

            float shc0 = dot(sampleHorizonVec0, viewVec);
            float shc1 = dot(sampleHorizonVec1, viewVec);

            // Сэмпл вне радиуса не отбрасывается, а плавно возвращается к нижней границе горизонта -
            // иначе окклюдер, выезжающий из радиуса при движении камеры, гас бы скачком.
            shc0 = lerp(lowHorizonCos0, shc0, weight0);
            shc1 = lerp(lowHorizonCos1, shc1, weight1);

            horizonCos0 = max(horizonCos0, shc0);
            horizonCos1 = max(horizonCos1, shc1);
        }

        // Фадж XeGTAO против передержки на крутых склонах (там же помечен как эмпирический:
        // "I can't figure out the slight overdarkening on high slopes").
        projectedNormalVecLength = lerp(projectedNormalVecLength, 1.0, 0.05);

        // Аналитический интеграл дуги видимости: a(h) = (cos(n) + 2h*sin(n) - cos(2h - n)) / 4.
        float h0 = -GtaoFastACos(horizonCos1);
        float h1 = GtaoFastACos(horizonCos0);

        float iarc0 = (cosNorm + 2.0 * h0 * sin(n) - cos(2.0 * h0 - n)) * 0.25;
        float iarc1 = (cosNorm + 2.0 * h1 * sin(n) - cos(2.0 * h1 - n)) * 0.25;

        visibility += projectedNormalVecLength * (iarc0 + iarc1);
    }

    visibility /= SliceCount;

    // Гашение на почти профильных поверхностях (NdotV -> 0). НЕ художественная правка и не
    // перестраховка: там реконструкция нормали из глубины вырождается принципиально - поверхность
    // занимает по экрану считанные пиксели, и одного шага квантования глубины хватает, чтобы
    // нормаль развернуло на десятки градусов. Дальше по срезу такая нормаль уводит центр дуги
    // видимости под горизонт, интеграл схлопывается в ноль, и на экране это сплошные ЧЁРНЫЕ пятна
    // с резкой границей плюс «полосы» вдоль силуэтов - складки штор, кромки карнизов, стена,
    // уходящая от камеры почти в профиль. Замерено (probe, Sponza, вид вдоль нефа): в этих
    // областях NdotV < 0.03, то есть окно смузстепа ровно их и накрывает.
    //
    // Здесь нельзя обойтись заваливанием нормали к камере (строка выше): оно спасает только от
    // нормали, глядящей ЗА поверхность (dot < 0), а вырождение начинается раньше, при dot,
    // чуть большем нуля. Единственное, что можно честно сказать про такой пиксель, - что данных
    // о его нормали нет, поэтому AO для него не оценивается вовсе (видимость = 1).
    //
    // XeGTAO этой проблемы не знает, потому что берёт нормаль из G-буфера; появится он в движке -
    // гашение снимается вместе с реконструкцией.
    visibility = lerp(1.0, visibility, smoothstep(0.005, 0.03, NdotV));

    visibility = pow(saturate(visibility), aoPower > 0.01 ? aoPower : GtaoFinalValuePower);

    // Полное затемнение запрещено ещё до денойза: пиксель заведомо виден (мы его и рисуем), а
    // нулевая видимость к тому же портит усреднение в фильтре.
    visibility = max(0.03, visibility);

    // Сырая (недофильтрованная) видимость может перескочить единицу и после усреднения вернуться
    // обратно, поэтому в UNORM8 она пакуется поделённой - денойзер домножает. Рёбра едут во втором
    // канале того же таргета: отдельный таргет под них не заводим, четырёх градаций на ребро
    // достаточно (см. GtaoPackEdges).
    output.color = float4(saturate(visibility / GtaoOcclusionTermScale), packedEdges, 0.0, 1.0);
    return output;
}
