// Общее тело пасса объёмного света - god rays и объёмный туман (см. VolumetricPS.hlsl /
// VolumetricMsaaPS.hlsl: обёртки определяют макрос DEPTH_FETCH под одиночный или мультисемпловый
// депт, ровно как у Fog*/Ssgi*/Ssao*).
//
// Чем это отличается от FogPass, который уже есть. Тот считает воздушную перспективу АНАЛИТИЧЕСКИ:
// один замкнутый интеграл на пиксель, солнечная подсветка - степень косинуса. Он ничего не знает о
// геометрии и потому не умеет главного - ТЕНЕЙ В СРЕДЕ. А световые столбы (god rays) - это ровно
// они: свет виден там, куда он дошёл, и не виден в тени колонны. Поэтому здесь марш вдоль луча с
// выборкой каскадного shadow map в каждой точке - другого способа получить столбы, согласованные с
// геометрией сцены, нет.
//
// Оба пасса живут одновременно и не мешают друг другу по построению: дальнюю дымку (потерю
// контраста с расстоянием) дешевле и стабильнее делать аналитикой, а марш отвечает только за
// РАССЕЯННЫЙ СВЕТ - то, что аналитика не берёт. Если включены оба, туман логично ставить мягче
// (см. порядок в GraphicsPipelineSimple.SignalGraph: марш идёт ДО тумана, и дымка ложится и на
// столбы тоже - как и должна, она ближе к камере).
//
// Как и туман, читает КОПИЮ кадра (_SceneTex) и пишет кадр целиком: PSO-абстракция движка
// блендинг не описывает (см. GraphicsStateInfo), а читать и писать один таргет нельзя.
//
// Работает в ЛИНЕЙНОМ пространстве, до тонемапа: рассеянный средой свет складывается с остальным
// светом кадра, а складывать свет можно только линейно.
#include "Instancing.hlsl"

Texture2D    _SceneTex;
SamplerState _SceneTex_sampler;

// 1x1 адаптированная яркость кадра (см. EyeAdaptationPS.hlsl) - та же, по которой делит тонемап.
// В LDR-режиме адаптации нет, сюда привязан плейсхолдер и он НЕ ЧИТАЕТСЯ (см. volExposureRelative):
// слот объявлен безусловно, а пустой дескриптор роняет валидацию Vulkan (VUID-08114).
Texture2D    _AdaptTex;

// Каскадный shadow map мирового света - тот же массив и тот же сэмплер, что у геометрии (см.
// UnlitInstancedPS.SampleWorldLightShadow); привязывается через IBatchRenderer.BindShadowResources.
// Обычный Z (clear 1.0 + Less при записи), сравнение LessEqual: SampleCmp возвращает 1 = освещено.
Texture2DArray         ShadowMaps;
SamplerComparisonState ShadowMaps_sampler;

// Punctual-света и их тени: среда рассеивает и свет ламп (см. петлю в Main), иначе спот в дымке
// «сухой» - геометрию освещает, а конуса в воздухе нет. Пул, матрицы и выбор грани куба зеркалят
// UnlitInstancedPS (кластерная петля); привязка - BindShadowResources, который для фуллскрин-
// пассов кладёт и карты, и оба буфера. Кластерная сетка здесь НЕ используется: она экранная
// по фрустуму поверхностей, а точка марша ей не принадлежит - светов сегмента камеры единицы,
// линейный проход дешевле ошибки адресации.
StructuredBuffer<PunctualLight> PunctualLights;
StructuredBuffer<float4> PunctualShadowMatrices;
Texture2DArray         PunctualShadowMaps;
SamplerComparisonState PunctualShadowMaps_sampler;

