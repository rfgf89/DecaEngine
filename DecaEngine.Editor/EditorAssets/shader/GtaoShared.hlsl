// Константы и общая математика GTAO, разделяемые главным пассом (GtaoCommon.hlsl), построителем
// мип-цепочки глубин (GtaoDepthMipPS.hlsl) и денойзером (GtaoDenoisePS.hlsl). Значения - дефолты
// XeGTAO (см. XeGTAO.h, XE_GTAO_DEFAULT_*); держим их в одном месте, потому что фильтр мипов
// обязан пользоваться ТЕМ ЖЕ радиусом влияния, что и сам пасс: он взвешивает глубины по тому же
// falloff, и разъедься эти два радиуса - мипы начали бы усреднять то, чего пасс на этой дальности
// уже не видит.

static const float GtaoPI = 3.14159265359;
static const float GtaoHalfPI = 1.57079632679;

// Фиксированный FOV 45 градусов превью (CameraData в ModelViewportEnvironment) и его near.
static const float GtaoTanHalfFov = 0.41421356;
static const float GtaoNearPlane = 0.05;

// Потолок вью-спейсной глубины - предел half (таргеты цепочки RGBA16F), см.
// GtaoDepthPrefilterCommon.hlsl.
static const float GtaoMaxViewDepth = 65504.0;

// Сколько звеньев в цепочке глубин (mip 0 - полное разрешение). Пять - как в XeGTAO
// (XE_GTAO_DEPTH_MIP_LEVELS); дальше уменьшать нечего, шаг сэмплов такой дальности уже не
// достигает.
#define GTAO_DEPTH_MIP_LEVELS 5

// Радиус эффекта задаётся не «как есть», а с поправкой: экранное пространство систематически
// недооценивает заслонённость (за силуэтом нет данных), и множитель компенсирует это смещение -
// XeGTAO подобрал его подгонкой под трассированный ground truth.
static const float GtaoRadiusMultiplier = 1.457;

// Доля радиуса, на которой вес сэмпла спадает с единицы до нуля: вес держится единицей до
// 38.5% радиуса и линейно гаснет к самому радиусу.
static const float GtaoFalloffRange = 0.615;

// Распределение шагов вдоль среза: >1 стягивает сэмплы к точке, где контактное затемнение
// важнее всего.
static const float GtaoSampleDistributionPower = 2.0;

// «Толщинная» эвристика XeGTAO: сэмпл, ушедший ЗА точку по глубине, теряет вес быстрее бокового -
// компенсация того, что экранный горизонт неявно считает любой окклюдер бесконечно толстым.
// Дефолт XeGTAO - ноль (эвристика выключена, ожидаемый диапазон [0, 0.7]).
static const float GtaoThinOccluderCompensation = 0.0;

// Контраст итоговой видимости (аналог Intensity у SSAO). Перебивается ручкой aoPower окна Graphics.
static const float GtaoFinalValuePower = 2.2;

// Смещение при выборе мипа по длине шага: чем больше, тем позже включаются грубые уровни -
// главный компромисс «пропускная способность памяти против временной стабильности и тонких
// объектов».
static const float GtaoDepthMipSamplingOffset = 3.30;

// Видимость до денойза может перескочить единицу (усреднится обратно только после фильтрации),
// поэтому в UNORM8 она пакуется поделённой на этот множитель, а денойзер домножает обратно.
static const float GtaoOcclusionTermScale = 1.5;

// Ближе этого расстояния (в пикселях) сэмпл не несёт информации - зато исправно ловит квантование
// глубины и поднимает горизонт на ровной поверхности.
static const float GtaoPixelTooCloseThreshold = 1.3;

// Сила размытия денойзера: вес центрального пикселя относительно соседей (XeGTAO DenoiseBlurBeta).
static const float GtaoDenoiseBlurBeta = 1.2;

// Легаси-режим (мировой радиус никто не пушил - probe без модели, сторонние потребители): радиус
// живёт в долях высоты экрана.
static const float GtaoLegacyScreenRadius = 0.06;

// Потолок экранного радиуса: при экстремальном зуме иначе шаг сэмплов разогнало бы на весь экран,
// а цена кадра - вместе с ним.
static const float GtaoMaxScreenRadiusFraction = 0.25;

// Нижний предел видимости по умолчанию: экранный AO не вправе гасить свет в ноль. Перебивается
// ручкой aoFloor окна Graphics.
static const float GtaoDefaultFloor = 0.12;

/// Размер пикселя в мировых единицах на глубине z: viewX = ndc.x * tan(fov/2) * aspect * z, шаг
/// ndc.x на пиксель равен 2/width, а aspect = width/height - ширина сокращается, остаётся высота.
float GtaoPixelWorldSize(float viewZ, float viewportHeight)
{
    return 2.0 * GtaoTanHalfFov * viewZ / max(viewportHeight, 1.0);
}