// viewProj слайса как row-major матрица - зеркало UnlitInstancedPS.LoadPunctualShadowMatrix
// (кбуферная передача матриц из структурного буфера транспонируется по-разному у бэкендов,
// см. разбор там же).
float4x4 LoadPunctualShadowMatrix(uint slice)
{
    uint row = slice * 4;
    return float4x4(PunctualShadowMatrices[row + 0], PunctualShadowMatrices[row + 1],
                    PunctualShadowMatrices[row + 2], PunctualShadowMatrices[row + 3]);
}

cbuffer View
{
    ViewData viewData;
}

// Матрицы каскадов приходят ТЕМ ЖЕ кбуфером Light, что и геометрии. Он переписывается по нескольку
// раз за кадр (ShadowPass льёт в него данные каждого каскада отдельно), но к моменту этого пасса в
// нём лежит основной набор - его положил ForwardPass, а пасс идёт строго после него.
cbuffer Light
{
    LightData lightData;
}

// Живые ручки объёмного света (окно Graphics -> VolumetricLightPassResources). Паддинг СКАЛЯРАМИ,
// не float3: трёхкомпонентный вектор по невыровненному смещению SPIR-V отвергает целиком (см.
// историю в SsaoCommon.hlsl). Зеркалит VolumetricConstantsData (VolumetricLightPass.cs).
cbuffer VolumetricConstants
{
    // Плотность среды на опорной высоте, 1/единица_мира.
    float volDensity;
    // Скорость спада плотности по высоте, 1/единица_мира. 0 - однородная среда без высоты.
    float volHeightFalloff;
    // Высота, на которой плотность равна volDensity.
    float volHeightRef;
    // Дистанция, с которой начинается марш - у самой камеры среда даёт только шум.
    float volStartDistance;

    // Предельная дальность марша. Прямо определяет ЦЕНУ пасса: шагов фиксированное число, и чем
    // дальше конец, тем крупнее шаг и грубее столбы.
    float volMaxDistance;
    // Число шагов марша. Главная ручка «качество против цены».
    float volSteps;
    // Общий коэффициент рассеяния среды: во сколько раз плотность превращается в свет.
    float volScatter;
    // Анизотропия фазовой функции Хеньи-Гринштейна, -1..1. >0 - рассеяние ВПЕРЁД: столбы видны в
    // основном при взгляде против солнца, как в реальной дымке. 0 - изотропная среда.
    float volAnisotropy;

    // Цвет солнечного рассеяния (линейный) и его сила - это и есть god rays.
    float volSunColorR, volSunColorG, volSunColorB;
    float volSunIntensity;

    // Цвет и сила НЕБЕСНОГО рассеяния - свет, приходящий в среду отовсюду. Тени его не режут,
    // поэтому именно он отвечает за «объёмный туман» в тени, а не за столбы. Без него среда в тени
    // становится абсолютно чёрной, и столбы читаются как вырезанные ножницами.
    float volAmbientColorR, volAmbientColorG, volAmbientColorB;
    float volAmbientIntensity;

    // Направление НА солнце в мире (нормализовано на CPU).
    float volSunDirX, volSunDirY, volSunDirZ;
    // Насколько тень режет солнечное рассеяние: 1 - полностью (настоящие столбы), 0 - тень
    // игнорируется. Ноль приходит и принудительно - когда теневого пасса в конвейере нет вовсе и
    // содержимое shadow map не определено (см. VolumetricLightPassResources.SetShadow).
    float volShadowStrength;

    // Мировой базис камеры - ЕДИНИЧНЫЕ right/up/forward, посчитанные на CPU прямо из eye/target
    // (см. VolumetricLightPassResources.SetCamera). Ровно та же схема и та же причина, что в
    // FogCommon.hlsl: разбор матрицы вида легко перепутать по строкам/столбцам, а ошибка даёт
    // объём, «приклеенный» к экрану.
    float volRightX, volRightY, volRightZ;
    // Потолок непрозрачности среды 0..1: сколько марш вправе съесть от исходного кадра.
    float volMaxOpacity;

    float volUpX, volUpY, volUpZ;
    // Коэффициент ЭКСТИНКЦИИ относительно плотности: насколько среда гасит свет, идущий сквозь неё.
    // Отдельно от volScatter - так среду можно сделать светящейся, но почти прозрачной (частый
    // художественный запрос: столбы есть, а кадр не мутнеет).
    float volExtinction;

    float volForwardX, volForwardY, volForwardZ;
    // Флор глушения НЕБЕСНОЙ доли рассеяния затенением: во сколько раз она слабее там, куда солнце
    // не дошло. 1 - не глушится вовсе (прежнее поведение), 0.1..0.2 - глубокий интерьер почти без
    // небесного свечения.
    //
    // Зачем это нужно, замерено на Sponza: небесная доля неглушёная ложится РОВНЫМ слоем на весь
    // кадр, включая крытую аркаду в двух метрах от камеры, куда неба не видно вовсе. Результат -
    // молочная пелена по всему кадру, поднятые чёрные и съеденная насыщенность: интерьер теряет
    // тот самый плотный контраст, ради которого сцену и ставят. Свечение обязано жить ТАМ, ГДЕ
    // СВЕТ, а не везде.
    //
    // Глушится именно тенью ключа, а не настоящей видимостью неба (её в объёме взять неоткуда), -
    // ровно тот же компромисс и та же формула, что у поверхностей в UnlitInstancedPS (skyDamp по
    // ProbeGiParams2.x). В интерьере эти две величины совпадают почти всегда: куда не дошло
    // солнце, туда обычно не дошло и небо.
    float volAmbientShadowFloor;

    // >0.5 - цвета заданы ОТНОСИТЕЛЬНО экспозиции (см. VolumetricExposureScale).
    float volExposureRelative;
    // Тот же key value, что уходит в авто-экспозицию и тонемап (TonemapConstants.x).
    float volExposureKey;
    // Множитель рассеяния punctual-светов (0 = среда ламп не видит). В отличие от volSunIntensity
    // это не цвет источника, а ручка ДОЛИ: сами света приходят из пула в сценовых линейных
    // единицах и экспозиционным множителем НЕ домножаются (см. сборку result в Main).
    float volPunctualIntensity;
    float volPad2;
}

// CameraData near/fov в ModelViewportEnvironment - тот же реверсивный-Z и тот же фиксированный
// FOV, что в FogCommon.hlsl и SkyBackgroundPS.hlsl.
static const float VolNearPlane = 0.05;
static const float VolTanHalfFov = 0.41421356; // tan(45deg / 2)

static const float VolPi = 3.14159265359;

// Сторона shadow map - обязана совпадать с ShadowRenderer.ShadowMapSize.
static const float VolShadowMapSize = 4096.0;

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

// Множитель яркости объёма - ровно тот же приём и та же причина, что в FogCommon.FogExposureScale:
// цвета рассеяния задаются в ОТОБРАЖАЕМЫХ единицах, а тонемап потом делит кадр на adapted и
// умножает на key. Без обратного домножения ручка цвета была бы бесполезна - абсолютный масштаб
// яркости сцены произволен и художнику неизвестен.
float VolumetricExposureScale()
{
    if (volExposureRelative < 0.5)
    {
        // LDR-конвейер: кадр уже display-referred, пересчитывать нечего.
        return 1.0;
    }

    float adapted = max(_AdaptTex.Load(int3(0, 0, 0)).r, 1e-4);
    return adapted / max(volExposureKey, 1e-4);
}

// Фазовая функция Хеньи-Гринштейна, НОРМИРОВАННАЯ на изотропный случай (домножена на 4pi, так что
// при g = 0 возвращает ровно 1). Так ручка силы остаётся прямой: смена анизотропии перераспределяет
// свет по направлениям, но не меняет общую яркость эффекта - иначе художнику приходилось бы
// подкручивать интенсивность после каждого движения ползунка g.
float VolumetricPhase(float cosTheta, float g)
{
    float g2 = g * g;
    float denom = 1.0 + g2 - 2.0 * g * cosTheta;
    return (1.0 - g2) / max(pow(max(denom, 1e-4), 1.5), 1e-4);
}