/// Мировой радиус влияния AO на глубине viewZ.
///
/// worldRange > 0 - радиус задан в мировых единицах (пушится после кадрирования модели, см.
/// SsaoPassResources.SetWorldRange): контактная тень не схлопывается при приближении камеры.
/// Сверху он всё равно ограничен экранным потолком, причём ИМЕННО как мировая величина, а не
/// зажатием одного лишь шага сэмплов: falloff считается от того же радиуса, и зажми мы только
/// шаг - сэмплы попадали бы в зону полного веса, которой на экране уже нет.
///
/// worldRange == 0 - легаси: радиус в долях высоты экрана, пересчитанный в мир на этой глубине.
/// Раньше в этом режиме радиус поиска и дальность falloff задавались НЕЗАВИСИМО (0.06 высоты
/// экрана против 0.22 глубины точки), то есть falloff покрывал примерно вчетверо большее
/// расстояние, чем куда вообще дотягивались сэмплы, и просто не срабатывал.
float GtaoEffectRadius(float viewZ, float viewportHeight, float worldRange)
{
    float pixelWorldSize = GtaoPixelWorldSize(viewZ, viewportHeight);
    float maxRadius = GtaoMaxScreenRadiusFraction * viewportHeight * pixelWorldSize;
    return worldRange > 0.0
        ? min(worldRange * GtaoRadiusMultiplier, maxRadius)
        : GtaoLegacyScreenRadius * viewportHeight * pixelWorldSize;
}

/// Быстрый обратный корень через манипуляцию экспонентой (Drobot2014a) - точности хватает
/// аргументу FastACos, зато нет полноценного sqrt в самом горячем цикле.
float GtaoFastSqrt(float x)
{
    return asfloat(0x1fbd1df5 + (asint(x) >> 1));
}

/// acos с полиномиальным приближением: вход [-1, 1], выход [0, PI]. В главном цикле acos зовётся
/// по два раза на срез, и точный вариант там заметно дороже, чем стоит его точность.
///
/// Аргумент КЛАМПИТСЯ, и это не перестраховка: на вход идёт dot двух нормализованных векторов,
/// который в fp32 регулярно выходит за единицу на единицы ulp. GtaoFastSqrt - битовый трюк над
/// экспонентой, у него нет понятия «корень из отрицательного»: он молча вернёт отрицательное
/// число, res сменит знак, и угол горизонта уедет в другую сторону. В XeGTAO кламп не нужен,
/// потому что там та же арифметика идёт в half - у неё до единицы попросту не хватает разрядов.
float GtaoFastACos(float inX)
{
    float x = min(abs(inX), 1.0);
    float res = -0.156583 * x + GtaoHalfPI;
    res *= GtaoFastSqrt(1.0 - x);
    return inX >= 0.0 ? res : GtaoPI - res;
}

/// «Рёбра» LRTB - мера того, лежит ли сосед на той же поверхности, что и центр (1 - лежит,
/// 0 - обрыв глубины). Ключевое здесь - поправка на СКЛОН: без неё честный наклон плоскости
/// неотличим от силуэта, и на скользящем полу все четыре соседа выглядят «чужими».
float4 GtaoCalculateEdges(float centerZ, float leftZ, float rightZ, float topZ, float bottomZ)
{
    float4 edgesLRTB = float4(leftZ, rightZ, topZ, bottomZ) - centerZ;

    float slopeLR = (edgesLRTB.y - edgesLRTB.x) * 0.5;
    float slopeTB = (edgesLRTB.w - edgesLRTB.z) * 0.5;
    float4 edgesLRTBSlopeAdjusted = edgesLRTB + float4(slopeLR, -slopeLR, slopeTB, -slopeTB);
    edgesLRTB = min(abs(edgesLRTB), abs(edgesLRTBSlopeAdjusted));
    return saturate(1.25 - edgesLRTB / (centerZ * 0.011));
}

/// Упаковка рёбер в один UNORM8-канал: по 2 бита на ребро, то есть четыре градации (0, 1/3, 2/3, 1) -
/// достаточно, чтобы денойзер получал плавные переходы, и ровно столько, сколько влезает рядом с
/// самой видимостью в RGBA8-таргете (отдельный таргет под рёбра не заводим).
float GtaoPackEdges(float4 edgesLRTB)
{
    edgesLRTB = round(saturate(edgesLRTB) * 2.9);
    return dot(edgesLRTB, float4(64.0 / 255.0, 16.0 / 255.0, 4.0 / 255.0, 1.0 / 255.0));
}

float4 GtaoUnpackEdges(float packedVal)
{
    uint packed = (uint)(packedVal * 255.5);
    float4 edgesLRTB;
    edgesLRTB.x = float((packed >> 6) & 0x03) / 3.0;
    edgesLRTB.y = float((packed >> 4) & 0x03) / 3.0;
    edgesLRTB.z = float((packed >> 2) & 0x03) / 3.0;
    edgesLRTB.w = float((packed >> 0) & 0x03) / 3.0;
    return saturate(edgesLRTB);
}