// Плотность среды на высоте h - экспоненциальный профиль, тот же, что интегрирует аналитически
// FogCommon.FogAverageDensity. Здесь он берётся ПОТОЧЕЧНО, потому что марш всё равно идёт по
// шагам ради теней: замкнутая форма ничего бы не сэкономила.
float VolumetricDensityAt(float height)
{
    if (volHeightFalloff < 1e-5)
    {
        return 1.0;
    }

    // Клампится СВЕРХУ: под опорной высотой экспонента растёт неограниченно, и камера, опущенная
    // на несколько десятков единиц ниже пола сцены, иначе упирается в непрозрачную белую стену.
    return min(exp(-volHeightFalloff * (height - volHeightRef)), 64.0);
}

// Тень мирового света в ТОЧКЕ ОБЪЁМА. Отличий от UnlitInstancedPS.SampleWorldLightShadow два, и
// оба вынужденные:
//
//  * нет normal-offset bias - у точки среды нет нормали. Вместо него чуть более крупная
//    константная добивка: acne на поверхности виден как рябь, а в объёме - лишь как небольшой
//    сдвиг границы столба, так что цена завышенного байеса здесь несопоставимо ниже;
//  * одна выборка вместо PCF 3x3 - на 32-64 шагах марша это 32-64 выборки на пиксель против
//    300-600. Мягкость границы столба берётся не из PCF, а из джиттера шага (см. Main): он
//    размазывает ступеньку по пикселям, и фильтровать каждую точку отдельно нет смысла.
float VolumetricShadow(float3 worldPos)
{
    [unroll]
    for (int c = 0; c < 4; c++)
    {
        // Ненулевая ширина = каскад заполнен (см. SimpleCullingAndRenderSystem.BuildLightData):
        // превью одиночной модели по-прежнему заполняет один, и цикл вырождается.
        if (lightData.CascadeSizes[c] <= 0.0)
        {
            continue;
        }

        float4 lightClip = mul(float4(worldPos, 1.0), lightData.CascadeMatrix[c]);
        float3 lightNdc = lightClip.xyz / max(lightClip.w, 1e-6);
        float2 shadowUv = float2(lightNdc.x * 0.5 + 0.5, 0.5 - lightNdc.y * 0.5);

        // Отступ от края карты - тот же, что у поверхностей (см.
        // UnlitInstancedPS.SUN_CASCADE_MARGIN_TEXELS), только уже: PCF здесь нет, за край тянет
        // лишь собственная фильтрация сравнивающего сэмплера (полтекселя). Без отступа краевые
        // выборки читают глубину чужого места сцены, и столб света получает прямую границу на
        // стыке каскадов. Полосы перехода здесь НЕТ сознательно: она стоила бы второй выборки на
        // каждом из 32-64 шагов марша, а ступенька резкости в объёме не читается - её съедает
        // джиттер шага.
        const float volMargin = 1.0 / VolShadowMapSize;
        if (any(shadowUv < volMargin) || any(shadowUv > 1.0 - volMargin)
            || lightNdc.z <= 0.0 || lightNdc.z >= 1.0)
        {
            // Точка вне этого каскада - пробуем следующий, крупнее.
            continue;
        }

        return ShadowMaps.SampleCmpLevelZero(ShadowMaps_sampler,
            float3(shadowUv, (float)c), lightNdc.z - 0.0015);
    }

    // За пределами всех каскадов - освещено. Именно поэтому предельная дальность марша обычно
    // держится в пределах последнего каскада: дальше столбы «выключаются» разом.
    return 1.0;
}

// Сторона слайса punctual-теней - обязана совпадать с LightClusters.ShadowMapSize (1024, не 4096
// солнечных каскадов).
static const float VolPunctualShadowMapSize = 1024.0;

// Тень punctual-света в точке объёма - зеркало кластерной петли UnlitInstancedPS (выбор грани
// куба по доминирующей оси, мировой байас с переводом локальной производной d(ndc)/dz), с теми же
// двумя вольностями, что у VolumetricShadow: без normal-offset (у точки среды нет нормали, байас
// втрое крупнее поверхностного) и одна выборка вместо PCF - ступеньку съедает джиттер шага.
// toFrag - вектор СВЕТ -> точка (для выбора грани куба).
float VolumetricPunctualShadow(PunctualLight l, float3 samplePos, float3 toFrag)
{
    if (l.ShadowParams.x < 0.0)
    {
        return 1.0;
    }

    uint slice = (uint)l.ShadowParams.x;
    if (l.DirectionType.w < 0.5)
    {
        float3 absDir = abs(toFrag);
        if (absDir.x >= absDir.y && absDir.x >= absDir.z)
            slice += toFrag.x > 0.0 ? 0 : 1;
        else if (absDir.y >= absDir.z)
            slice += toFrag.y > 0.0 ? 2 : 3;
        else
            slice += toFrag.z > 0.0 ? 4 : 5;
    }

    float4x4 shadowMatrix = LoadPunctualShadowMatrix(slice);
    float4 clip = mul(float4(samplePos, 1.0), shadowMatrix);
    if (clip.w <= 1e-4)
    {
        return 1.0;
    }

    float3 ndc = clip.xyz / clip.w;
    float2 uv = ndc.xy * float2(0.5, -0.5) + 0.5;
    if (any(uv < 0.0) || any(uv > 1.0) || ndc.z >= 1.0)
    {
        return 1.0;
    }

    float near = max(l.ShadowParams.z, 1e-4);
    float far = l.PositionRange.w;
    float z = max(clip.w, near);
    float tanHalf = l.DirectionType.w > 0.5 ? l.SpotAngles.z / max(l.SpotAngles.x, 1e-4) : 1.0;
    float texelWorld = 2.0 * tanHalf * z / VolPunctualShadowMapSize;
    float bias = texelWorld * 3.0 * near * far / max((far - near) * z * z, 1e-6);
    return PunctualShadowMaps.SampleCmpLevelZero(PunctualShadowMaps_sampler,
        float3(uv, (float)slice), ndc.z - bias);
}

// Interleaved gradient noise - псевдослучайное смещение первого шага, разное у соседних пикселей.
// Без него марш выдаёт КОЛЬЦА: у всех пикселей шаги попадают на одни и те же дистанции, и граница
// шага становится видимой дугой поперёк кадра. Джиттер меняет эту дугу на мелкое зерно, которое
// глаз читает как плёночный шум, а не как артефакт. Дёшево и не требует ни текстуры, ни истории
// кадров (temporal-фильтра в конвейере нет).
float VolumetricDither(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 viewportSize = viewData.viewport.zw;
    int2 pixel = clamp(int2(input.pos.xy), int2(0, 0), int2(viewportSize) - 1);
    float2 uv = input.pos.xy / viewportSize;

    float4 scene = _SceneTex.Sample(_SceneTex_sampler, uv);

    // Луч пикселя в МИРЕ. НЕ нормализуется намеренно - см. подробный разбор в FogCommon.hlsl:
    // проекция такого луча на ось камеры равна единице, и мировая точка получается как
    // camPos + ray * zView без единого деления.
    float aspect = viewData.viewport.z / max(viewData.viewport.w, 1.0);
    float3 ray = float3(volForwardX, volForwardY, volForwardZ)
        + float3(volRightX, volRightY, volRightZ) * (input.ndc.x * VolTanHalfFov * aspect)
        + float3(volUpX, volUpY, volUpZ) * (input.ndc.y * VolTanHalfFov);

    // Реверсивный-Z: ноль - это ФОН (таргет очищается нулём), а не нулевая глубина. Небу отдаём
    // предельную дальность: столбы обязаны быть видны и на фоне неба - там они, собственно, и
    // читаются лучше всего.
    float depth = DEPTH_FETCH(pixel);
    float zView = depth < 1e-6 ? volMaxDistance : VolNearPlane / depth;

    float rayLength = length(ray);
    float3 viewDir = ray / max(rayLength, 1e-6);

    float endDistance = min(zView * rayLength, volMaxDistance);
    float startDistance = min(volStartDistance, endDistance);
    float marchLength = endDistance - startDistance;

    if (marchLength <= 1e-4 || volDensity <= 0.0)
    {
        output.color = scene;
        return output;
    }

    int steps = clamp((int)volSteps, 4, 256);
    float stepLength = marchLength / (float)steps;

    // Фазовая функция от направления взгляда и направления НА солнце: при g > 0 максимум приходится
    // на взгляд против света - ровно то положение, в котором столбы видны в жизни.
    float3 sunDir = float3(volSunDirX, volSunDirY, volSunDirZ);
    float phase = VolumetricPhase(dot(viewDir, sunDir), clamp(volAnisotropy, -0.95, 0.95));

    float3 sunRadiance = float3(volSunColorR, volSunColorG, volSunColorB) * volSunIntensity * phase;
    float3 ambientRadiance = float3(volAmbientColorR, volAmbientColorG, volAmbientColorB)
        * volAmbientIntensity;

    float3 camPos = viewData.CameraWorldPos;
    float jitter = VolumetricDither(input.pos.xy);

    // Сегмент punctual-светов этой камеры в общем пуле (см. LightData.ClusterParams). Фазовая
    // функция ламп считается НА ШАГЕ: в отличие от солнца направление на свет меняется вдоль луча,
    // и при рассеянии вперёд конус спота, глядящий на камеру, вспыхивает - как фара в тумане.
    uint punctualOffset = (uint)lightData.ClusterParams.x;
    uint punctualCount = volPunctualIntensity > 0.0 ? (uint)lightData.ClusterParams.y : 0u;
    float anisotropy = clamp(volAnisotropy, -0.95, 0.95);

    float3 scattered = float3(0.0, 0.0, 0.0);
    // Рассеяние ламп копится ОТДЕЛЬНО: света пула лежат в сценовых линейных единицах, как сам
    // кадр, и экспозиционного домножения (в отличие от художественных цветов солнца/неба) не ждут.
    float3 scatteredScene = float3(0.0, 0.0, 0.0);
    float transmittance = 1.0;

    for (int i = 0; i < steps; i++)
    {
        float t = startDistance + ((float)i + jitter) * stepLength;
        float3 samplePos = camPos + viewDir * t;

        float density = volDensity * VolumetricDensityAt(samplePos.y);
        if (density <= 1e-7)
        {
            continue;
        }

        float shadow = lerp(1.0, VolumetricShadow(samplePos), saturate(volShadowStrength));

        // Свет, рассеиваемый ЕДИНИЦЕЙ ДЛИНЫ отрезка в сторону камеры. Солнечную долю тень режет
        // насухо, небесную - лишь ослабляет до флора (см. volAmbientShadowFloor): в тени колонны
        // небо всё ещё что-то даёт, а вот в крытой аркаде - почти ничего.
        float3 inScatter = (sunRadiance * shadow
            + ambientRadiance * lerp(saturate(volAmbientShadowFloor), 1.0, shadow))
            * density * volScatter;

        // Рассеяние punctual-светов - формулы затухания и конуса зеркалят кластерный шейдинг
        // UnlitInstancedPS, тень - VolumetricPunctualShadow выше.
        float3 punctualRadiance = float3(0.0, 0.0, 0.0);
        [loop]
        for (uint li = 0; li < punctualCount; li++)
        {
            PunctualLight l = PunctualLights[punctualOffset + li];
            float3 toLight = l.PositionRange.xyz - samplePos;
            float distSq = dot(toLight, toLight);
            float range = l.PositionRange.w;
            if (distSq > range * range)
            {
                continue;
            }

            float dist = sqrt(max(distSq, 1e-6));
            float3 dirToLight = toLight / dist;

            // Пол знаменателя КРУПНЕЕ поверхностного (+0.25 против +1e-2): шаг марша, легший
            // вплотную к лампе, с чистым 1/d^2 даёт одинокий пиксель-фаервол, который джиттер
            // превращает в мерцающее зерно вокруг источника.
            float distRatio2 = distSq / (range * range);
            float distFactor = saturate(1.0 - distRatio2 * distRatio2);
            float atten = distFactor * distFactor / (distSq + 0.25);

            if (l.DirectionType.w > 0.5)
            {
                float cd = dot(-dirToLight, l.DirectionType.xyz);
                float spotFactor = saturate((cd - l.SpotAngles.x) * l.SpotAngles.y);
                atten *= spotFactor * spotFactor;
            }

            if (atten <= 1e-6)
            {
                continue;
            }

            float punctualShadow = VolumetricPunctualShadow(l, samplePos, -toLight);
            punctualShadow = lerp(1.0, punctualShadow,
                saturate(volShadowStrength) * saturate(l.ShadowParams.y));

            float punctualPhase = VolumetricPhase(dot(viewDir, dirToLight), anisotropy);
            punctualRadiance += l.ColorIntensity.rgb * l.ColorIntensity.w
                * (atten * punctualPhase) * punctualShadow;
        }

        float3 inScatterScene = punctualRadiance * volPunctualIntensity * density * volScatter;

        // Аналитическое интегрирование рассеяния ПО ОТРЕЗКУ, а не «в точке × длину шага»:
        //
        //   S(x) = S0 * exp(-sigma * x)  =>  integral[0..dx] = S0 * (1 - exp(-sigma*dx)) / sigma
        //
        // Разница не косметическая. Наивная сумма зависит от числа шагов: художник крутит качество -
        // и вместе с ним меняется ЯРКОСТЬ эффекта, то есть ручка качества перестаёт быть ручкой
        // качества. Здесь же 16 и 128 шагов дают одну и ту же яркость, отличаясь только гладкостью
        // границ.
        float sigma = max(density * volExtinction, 1e-6);
        float stepTransmittance = exp(-sigma * stepLength);
        float segment = transmittance * (1.0 - stepTransmittance) / sigma;
        scattered += inScatter * segment;
        scatteredScene += inScatterScene * segment;

        transmittance *= stepTransmittance;

        // Луч уже почти непрозрачен - остаток вклада ниже кванта таргета. Ранний выход экономит
        // основное время именно на плотной среде, где марш дороже всего.
        if (transmittance < 1e-3)
        {
            break;
        }
    }

    // Потолок непрозрачности - художественная ручка без физического смысла, та же, что у тумана:
    // сквозь среду всегда что-то видно, как бы плотно её ни задали.
    float minTransmittance = 1.0 - saturate(volMaxOpacity);
    transmittance = max(transmittance, minTransmittance);

    // Экспозиционный множитель - ТОЛЬКО на солнечно-небесное рассеяние: его цвета заданы в
    // отображаемых единицах. Рассеяние ламп (scatteredScene) уже в сценовых линейных, как кадр.
    float3 result = scene.rgb * transmittance + scattered * VolumetricExposureScale()
        + scatteredScene;

    // Альфа берётся ОТ СЦЕНЫ: пасс рисует поверх кадра, и своя альфа выбила бы прозрачный фон
    // бейкера иконок (та же причина, что в FogCommon.hlsl).
    output.color = float4(result, scene.a);
    return output;
}
