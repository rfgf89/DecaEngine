// Раунд обновления probe-GI на GPU - перенос ProbeGiBaker.RunRound в compute. Ради этого всё и
// затевалось: раунд на CPU стоит десятки миллисекунд, здесь - доли, и пробы могут обновляться
// каждый кадр, то есть свет и геометрия становятся динамическими.
//
// Один поток = одна проба. Трассировка идёт через общий интерфейс SceneTrace.hlsl, поэтому шейдер
// одинаков для аппаратного и программного путей - разница только в кейворде при компиляции.
//
// Поле хранится в той же раскладке, что и SH-атласы (см. ProbeGiBakeResult): четыре float4 на
// пробу. Это не совпадение - в дальнейшем шейдер будет писать прямо в атласы как в UAV, без
// промежуточной упаковки на CPU.

#include "SceneTrace.hlsl"

cbuffer ProbeRoundParams
{
    // xyz = угол сетки в мире, w = вес нового раунда (бегущее среднее, см. RunRound).
    float4 ProbeGridOrigin;
    // xyz = шаг сетки проб, w = предельная дальность луча.
    float4 ProbeGridCell;
    // xyz = направление НА солнце, w = лучей за раунд.
    float4 ProbeSunDirection;
    // xyz = цвет/интенсивность солнца, w = сколько всего проб.
    float4 ProbeSunColor;
    // x = эпсилон отступа теневого луча, y = кламп дистанции для окто-карты, z = отступ точки
    // сбора вдоль нормали, w = множитель обратной связи поля (0 = без переотскока).
    float4 ProbeRoundParams;
    // Порция раунда: x = индекс первого элемента, y = индекс за последним. Раунд режется на порции
    // и растягивается на несколько кадров - один диспатч на все пробы занимал бы GPU так надолго,
    // что презентация голодает, кадровый объект уходит в таймаут, а следом снимается устройство.
    // z = потолок яркости одного луча (0 = без ограничения, см. RealtimeMaxRayLuminance),
    // w = предел изменения пробы за раунд в долях её яркости (0 = без предела, см. RealtimeMaxStep).
    float4 ProbeChunk;
    // x = предел релокации пробы в мировых единицах (0 = релокация выключена, см.
    // ProbeGiBakeSession.RelocationLimit); y = гамма перцептивного накопления (1 = линейно,
    // см. RealtimeGamma); z = 1 - кэш поверхностей выключен (реальное время: его статичная
    // геометрия врёт на движущейся сцене, отскок идёт из поля). w = сон проб: 0 - выключен,
    // иначе 1 + (номер веера & 3) - фаза раунда для пробуждения спящих (см. mainProbe).
    float4 ProbeRelocation;
    // x = предел релокации СВЕЖЕЙ пробы (её плоскость только что въехала прокруткой объёма, см.
    // ProbeGiBakeSession.ProbeFresh): у неё своё окно, не общесеточное.
    float4 ProbeScroll;
};

// Направления лучей раунда считает CPU и присылает готовыми. Повторять формулу веера Фибоначчи в
// шейдере нельзя: расхождение в последнем бите синуса увело бы луч на соседний треугольник у
// силуэта, и сверка с CPU-эталоном перестала бы что-либо значить.
StructuredBuffer<float4> _ProbeRayDirections;

// Длина окна свежести - зеркало ProbeGiBaker.RelocationRounds. Разъехаться им нельзя: по этому
// числу считается номер раунда свежей пробы, а значит и вес её бегущего среднего.
#define PROBE_FRESH_WINDOW 5

// Поле проб, четыре float4 на пробу (раскладка атласов):
//   [0] rgb = SH L0,  a = видимость неба
//   [1] rgb = SH L1x, a = валидность пробы
//   [2] rgb = SH L1y, a = доля солнца
//   [3] rgb = SH L1z, a = резерв
//
// Двойной буфер: сбор переотскока читает СОСЕДНИЕ пробы, а их этот же раунд перезаписывает - без
// разделения чтения и записи результат зависел бы от порядка выполнения групп. CPU-версия по той
// же причине держит два набора массивов и меняет их местами.
StructuredBuffer<float4>   _ProbeFieldRead;
RWStructuredBuffer<float4> _ProbeField;

// Геометрические накопители: xyz = всего лучей / промахов / попаданий в заднюю грань. Копятся по
// ВСЕМ раундам точными суммами - они от освещения не зависят (см. RunRound).
RWStructuredBuffer<int4> _ProbeCounters;

// Окто-карта глубин, PROBE_VIS_RES² на пробу: x = сумма дистанций, y = сумма квадратов, z = счётчик.
RWStructuredBuffer<float4> _ProbeVisibility;

// xyz = размер ПЛОТНОЙ сетки проб, w = насыщенность отскока.
// (второй кбуфер, чтобы не ломать раскладку первого)
cbuffer ProbeGridParams
{
    float4 ProbeGridCounts;
    // xyz = тороидальное смещение сетки в пробах: узел c лежит по индексу (c + scroll) mod counts.
    float4 ProbeGridScroll;
    // xyz = ПЕРВАЯ въехавшая прокруткой плоскость по каждой оси, в координатах ХРАНЕНИЯ.
    // -1 = по этой оси не двигались; очень большое значение (ProbeGiBakeSession.ClearWholeAxis) =
    // чистить ось целиком: камеру телепортировали дальше размера объёма, беречь нечего.
    float4 ProbeGridClear;
    // xyz = сколько плоскостей подряд въехало по каждой оси, начиная с ProbeGridClear.
    float4 ProbeGridClearSpan;
    // xyz = размер вокселя кэша поверхностей, w = число живых вокселей (0 = кэш выключен).
    float4 SurfaceVoxel;
    // xyz = размер воксельной сетки кэша.
    float4 SurfaceCounts;
    // x = поворот энвайронмента (радианы), y = множитель яркости неба, z = сторона окто-карты
    // видимости (ручка «Visibility res», см. ProbeGiBakeResult.VisRes), w - резерв.
    float4 ProbeSkyParams;
};

// Координаты узла сетки по индексу ХРАНЕНИЯ пробы и обратно - зеркало ProbeGiBaker.StorageIndex /
// Wrap. Слагаемое counts перед остатком обязательно: смещение бывает отрицательным, а % в HLSL
// (как и в C#) даёт отрицательный остаток от отрицательного делимого.
int3 ProbeStorageCoords(uint probe)
{
    int cx = (int)ProbeGridCounts.x;
    int cy = (int)ProbeGridCounts.y;
    return int3((int)probe % cx, (int)probe / cx % cy, (int)probe / (cx * cy));
}

int3 ProbeWrap(int3 coords, int3 scroll, int3 counts)
{
    return ((coords + scroll) % counts + counts) % counts;
}

// Въехала ли эта плоскость последней прокруткой - её поле принадлежит месту, откуда объём уехал, и
// накопители надо не смешивать, а обнулить (эталон RTXGI: DDGIClearScrolledPlane).
bool ProbeScrolledIn(int3 storage)
{
    bool cleared = false;
    [unroll]
    for (int axis = 0; axis < 3; axis++)
    {
        int plane = (int)ProbeGridClear[axis];
        if (plane < 0)
        {
            continue;
        }

        int count = (int)ProbeGridCounts[axis];
        if (plane >= count)
        {
            // Вся ось - телепорт дальше размера объёма.
            cleared = true;
            continue;
        }

        // Плоскостей могло въехать несколько подряд; диапазон заворачивается по периоду сетки.
        int span = min((int)ProbeGridClearSpan[axis], count);
        int delta = (storage[axis] - plane % count + count) % count;
        if (delta < span)
        {
            cleared = true;
        }
    }

    return cleared;
}

// Сторона окто-карты видимости. Приходит кбуфером, а не дефайном: это ручка окна Graphics, и
// раскладка буфера глубин (VisRes² на пробу) задаётся на CPU тем же значением - разойтись им
// нельзя. 0 (кбуфер не заполнен) трактуется как дефолтные 8.
int ProbeVisRes()
{
    int res = (int)ProbeSkyParams.z;
    return res > 0 ? res : 8;
}

// Панорама окружения - тот же источник неба, что у пиксельного шейдера. Луч-промах берёт радианс
// отсюда; на CPU это была функция выборки той же карты (см. PreviewEnvironmentMap).
Texture2D    _EnvMap;
SamplerState _EnvMap_sampler;

// Небо по направлению. Конвенция обязана совпадать с SampleEnvironment в UnlitInstancedPS:
// поворот вокруг Y для equirect - это сдвиг U, знак +yaw.
float3 ProbeSampleSky(float3 dir)
{
    const float PI = 3.14159265;
    float2 uv = float2(atan2(dir.z, dir.x) / (2.0 * PI) + 0.5 + ProbeSkyParams.x / (2.0 * PI),
                       acos(clamp(dir.y, -1.0, 1.0)) / PI);
    // Нулевой мип: бейку нужен неразмытый радианс, префильтрованные мипы - для спекуляра.
    return _EnvMap.SampleLevel(_EnvMap_sampler, uv, 0.0).rgb * ProbeSkyParams.y;
}

// Кэш радианса на поверхностях (см. SurfaceCache в ProbeGi.cs). Индекс - плотный массив по всей
// воксельной сетке, -1 = поверхности тут нет; остальное - по живым вокселям.
StructuredBuffer<int>    _SurfaceIndex;
StructuredBuffer<float4> _SurfacePosition;
StructuredBuffer<float4> _SurfaceNormal;
StructuredBuffer<float4> _SurfaceAlbedo;

// rgb = исходящий радианс вокселя, a = доля солнца в нём.
RWStructuredBuffer<float4> _SurfaceRadiance;

// Атласы проб как UAV: раунд пишет их напрямую, поэтому шага упаковки на CPU (прежний Snapshot) в
// GPU-пути нет вовсе. Раскладка - пул кирпичей, см. ProbeGiBaker.ProbeTexel.
RWTexture2D<float4> _ProbeAtlasSh0;
RWTexture2D<float4> _ProbeAtlasSh1;
RWTexture2D<float4> _ProbeAtlasSh2;
RWTexture2D<float4> _ProbeAtlasSh3;
RWTexture2D<float4> _ProbeAtlasVis;
RWTexture2D<float4> _ProbeAtlasOffset;

// Смещения проб от их узлов сетки, мировые единицы (см. ProbeGiBakeResult.Offset). Раунд их и
// считает, и читает: трассировать надо из АКТУАЛЬНОЙ позиции пробы, иначе релокация не сойдётся -
// статистика задних граней осталась бы от старого места.
RWStructuredBuffer<float4> _ProbeOffsets;

// ИЗМЕНЧИВОСТЬ ПРОБЫ (RTXGI-DDGI, ProbeBlendingCS.hlsl:552-562, «Probe Variability»): коэффициент
// вариации её яркости, то есть отношение оценки среднеквадратичного разброса к самому среднему.
// Величина безразмерная и потому сравнимая между пробами тёмными и яркими - именно этим она лучше
// абсолютной разницы, по которой считается счётчик спокойствия для сна.
//
// x = коэффициент вариации, y = вес пробы в усреднении (0 у тех, чьё мнение не считается: пробы в
// стене и заведомо сошедшиеся). Дальше это сворачивается в одно число на объём (см.
// ProbeVariabilityCS.hlsl) и служит признаком «объём сошёлся, трассировку можно остановить вовсе».
RWStructuredBuffer<float2> _ProbeVariability;

// Тексель пробы в атласе - зеркало ProbeGiBaker.ProbeTexel. Ширина атласа равна оси X сетки,
// поэтому индекс хранения раскладывается в тексель делением с остатком, и это вся адресация.
uint2 ProbeAtlasTexel(uint probe)
{
    uint width = max((uint)ProbeGridCounts.x, 1u);
    return uint2(probe % width, probe / width);
}

// Индекс вокселя, накрывающего точку, или -1. Зеркало SurfaceCache.Lookup.
int SurfaceLookup(float3 worldPos)
{
    if (SurfaceVoxel.w < 0.5)
    {
        return -1;
    }

    float3 f = (worldPos - ProbeGridOrigin.xyz) / SurfaceVoxel.xyz;
    int3 v = (int3)floor(f);
    if (any(v < 0) || any(v >= (int3)SurfaceCounts.xyz))
    {
        return -1;
    }

    return _SurfaceIndex[(v.z * (int)SurfaceCounts.y + v.y) * (int)SurfaceCounts.x + v.x];
}

static const float SH_Y00 = 0.28209479;
static const float SH_Y1  = 0.48860251;

float ProbeLuminance(float3 c)
{
    return 0.2126 * c.x + 0.7152 * c.y + 0.0722 * c.z;
}

// Обратное окто-преобразование: направление по центру текселя карты глубин. Нужно для укладки
// глубины по КОНУСУ (см. цикл лучей) - обратная операция к ProbeOctEncode.
float3 ProbeOctDecode(float2 uv)
{
    float2 p = uv * 2.0 - 1.0;
    float3 d = float3(p.x, p.y, 1.0 - abs(p.x) - abs(p.y));
    if (d.z < 0.0)
    {
        d.xy = (1.0 - abs(d.yx)) * float2(d.x >= 0.0 ? 1.0 : -1.0, d.y >= 0.0 ? 1.0 : -1.0);
    }

    return normalize(d);
}

// Резкость лобы, которой луч размазывается по окто-карте глубин (§4.4 статьи: депт-тексели
// обновляются по cosine-power лобе). 64 берётся шестью возведениями в квадрат - это дешевле pow, а
// порог 0.001 отсекает всё за 26 градусами от луча, то есть горстку текселей из 64.
#define PROBE_DEPTH_SHARPNESS_SQUARINGS 6
#define PROBE_DEPTH_WEIGHT_EPSILON      0.001

// Окно скользящих сумм геометрических накопителей в РЕАЛЬНОМ ВРЕМЕНИ, в раундах (см. main).
// В запечке затухания нет вовсе: там раундов конечное число и нужны точные суммы для сверки с CPU.
#define PROBE_GEOMETRY_WINDOW 64.0

// Окто-кодирование - обязано совпадать с ProbeGiBaker.OctEncode и OctEncode в UnlitInstancedPS.
float2 ProbeOctEncode(float3 d)
{
    float sum = abs(d.x) + abs(d.y) + abs(d.z);
    float2 p = d.xy / sum;
    if (d.z < 0.0)
    {
        p = (1.0 - abs(p.yx)) * float2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
    }

    return p * 0.5 + 0.5;
}

// Сбор переотскока из поля проб в точке попадания - построчное зеркало
// ProbeGiBaker.EvalIrradiance. Трилинейка по восьми углам ячейки с весами валидности и мягким
// backface-весом; без последнего мультибаунс за несколько раундов протаскивает свет сквозь стены.
float3 ProbeGatherIrradiance(float3 pos, float3 normal, out float sunFracOut)
{
    sunFracOut = 0.0;

    float3 origin = ProbeGridOrigin.xyz;
    float3 cell = ProbeGridCell.xyz;
    float3 f = clamp((pos - origin) / cell, 0.0, ProbeGridCounts.xyz - 1.0);

    // Базовый узел ячейки - просто пол координат сетки: у плотной сетки проба есть в каждом узле,
    // искать её больше негде и не через что. Индирекции здесь не осталось вовсе.
    int3 counts = (int3)ProbeGridCounts.xyz;
    int3 scroll = (int3)ProbeGridScroll.xyz;
    int3 l = clamp((int3)floor(f), 0, counts - 2);
    float3 t = saturate(f - (float3)l);

    float3 sh0 = 0.0, shX = 0.0, shY = 0.0, shZ = 0.0;
    float fracSum = 0.0;
    float weightSum = 0.0;

    [unroll]
    for (int corner = 0; corner < 8; corner++)
    {
        int3 o = int3(corner & 1, (corner >> 1) & 1, corner >> 2);
        int3 lp = l + o;
        int3 sp = ProbeWrap(lp, scroll, counts);
        uint index = (uint)((sp.z * counts.y + sp.y) * counts.x + sp.x);

        float4 field1 = _ProbeFieldRead[index * 4 + 1];
        float w = (o.x ? t.x : 1.0 - t.x) * (o.y ? t.y : 1.0 - t.y) * (o.z ? t.z : 1.0 - t.z)
                * field1.a;

        // Смещение соседа учитывается и здесь: вес по нормали меряет направление НА пробу, а она
        // могла быть отодвинута из стены. Чтение буфера, который этот же диспатч пишет, здесь
        // безобидно - в худшем случае сосед отдаст смещение прошлого раунда, а разница между ними
        // заведомо меньше отступа, на который влияет вес.
        float3 probePos = origin + (float3)lp * cell + _ProbeOffsets[index].xyz;
        float3 toProbe = probePos - pos;
        float toProbeLen = length(toProbe);
        float wrap = (dot(toProbe / max(toProbeLen, 1e-4), normal) + 1.0) * 0.5;
        w *= wrap * wrap + 0.05;

        float4 field0 = _ProbeFieldRead[index * 4 + 0];
        float4 field2 = _ProbeFieldRead[index * 4 + 2];
        float4 field3 = _ProbeFieldRead[index * 4 + 3];

        sh0 += field0.rgb * w;
        shX += field1.rgb * w;
        shY += field2.rgb * w;
        shZ += field3.rgb * w;
        fracSum += field2.a * w;
        weightSum += w;
    }

    if (weightSum < 1e-4)
    {
        return 0.0;
    }

    float inv = 1.0 / weightSum;
    sunFracOut = saturate(fracSum * inv);
    float3 e = (sh0 * inv) * 0.8862269
             + ((shX * inv) * normal.x + (shY * inv) * normal.y + (shZ * inv) * normal.z) * 1.0233267;
    return max(e, 0.0);
}

// Обновление кэша поверхностей - отдельный проход ПЕРЕД раундом проб, зеркало
// ProbeGiBaker.UpdateSurfaceCache. Резкая часть освещения (солнце) считается теневым лучом на
// каждый воксель, гладкая (переотскок) берётся из поля проб, которому детализация не нужна.
[numthreads(64, 1, 1)]
void mainSurface(uint3 threadId : SV_DispatchThreadID)
{
    uint voxel = (uint)ProbeChunk.x + threadId.x;
    if (voxel >= (uint)ProbeChunk.y || voxel >= (uint)SurfaceVoxel.w)
    {
        return;
    }

    float3 normal = _SurfaceNormal[voxel].xyz;
    float3 pos = _SurfacePosition[voxel].xyz + normal * (ProbeRoundParams.x * 4.0);
    float tMax = ProbeGridCell.w;

    float3 sunIrradiance = 0.0;
    float ndotl = dot(normal, ProbeSunDirection.xyz);
    if (ndotl > 0.0 && !SceneTraceAnyHit(pos, ProbeSunDirection.xyz, tMax))
    {
        sunIrradiance = ProbeSunColor.rgb * ndotl;
    }

    float3 ambient = 0.0;
    float ambientFrac = 0.0;
    float feedback = ProbeRoundParams.w;
    if (feedback > 0.0)
    {
        ambient = ProbeGatherIrradiance(pos, normal, ambientFrac) * feedback;
    }

    float3 irradiance = sunIrradiance + ambient;
    float3 rawAlbedo = _SurfaceAlbedo[voxel].rgb;
    float3 albedo = lerp((float3)ProbeLuminance(rawAlbedo), rawAlbedo, ProbeGridCounts.w);

    float lumIrr = ProbeLuminance(irradiance);
    float sunFrac = lumIrr > 1e-6
        ? saturate((ProbeLuminance(sunIrradiance) + ProbeLuminance(ambient) * ambientFrac) / lumIrr)
        : 0.0;

    _SurfaceRadiance[voxel] = float4(albedo * irradiance * (1.0 / 3.14159265), sunFrac);
}

[numthreads(64, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint probe = (uint)ProbeChunk.x + threadId.x;
    if (probe >= (uint)ProbeChunk.y || probe >= (uint)ProbeSunColor.w)
    {
        return;
    }

    // Сторона окто-карты глубин - из кбуфера (ручка «Visibility res»): по ней размечены и буфер
    // накопления (visRes² на пробу), и тайл пробы в атласе видимости. uint - вся индексная
    // арифметика вокруг беззнаковая, смешивать типы в условиях циклов ни к чему.
    uint visRes = (uint)ProbeVisRes();

    // Пустых слотов у плотной сетки нет по построению - проба есть в каждом узле, и ранний выход
    // «слот пуст» вместе с запасом пула отсюда ушёл.
    int3 counts = (int3)ProbeGridCounts.xyz;
    int3 scroll = (int3)ProbeGridScroll.xyz;
    int3 storage = ProbeStorageCoords(probe);

    // Координаты УЗЛА - обратный сдвиг прокрутки: узел c лежит по индексу (c + scroll) mod counts,
    // значит c = (storage - scroll) mod counts.
    int3 cell = ProbeWrap(storage, -scroll, counts);

    // ХОЛОДНЫЙ раунд: эта плоскость только что въехала прокруткой, и всё, что накоплено в её
    // текселях, описывает ЧУЖОЕ место - то, откуда объём уехал. Поле принимается целиком,
    // накопители обнуляются: смешивать новую пробу со старой было бы не «плавным переходом», а
    // размазыванием освещения одного угла сцены по другому.
    //
    // Раньше это состояние приезжало таблицей на слот пула, а снималось отдельным прогревающим
    // диспатчем (WarmColdBricks). Теперь оно выводится из трёх чисел кбуфера прямо здесь, в том же
    // диспатче, что и обычная работа, - отдельного прохода и отдельной синхронизации больше нет.
    bool cold = ProbeScrolledIn(storage);

    // СОН ПРОБ (Majercik 2021 §6, упрощённая машина состояний): проба, чьё поле давно спокойно
    // (счётчик calm в _ProbeCounters.w), обновляется раз в 4 раунда - фаза по номеру пробы против
    // фазы веера. Включается ТОЛЬКО в устоявшемся реальном времени (см. RunRound: вес на полу и
    // окно релокации закрыто); смена света поднимает вес раунда и будит всех, движение сцены
    // открывает релокацию - тоже всех, а проснувшаяся проба с изменившимся полем сама обнуляет
    // calm внизу. Экономия - до 3/4 лучей кадра на спокойной сцене.
    int4 countersPrev = cold ? int4(0, 0, 0, 0) : _ProbeCounters[probe];
    int sleepPhase = (int)ProbeRelocation.w;
    if (!cold && sleepPhase > 0 && countersPrev.w > 16 && ((int)probe & 3) != sleepPhase - 1)
    {
        return;
    }

    // Состояние OFF (§6 статьи): проба, оставшаяся В СТЕНЕ после окна релокации (окно закрыто,
    // статистики достаточно, большинство лучей в задние грани), не трассируется вовсе - её
    // валидность и так ноль, выборка её не читает, а лучи она жгла бы вслепую. Движение сцены
    // переоткрывает окно (ProbeRelocation.x > 0), и проба получает новый шанс выбраться.
    if (!cold && ProbeRelocation.z > 0.5 && ProbeRelocation.x == 0.0
        && countersPrev.x >= 64 && countersPrev.z * 2 > countersPrev.x)
    {
        return;
    }

    // Трассируем из АКТУАЛЬНОЙ позиции пробы - с учётом накопленной релокации. Иначе статистика
    // задних граней описывала бы узел сетки, а не то место, где проба реально стоит, и релокация
    // не сошлась бы: сдвинувшись наружу, проба продолжала бы считать себя замурованной.
    float4 offsetSlot = _ProbeOffsets[probe];
    float3 probeOffset = cold ? (float3)0.0 : offsetSlot.xyz;

    // ОКНО РЕЛОКАЦИИ СВЕЖЕЙ ПРОБЫ живёт несколько раундов, а не один, и счётчик этих раундов
    // хранится в МОДУЛЕ .w слота смещений: |w| = 1 + сколько раундов ещё осталось.
    //
    // Место выбрано не от бедности. Само .w занято классификацией (см. probeActive), но она читается
    // ТОЛЬКО по знаку - и здесь, и в пиксельном шейдере, который из этого атласа берёт одни rgb.
    // Модуль, таким образом, свободен, и счётчик влезает в уже существующий ресурс: заводить под
    // него отдельный буфер значило бы вернуть таблицу на пробу, которую эта переделка как раз и
    // сняла. Одного раунда мало по существу: релокация сходится итеративно (Majercik 2021 §5), а
    // въехавшая проба - это ровно случай инициализации, ей нужно всё окно.
    int freshLeft = cold ? PROBE_FRESH_WINDOW - 1 : max((int)abs(offsetSlot.w) - 1, 0);
    float3 probePos = ProbeGridOrigin.xyz + (float3)cell * ProbeGridCell.xyz + probeOffset;

    int rays = (int)ProbeSunDirection.w;
    float tMax = ProbeGridCell.w;
    float sceneEpsilon = ProbeRoundParams.x;
    float visMax = ProbeRoundParams.y;

    // ФИКСИРОВАННЫЕ ЛУЧИ (RTXGI-DDGI, RTXGI_DDGI_NUM_FIXED_RAYS): первые fixedRays направлений
    // веера НЕ вращаются по номеру раунда (см. ProbeGiBaker.FixedRayCount - там же, почему только
    // в реальном времени). Разделение труда ровно как в эталоне:
    //   - фиксированные лучи кормят ГЕОМЕТРИЮ: долю задних граней, ближайший выход наружу,
    //     ближайшую и дальнюю переднюю грань - всё, по чему принимается решение о переезде пробы.
    //     Решение это дискретное, и мерить его дрожащей линейкой нельзя: у пробы на кромке
    //     геометрии доля задних граней гуляет от раунда к раунду просто из-за смены направлений,
    //     проба то уезжает, то возвращается, и каждый переезд сбрасывает накопители;
    //   - остальные лучи кормят РАДИАНС и карту глубин. Фиксированные в оценку не идут именно
    //     потому, что они фиксированные: их направления представлены во времени вдвое чаще
    //     остальных, и подмешивание внесло бы постоянное смещение оценки по этим направлениям
    //     (эталон исключает их из обеих полос - и из радианса, и из глубины, см.
    //     ProbeBlendingCS.hlsl, инициализация rayIndex перед циклом).
    //
    // fixedRays == 0 (запечка) - деления нет вовсе: КАЖДЫЙ луч работает и на геометрию, и на
    // радианс, ровно как раньше. Это и сохраняет сверку с CPU-эталоном луч в луч.
    int fixedRays = (int)ProbeScroll.y;
    int blendRays = max(rays - fixedRays, 1);
    float domega = 4.0 * 3.14159265 / (float)blendRays;

    // КЛАССИФИКАЦИЯ ПРОБ (RTXGI-DDGI, ProbeClassificationCS.hlsl). Проба, вокруг которой в её
    // собственном вокселе нет геометрии, ничего нового за раунд не узнаёт: её поле - это небо и
    // далёкие стены, и оно уже сошлось. Эталон такие пробы гасит целиком; здесь они лишь перестают
    // тратить ОЦЕНОЧНЫЕ лучи, а фиксированные продолжают идти каждый раунд.
    //
    // Так сделано намеренно, и это отход от эталона в безопасную сторону сразу по двум статьям:
    //   1) выборка неактивные пробы НЕ пропускает. У эталона пропуск оправдан тем, что затеняемая
    //      точка всегда лежит в вокселе с геометрией и окружена активными пробами; у нас же поле
    //      кирпичное, с каскадами и уверенностью, и выбивать углы из трилинейки - риск дырок в
    //      освещении ради экономии, которая и так достаётся;
    //   2) неактивная проба продолжает трассировать фиксированные лучи - те самые, по которым
    //      классификация и считается. Без этого она никогда бы не узнала, что рядом ПОЯВИЛАСЬ
    //      геометрия (объект подъехал), и осталась бы выключенной навсегда. Цена самопроверки -
    //      fixedRays лучей вместо rays, то есть та самая экономия и есть: 16 из 128.
    //
    // Состояние живёт в .w слота смещений - единственном свободном канале, который уже ездит и в
    // буфер, и в атлас (выборка читает оттуда только rgb, см. ProbeGiSampleBody).
    //
    // Кодировка ОТРИЦАТЕЛЬНЫМ значением, а не нулём, и это принципиально: буферы заводятся
    // обнулёнными (см. ProbeRoundGpu.ClearBuffers), поэтому ноль обязан значить «ещё не
    // классифицирована», то есть АКТИВНА. Прими мы ноль за «выключена» - на первом же раунде
    // погасли бы разом все пробы объёма, и до фиксированных лучей, которые могли бы их зажечь
    // обратно, дело дошло бы только на следующем.
    // Холодная проба и запечка (fixedRays == 0) активны по тем же соображениям: у первой ещё нет
    // статистики, у второй классификации нет вовсе.
    //
    // И ГЛАВНОЕ УСЛОВИЕ: заморозка разрешена только УСТОЯВШЕЙСЯ пробе - те же ворота, что у сна
    // (устоявшееся реальное время плюс счётчик спокойствия). Без них приём неверен, и это проверено
    // замером: «вокруг нет геометрии» НЕ значит «поле сошлось». Проба посреди двора видит небо и
    // дальние стены - это настоящий свет, и её полю нужны те же десятки раундов, что и любому
    // другому. Замораживая её сразу, мы фиксируем почти чёрное поле второго раунда: на Sponza это
    // дало 40% проб с расхождением 100% и среднюю яркость 0.56 вместо 0.90.
    // У эталона этой беды нет, потому что там классификация решает другую задачу - неактивную пробу
    // он ВЫБРАСЫВАЕТ ИЗ ВЫБОРКИ (Irradiance.hlsl:105), и её поле никого не интересует вовсе.
    // Здесь же выборка неактивные пробы читает (см. выше, почему), значит их поле обязано быть
    // верным - и заморозить его можно только после того, как оно таковым стало.
    bool settled = sleepPhase > 0 && countersPrev.w > 16;
    bool probeActive = cold || fixedRays == 0 || !settled || offsetSlot.w > -0.5;
    int rayEnd = probeActive ? rays : fixedRays;

    float3 sum0 = 0.0, sumX = 0.0, sumY = 0.0, sumZ = 0.0;
    float sunLum = 0.0, totalLum = 0.0;
    int missCount = 0, backCount = 0;

    // Своя статистика задних граней - по ГЕОМЕТРИЧЕСКИМ лучам. Общий backCount остаётся счётчиком
    // по всем лучам: он копится раундами и кормит validity, где важна не устойчивость решения, а
    // точность доли (а фиксированные лучи - такая же равномерная выборка сферы, как и прочие).
    int geomRays = 0, geomBackCount = 0;

    // Классификация: нашёлся ли хоть один геометрический луч, упёршийся в переднюю грань НЕ дальше
    // границы собственного вокселя пробы (см. probeActive и цикл лучей).
    bool nearGeometry = false;

    // Для релокации: ближайшая ЗАДНЯЯ грань - это ближайший выход наружу (если проба внутри
    // геометрии, луч, ушедший в стену, протыкает её изнутри и выходит именно там), а ближайшая
    // передняя - мера того, сколько вокруг свободного места.
    float closestBackT = tMax; float3 closestBackDir = float3(0.0, 1.0, 0.0);
    float closestFrontT = tMax; float3 closestFrontDir = float3(0.0, 1.0, 0.0);
    float farthestFrontT = 0.0; float3 farthestFrontDir = float3(0.0, 1.0, 0.0);
    uint visBase = probe * visRes * visRes;

    // ЗАТУХАНИЕ ГЕОМЕТРИЧЕСКИХ НАКОПИТЕЛЕЙ. Окто-карта глубин и счётчики лучей копятся СУММАМИ, и в
    // запечке это правильно: раундов конечное число, сцена не шевелится, а точные суммы дают сверку
    // с CPU-эталоном. В реальном времени раунд идёт каждый кадр и суммы не кончаются никогда -
    // после N раундов свежая оценка сдвигает среднее на 1/N, то есть через десять секунд на 0.2%, а
    // через минуту на 0.03%. Геометрия пробы оказывается ЗАМОРОЖЕНА по стартовой сцене:
    //   - тест Чебышёва меряет расстояния до стен, которых там уже нет (протечки и, наоборот,
    //     призрачная тень от уехавшего объекта);
    //   - validity держит замурованной пробу, из-под которой объект уже уехал, и наоборот;
    //   - skyVis показывает старую долю неба.
    // Поле SH при этом затухает нормально (вес раунда alpha), и вот это рассогласование - радианс
    // адаптируется, а геометрия под ним нет - и есть источник «свет остался лежать там, где объекта
    // уже нет». Релокация не лечит: она сбрасывает накопители только ПЕРЕЕХАВШЕЙ пробе, а
    // неподвижная проба рядом с уехавшей стеной держит старую глубину бесконечно.
    //
    // Лечится экспоненциальным затуханием: сумма становится скользящей с окном
    // PROBE_GEOMETRY_WINDOW раундов. Окно СВОЁ, не alpha поля, и заметно длиннее: геометрия
    // малошумна по сравнению с радиансом (глубина луча - это одно число, а не оценка интеграла), и
    // ей нужна долгая память, иначе карта глубин у 8x8 текселей начинает дрожать. 64 раунда - это
    // около секунды при раунде на кадр: столько и должно занимать «объект уехал - затенение
    // обновилось». Средние при этом не смещаются вовсе: и сумма, и счётчик умножаются на один
    // множитель, отношение сохраняется точно.
    //
    // Затухание стоит ЗДЕСЬ, после ранних выходов (сон, состояние OFF): проба, которая этот раунд
    // не трассирует, ничего и не накапливает - затухать ей не с чего, иначе спящая проба за свои
    // три пропущенных раунда просто растеряла бы статистику.
    // Затухание идёт только там, где в этом же раунде накопители и ПОПОЛНЯЮТСЯ. У неактивной пробы
    // (классификация, см. probeActive) оценочных лучей нет, карту глубин пополнять нечем - затухай
    // она вхолостую, счётчик сэмплов в текселях сполз бы к нулю, и карта развалилась бы на пустые
    // октанты у пробы, с которой всё в порядке.
    float geometryDecay = ProbeRelocation.z > 0.5 && probeActive
        ? 1.0 - 1.0 / PROBE_GEOMETRY_WINDOW
        : 1.0;

    // Окто-карта глубин копится суммами, поэтому у холодной пробы её надо именно ОБНУЛИТЬ, а не
    // пересилить весом: суммы прежнего жильца слота никаким весом не разбавляются, и тест Чебышёва
    // мерил бы глубины от точки, где этой пробы никогда не было.
    if (cold)
    {
        [loop]
        for (uint c = 0; c < visRes * visRes; c++)
        {
            _ProbeVisibility[visBase + c] = float4(0.0, 0.0, 0.0, 0.0);
        }

        // Смещение релокации - тоже наследство прежнего жильца, и его надо снять ЗДЕСЬ, а не в
        // блоке релокации внизу: тот работает только при открытом окне, а с закрытым проба так и
        // осталась бы отодвинутой от своего узла в сторону чужой стены.
        // Модуль .w несёт остаток окна релокации (см. freshLeft): въехавшей пробе оно открыто целиком.
        _ProbeOffsets[probe] = float4(0.0, 0.0, 0.0, (float)(1 + freshLeft));
    }
    else if (geometryDecay < 1.0)
    {
        [loop]
        for (uint c = 0; c < visRes * visRes; c++)
        {
            _ProbeVisibility[visBase + c] = _ProbeVisibility[visBase + c] * geometryDecay;
        }

        // Счётчики - целые, и затухание их подтачивает усечением. На больших значениях это ничто
        // (в установившемся режиме x = rays * окно, то есть тысячи), а малые счётчики усечение
        // гасит быстрее номинального окна - и это ровно та сторона, в которую ошибаться безопасно:
        // редкие задние грани должны забываться, а проба, реально сидящая в стене, набирает их
        // сотнями за раунд, где усечение неразличимо. Спокойствие (.w) НЕ трогаем: это счётчик
        // раундов для сна, а не выборочная статистика.
        countersPrev.xyz = (int3)((float3)countersPrev.xyz * geometryDecay);
    }

    // В РЕАЛЬНОМ ВРЕМЕНИ веер пробы повёрнут ПЛАВНО ПО ПРОСТРАНСТВУ - малый градиент фазы от
    // координат ячейки. Общий веер корректен для запечки (сверка с CPU луч в луч), но коррелирует
    // ошибку всех проб - поле ходит пятнами. Крайность в другую сторону тоже проверена и ХУЖЕ:
    // фаза от НОМЕРА пробы через золотое сечение даёт соседям по X сдвиг ~222°, почти противофазу -
    // соседние пробы смотрят в противоположные стороны, и «независимый шум» оборачивается честной
    // шахматной решёткой. Плавный градиент - середина: соседи почти согласованы (поле гладкое),
    // корреляция спадает за несколько ячеек (пятен размером с комнату больше нет).
    float fanAngle = ProbeRelocation.z > 0.5
        ? frac(dot((float3)cell, float3(0.0731, 0.0937, 0.1181))) * 6.2831853
        : 0.0;
    float fanSin = sin(fanAngle);
    float fanCos = cos(fanAngle);

    [loop]
    for (int r = 0; r < rayEnd; r++)
    {
        float3 dir = _ProbeRayDirections[r].xyz;
        dir = float3(fanCos * dir.x + fanSin * dir.z, dir.y,
                     fanCos * dir.z - fanSin * dir.x);
        SceneHit hit = SceneTraceClosest(probePos, dir, tMax);

        // Роль луча (см. шапку цикла): фиксированный работает на геометрию, остальные - на оценку.
        // При fixedRays == 0 (запечка) деления нет, и геометрию собирает каждый луч, как раньше.
        bool isFixed = r < fixedRays;
        bool geometryRay = isFixed || fixedRays == 0;

        if (geometryRay)
        {
            geomRays++;
        }

        if (geometryRay && hit.hit)
        {
            if (hit.backface)
            {
                // Луч вышел изнутри геометрии - проба в стене. Ближайшая задняя грань и есть её
                // ближайший выход наружу.
                //
                // Дистанция здесь ПОЛНАЯ, не укороченная. Укорачивание на 80% (Majercik 2021,
                // §4.1) - приём для КАРТЫ ГЛУБИН, оно ниже по коду и релокации не касается: по
                // укороченной проба вышла бы из стены на пятую часть нужного пути и осталась
                // внутри.
                geomBackCount++;
                if (hit.t < closestBackT)
                {
                    closestBackT = hit.t;
                    closestBackDir = dir;
                }
            }
            else
            {
                if (hit.t < closestFrontT)
                {
                    closestFrontT = hit.t;
                    closestFrontDir = dir;
                }

                if (hit.t > farthestFrontT)
                {
                    farthestFrontT = hit.t;
                    farthestFrontDir = dir;
                }

                // Классификация (эталон, ProbeClassificationCS.hlsl): попадание считается «рядом»,
                // если оно ближе, чем луч вышел бы из собственного вокселя пробы. У эталона это
                // выписано через нормали и скалярные произведения трёх плоскостей, но сводится к
                // выходу луча из коробки полуразмером в шаг сетки: по каждой оси граница на
                // расстоянии spacing/|dir|, берётся ближайшая. Шаг у плотной сетки один на весь
                // объём - множителя по уровню подразделения, под который это подстраивалось, больше
                // нет.
                float3 spacing = ProbeGridCell.xyz;
                float3 planeT = spacing / max(abs(dir), 1e-6);
                if (hit.t <= min(planeT.x, min(planeT.y, planeT.z)))
                {
                    nearGeometry = true;
                }
            }
        }

        // Фиксированный луч свою работу сделал. Ни в радианс, ни в карту глубин он не идёт, а
        // значит и затенять точку попадания незачем - это теневой луч плюс сбор поля проб на
        // каждое попадание, самая дорогая часть раунда.
        if (isFixed)
        {
            if (!hit.hit)
            {
                missCount++;
            }
            else if (hit.backface)
            {
                backCount++;
            }

            continue;
        }

        float3 radiance = 0.0;
        float sunShare = 0.0;
        float hitT = tMax;

        if (!hit.hit)
        {
            radiance = ProbeSampleSky(dir);
            missCount++;
        }
        else
        {
            hitT = hit.t;
            if (hit.backface)
            {
                backCount++;

                // Глубина такого луча УКОРАЧИВАЕТСЯ на 80% (Majercik 2021, §4.1: "probe-update
                // rays that hit backfaces record a value of 0 for irradiance and shorten their
                // depth values by 80%"). Смысл: тест Чебышёва должен считать заднюю грань
                // заслоняющей, а не освещать её. Записав полную дистанцию, проба заявляет, что
                // видит в эту сторону далеко, и свет протекает сквозь стену.
                //
                // Именно укорачивание, а не обнуление, и авторы объясняют почему: ноль загнал бы
                // вес Чебышёва в ноль, а после нормировки весов он мог бы, наоборот, подскочить;
                // к тому же проба, задевшая пару задних граней по огрехам моделирования, но в
                // стене не сидящая, получила бы полностью перекошенную среднюю глубину.
                hitT *= 0.2;
            }
            else
            {
                // Кэш поверхностей: у точки попадания уже есть готовый радианс, посчитанный на
                // своём разрешении. Одно чтение вместо тридцати двух разбросанных - на GPU это
                // Кэш поверхностей: у точки попадания уже есть готовый радианс, посчитанный на
                // своём разрешении. Одно чтение вместо тридцати двух разбросанных - на GPU это
                // главная статья экономии, ровно поэтому Lumen и держит кэш вместо пересбора поля
                // проб в каждой точке попадания.
                //
                // В РЕАЛЬНОМ ВРЕМЕНИ кэш выключен (ProbeRelocation.z, см. RunRound): его геометрия
                // захвачена один раз и на движущейся сцене ВРЁТ - отражённый свет шёл бы от старой
                // позы объекта. Отскок собирается веткой ниже: прямое солнце в точке попадания
                // плюс поле проб прошлого раунда - это честно к движению и даёт бесконечный
                // переотскок, ценой детализации сетки вместо детализации поверхности.
                int surfaceVoxel = ProbeRelocation.z > 0.5
                    ? -1
                    : SurfaceLookup(hit.position + hit.normal * ProbeRoundParams.z);
                if (surfaceVoxel >= 0)
                {
                    float4 cached = _SurfaceRadiance[surfaceVoxel];
                    radiance = cached.rgb;
                    sunShare = cached.a;
                }
                else
                {
                    // Кэша тут нет (воксель не захвачен) - считаем отскок по-старому, из поля проб.
                    float3 sunIrradiance = 0.0;
                    float ndotl = dot(hit.normal, ProbeSunDirection.xyz);
                    if (ndotl > 0.0 &&
                        !SceneTraceAnyHit(hit.position + hit.normal * (sceneEpsilon * 4.0),
                                          ProbeSunDirection.xyz, tMax))
                    {
                        sunIrradiance = ProbeSunColor.rgb * ndotl;
                    }

                    float3 prevIrradiance = 0.0;
                    float prevFrac = 0.0;
                    float feedback = ProbeRoundParams.w;
                    if (feedback > 0.0)
                    {
                        prevIrradiance = ProbeGatherIrradiance(
                            hit.position + hit.normal * ProbeRoundParams.z, hit.normal, prevFrac) * feedback;
                    }

                    float3 irradiance = sunIrradiance + prevIrradiance;

                    // Хрома-кламп альбедо: цвет тянется к собственной люме, яркость не меняется
                    // (lerp к люме линеен) - зеркало CPU-версии.
                    float3 albedo = lerp((float3)ProbeLuminance(hit.albedo), hit.albedo, ProbeGridCounts.w);
                    radiance = albedo * irradiance * (1.0 / 3.14159265);

                    // Солнечная доля яркости луча: прямой вклад плюс солнечная часть собранного
                    // поля (переотскок наследует долю источника).
                    float lumIrr = ProbeLuminance(irradiance);
                    sunShare = lumIrr > 1e-6
                        ? (ProbeLuminance(sunIrradiance) + ProbeLuminance(prevIrradiance) * prevFrac) / lumIrr
                        : 0.0;
                }
            }
        }

        // Подавление выбросов: редкий луч в очень яркое (диск солнца в панораме) двигает пробу
        // целиком, и с числом лучей это не сходится - редкое событие остаётся редким. Гасится
        // только в реальном времени, в запечке потолок нулевой (см. ProbeGiBakeSession.MaxRayLuminance).
        float maxRayLum = ProbeChunk.z;
        if (maxRayLum > 0.0)
        {
            float rayLum = ProbeLuminance(radiance);
            if (rayLum > maxRayLum)
            {
                // Масштабируем, а не обрезаем по каналам: обрезание по каналам увело бы цвет.
                radiance *= maxRayLum / rayLum;
            }
        }

        // Окто-карта глубин: кламп по масштабу ячейки, иначе промах вносит дистанцию в несколько
        // габаритов сцены и тест Чебышёва не срабатывает никогда.
        // Луч размазывается по КОНУСУ текселей, а не кладётся в один ближайший (§4.4 статьи:
        // "warping them according to a cosine-power lobe distribution", тексели с весом ниже 0.001
        // не трогаются). Это не косметика: текселей 64 и лучей за раунд столько же, поэтому при
        // укладке в один тексель большинству октантов не достаётся НИ ОДНОГО сэмпла, и их
        // приходится заполнять средним по всей пробе. Тест Чебышёва - главное средство статьи
        // против протечек - работал бы тогда по карте, которой почти нет.
        float tv = min(hitT, visMax);
        [loop]
        for (uint dt = 0; dt < visRes * visRes; dt++)
        {
            float2 texelUv = (float2(dt % visRes, dt / visRes) + 0.5)
                           / (float)visRes;
            float w = max(0.0, dot(ProbeOctDecode(texelUv), dir));

            [unroll]
            for (int s = 0; s < PROBE_DEPTH_SHARPNESS_SQUARINGS; s++)
            {
                w *= w;
            }

            if (w < PROBE_DEPTH_WEIGHT_EPSILON)
            {
                continue;
            }

            _ProbeVisibility[visBase + dt] += float4(tv * w, tv * tv * w, w, 0.0);
        }

        float lum = ProbeLuminance(radiance);
        sunLum += lum * sunShare;
        totalLum += lum;

        sum0 += radiance * (SH_Y00 * domega);
        sumX += radiance * (SH_Y1 * dir.x * domega);
        sumY += radiance * (SH_Y1 * dir.y * domega);
        sumZ += radiance * (SH_Y1 * dir.z * domega);
    }

    // Неактивная проба (классификация): оценочных лучей в этом раунде не было, обновлять поле
    // нечем и незачем - вокруг открытое место, накопленное значение и есть верное. Пишем только
    // состояние: фиксированные лучи мы всё-таки пустили, и именно они могли обнаружить, что рядом
    // ПОЯВИЛАСЬ геометрия. Следующий раунд такая проба отработает полностью.
    // Атласы, счётчики и карта глубин остаются от прошлого раунда нетронутыми - у них по-прежнему
    // валидное содержимое, а не устаревшее.
    if (!probeActive)
    {
        // Счётчик свежести тикает и на этой ветке: замороженная проба обязана дожить своё окно, а не
        // застрять в нём навсегда (см. freshLeft).
        _ProbeOffsets[probe] = float4(probeOffset,
            (nearGeometry ? 1.0 : -1.0) * (float)(1 + max(freshLeft - 1, 0)));

        // В общую изменчивость такая проба входит нулём с ПОЛНЫМ весом, а не нулевым: она
        // действительно неизменна - её ровно за устоявшееся поле и заморозили. Исключи мы её из
        // среднего, итог считался бы только по оставшимся беспокойным пробам и никогда бы не упал
        // ниже порога: чем больше проб успокоилось, тем хуже выглядел бы объём.
        _ProbeVariability[probe] = float2(0.0, 1.0);
        return;
    }

    // ПЕРЕЕХАВШАЯ ПРОБА НАЧИНАЕТ ПОЛЕ ЗАНОВО.
    //
    // Релокация уже сбрасывает переехавшей пробе накопленную ГЕОМЕТРИЮ - счётчики лучей и окто-карту
    // глубин (см. блок релокации внизу): они намерены из старой точки и там прямо сказано, что иначе
    // выбравшаяся из стены проба продолжала бы числиться замурованной. А ПОЛЕ при этом оставалось
    // копиться дальше, как ни в чём не бывало, - хотя описывает ровно ту же старую точку.
    //
    // На прокрутке это и вылезает. Свежему слоту релокация открыта всё окно свежести
    // (freshLeft > 0), то есть проба, приведённая прокруткой, первые раунды ЕЗДИТ. Её поле
    // в это время - смесь замеров из разных точек пространства, и чем дольше копится, тем сильнее
    // размазано. Перед летящей камерой, где прокрутка идёт непрерывно, это читается как полоса,
    // которая "сквозит" - показывает освещение соседнего места.
    // (Проверено от обратного: попытка УСРЕДНИТЬ оценки по окну свежести сделала картину заметно
    // хуже - она усиливала ровно это смешение. См. комментарий ниже, там разбор.)
    //
    // Признак переезда брать неоткуда, кроме как из самого сброса: релокация обнуляет счётчики, и
    // ноль в них у некольдовой пробы означает ровно "прошлый раунд её переселил". Отдельного флага
    // это не требует, а расходиться со сбросом не может по построению - источник один.
    //
    // Только в реальном времени. В запечке переезд случается один раз на инициализации, поле там
    // ещё пустое, и лечить нечего; зато CPU-путь такого сброса не делает, и разойтись с ним нельзя -
    // на этой сверке держится вся проверка GPU-обхода.
    bool justRelocated = !cold && ProbeRelocation.z > 0.5 && countersPrev.x == 0;

    // Вес раунда - бегущее среднее, посчитанное на CPU (разгонные раунды кладутся целиком).
    float alpha = (cold || justRelocated) ? 1.0 : ProbeGridOrigin.w;

    // Усреднения оценок свежей пробы по окну свежести здесь НЕТ, и это проверено на живой сцене.
    // Идея была очевидной: холодный раунд принимает одну шумную оценку целиком, кирпич проявляется
    // вместе с её разбросом, значит надо копить среднее за те раунды, пока он всё равно скрыт.
    // Арифметически верно, а на деле стало ХУЖЕ - блоки перед летящей камерой поплыли сильнее.
    //
    // Причина в том, что окно свежести - это ТО ЖЕ САМОЕ окно, в котором пробе открыта релокация
    // (freshLeft > 0, счётчик лежит в модуле .w слота смещений). Всё это время
    // проба ездит, и усреднять её оценки значит смешивать замеры из РАЗНЫХ ТОЧЕК пространства -
    // тем сильнее, чем больше раундов в среднее положить.
    //
    // Чинить это усреднением нельзя в принципе, пока релокация и свежесть делят одно окно. Развязать
    // их - отдельная задача: либо копить только после того, как проба встала, либо сбрасывать
    // накопление на каждый её переезд.
    uint fieldBase = probe * 4;

    // Предыдущее значение берётся из ЧИТАЮЩЕГО буфера. Взять его из _ProbeField (в который раунд
    // сейчас пишет) - значит при пинг-понге смешаться с полем ПОЗАПРОШЛОГО раунда: накопление
    // распадается на две независимые цепочки, чётную и нечётную, у каждой свой веер лучей и свой
    // шум, а атлас достаётся той, что отработала последней. На весах запечки (alpha -> 0.02) обе
    // цепочки сходятся к одному среднему, и это незаметно; в реальном времени (alpha 0.15) каждая
    // держит свои 15% свежего шума - поле мигает между ними через кадр.
    float4 prev0 = _ProbeFieldRead[fieldBase + 0];
    float4 prev1 = _ProbeFieldRead[fieldBase + 1];
    float4 prev2 = _ProbeFieldRead[fieldBase + 2];
    float4 prev3 = _ProbeFieldRead[fieldBase + 3];

    // .w = счётчик спокойных раундов для сна (обновляется после смешивания, см. ниже).
    int4 counters = countersPrev + int4(rays, missCount, backCount, 0);
    _ProbeCounters[probe] = counters;

    float invTotal = 1.0 / max((float)counters.x, 1.0);
    float skyVis = (float)counters.y * invTotal;
    // Проба в стене видит в основном задние грани - гасим её вес в интерполяции.
    float validity = saturate(1.0 - (float)counters.z * invTotal * 3.0);
    float roundSunFrac = totalLum > 1e-6 ? saturate(sunLum / totalLum) : 0.0;

    // Пер-пробной адаптивной альфы (параграф 4.3 статьи) здесь НЕТ, и это выстраданное решение:
    // пробовали, пороги 25%/80% с полом по яркости сцены и фильтром стабильности - всё равно
    // хвосты шума цепляют порог у случайных проб, каждая делает шаг на порядок крупнее соседей,
    // и поле идёт ПЯТНАМИ (проверено глазами на Sponza). Быстрый отклик на события даёт
    // ГЛОБАЛЬНЫЙ откат веса объёма: смена света - SetLighting, движение геометрии -
    // ReopenRelocation; оба откатывают Round, и вес всплывает у ВСЕХ проб разом - краткий
    // равномерный шум вместо пятен.
    float alphaEff = alpha;

    // «Hysteresis 0 для свежепереехавших» из §6 здесь НЕ реализован, и это выстрадано: у авторов
    // он опирается на отдельный проход сходимости с БОЛЬШИМ пакетом лучей, а полный приём одной
    // шумной оценки из 128 лучей дал хвост p99 64% (замерено). Переехавшая проба сходится обычным
    // весом - при переезде из стены её старое поле хотя бы того же порядка яркости.

    float4 out0 = float4(lerp(prev0.rgb, sum0, alphaEff), skyVis);
    float4 out1 = float4(lerp(prev1.rgb, sumX, alphaEff), validity);
    float4 out2 = float4(lerp(prev2.rgb, sumY, alphaEff), lerp(prev2.a, roundSunFrac, alphaEff));
    float4 out3 = float4(lerp(prev3.rgb, sumZ, alphaEff), 1.0);

    // Перцептивное накопление (адаптация Majercik 2021 §4.2 к SH, см. RealtimeGamma): поле хранится
    // линейно, но яркость движется по кривой pow(lerp(old^(1/g), new^(1/g), a), g) - светлячок
    // давится примерно в a^(g-1) раз, переход свет→тень ускоряется. Один множитель на все полосы:
    // направленность поля не трогаем, гнётся только траектория яркости.
    float accumGamma = ProbeRelocation.y;
    if (accumGamma > 1.0 && alphaEff < 1.0)
    {
        float lumOld = ProbeLuminance(prev0.rgb);
        float lumNew = ProbeLuminance(sum0);
        float lumLinear = ProbeLuminance(out0.rgb);

        // Гамма - ТОЛЬКО на потемнение. Симметричная кривая душила подъём из темноты в ~a^(g-1)
        // раз (от чистого нуля - в a^g: тёмный коридор так и оставался чёрным при любом Ambient
        // boost), а её анти-светлячковую работу на подъёме и так делает предел шага. Быстрое
        // перцептивно-линейное потемнение - то, ради чего кривая в статье и введена - остаётся.
        if (lumNew < lumOld && lumLinear > 1e-6)
        {
            float invGamma = 1.0 / accumGamma;
            float lumPerceptual = pow(
                lerp(pow(max(lumOld, 0.0), invGamma), pow(max(lumNew, 0.0), invGamma), alphaEff),
                accumGamma);
            float k = lumPerceptual / lumLinear;
            out0.rgb *= k;
            out1.rgb *= k;
            out2.rgb *= k;
            out3.rgb *= k;
        }
    }

    // Ограничитель скорости: пробе запрещено менять яркость больше чем на maxStep за раунд.
    // Вес раунда - фильтр, ОДИНАКОВЫЙ для всех проб, и его приходится ставить по худшей; здесь же
    // спокойные пробы не задеты вовсе, а буйная перестаёт вспыхивать и начинает переползать.
    // Установившееся значение не смещается: режется производная, а не величина.
    float maxStep = ProbeChunk.w;
    if (maxStep > 0.0 && alphaEff < 1.0)
    {
        float3 delta = out0.rgb - prev0.rgb;
        float deltaLen = length(delta);

        // Масштаб по ПОЛУСУММЕ старого и нового: от одного лишь старого проба, стоящая в нуле
        // (свет ещё не дошёл), не смогла бы тронуться с места вовсе.
        float scale = 0.5 * (length(prev0.rgb) + length(out0.rgb)) + 1e-4;
        float limit = maxStep * scale;

        // Минимального АБСОЛЮТНОГО шага на потемнение (RTXGI-DDGI, ProbeBlendingCS.hlsl:544-549:
        // "When darkening, step at least the minimum value the texture format can represent") здесь
        // сознательно НЕТ, хотя болезнь, от которой он лечит, есть и у нас: допустимый шаг
        // пропорционален текущей яркости, при потемнении она съёживается вместе с шагом, и проба
        // идёт к нулю геометрической прогрессией со знаменателем (1 - maxStep) - хвост длиной в
        // полторы сотни раундов.
        //
        // Приём не переносится, потому что у эталона он выведен из РАЗРЯДНОСТИ формата (1/1024 при
        // 10 битах на канал): там это ровно одна ступенька квантования, то есть заведомо
        // неразличимая величина. Наши атласы float32, ступенек у них нет, и та же константа
        // становится просто абсолютным полом в линейных единицах яркости - для тусклой пробы это
        // шаг в проценты от её собственной величины. Замерено на Sponza: пол 1/1024 поднял хвост
        // мерцания max с 3% до 9% при неизменных p50/p90. Быстрое потемнение здесь и так делает
        // перцептивная гамма выше (accumGamma), она относительная и этим пороком не страдает.
        if (deltaLen > limit)
        {
            // Один множитель на все четыре полосы SH: масштабировать их порознь значило бы менять
            // направленность поля, а не только его яркость.
            float k = limit / deltaLen;
            out0.rgb = prev0.rgb + (out0.rgb - prev0.rgb) * k;
            out1.rgb = prev1.rgb + (out1.rgb - prev1.rgb) * k;
            out2.rgb = prev2.rgb + (out2.rgb - prev2.rgb) * k;
            out3.rgb = prev3.rgb + (out3.rgb - prev3.rgb) * k;
        }
    }

    // Счётчик спокойствия для сна: относительная смена яркости за раунд ниже процента - раунд
    // спокойный. Порог ЗАМЕТНО ниже предела шага (3%), иначе проба, которую ограничитель ещё
    // ведёт к цели, уснула бы на полпути.
    // Спокойствие копится ВСЕГДА в реальном времени, не только при включённом сне: его читает и
    // адаптивная альфа (стабильность до события - её фильтр против осцилляторов).
    if (ProbeRelocation.z > 0.5)
    {
        float lumPrev = ProbeLuminance(prev0.rgb);
        float lumOut = ProbeLuminance(out0.rgb);
        float rel = abs(lumOut - lumPrev) / (0.5 * (lumPrev + lumOut) + 1e-4);

        // Спокойствие - это не только «мало меняюсь», но и «уже ПРИШЁЛ»: поле обязано совпадать со
        // свежей оценкой раунда в пределах её шума. Без этой проверки проба, медленно ползущая к
        // свету (маленький вес, ограничитель, длинный хвост мультибаунса), меняется меньше процента
        // за раунд и засыпает на полпути - тёмные интерьеры замерзали тёмными.
        float lumEst = ProbeLuminance(sum0);
        float relEst = abs(lumOut - lumEst) / (0.5 * (lumOut + lumEst) + 1e-4);
        counters.w = rel < 0.01 && relEst < 0.25 ? min(counters.w + 1, 255) : 0;
        _ProbeCounters[probe] = counters;
    }

    // Изменчивость пробы - коэффициент вариации её яркости (эталон, ProbeBlendingCS.hlsl:552-562).
    // Оценка дисперсии берётся в форме Уэлфорда: произведение отклонений свежей выборки от СТАРОГО
    // и от НОВОГО среднего. Форма выбрана не для красоты - она несмещённая при бегущем среднем и,
    // в отличие от квадрата одного отклонения, не требует хранить историю.
    //
    // Делим на среднее, получая безразмерную величину: только так пробы тёмного интерьера и
    // залитого солнцем двора сравнимы между собой и их можно осмысленно усреднять по объёму.
    // У самых тёмных проб отношение вырождается (делить почти на ноль), поэтому ниже порога
    // яркости изменчивость считается нулевой - шум в темноте всё равно невидим.
    //
    // Вес нулевой у проб В СТЕНЕ: их поле никого не интересует (валидность около нуля, выборка их
    // почти не читает), а лучи у них мечутся между задними гранями и небом за краем - самая
    // беспокойная статистика в объёме. Учитывай мы их, средняя изменчивость никогда бы не сошлась.
    {
        float lumSample = ProbeLuminance(sum0);
        float lumPrevMean = ProbeLuminance(prev0.rgb);
        float lumMean = ProbeLuminance(out0.rgb);
        float sigma2 = (lumSample - lumPrevMean) * (lumSample - lumMean);
        float cov = lumMean > 1e-3 ? sqrt(max(sigma2, 0.0)) / lumMean : 0.0;
        _ProbeVariability[probe] = float2(cov, validity > 0.05 ? 1.0 : 0.0);
    }

    _ProbeField[fieldBase + 0] = out0;
    _ProbeField[fieldBase + 1] = out1;
    _ProbeField[fieldBase + 2] = out2;
    _ProbeField[fieldBase + 3] = out3;

    // Релокация: проба, стоящая внутри стены или колонны, отодвигается наружу. Это главное лекарство
    // густой сетки - чем мельче ячейка, тем больше проб оказывается замуровано, а такая проба и
    // мигает (её лучи мечутся между задними гранями и небом за краем), и течёт светом сквозь стену.
    // У СВЕЖЕЙ пробы (её плоскость въехала прокруткой) окно релокации СВОЁ: общесеточное открывать
    // на каждый сдвиг камеры нельзя - Majercik 2021 §5 двигает пробы только на инициализации, а для
    // новых проб этот сдвиг инициализацией и является.
    float relocLimit = freshLeft > 0
        ? max(ProbeRelocation.x, ProbeScroll.x)
        : ProbeRelocation.x;
    bool relocated = false;
    if (relocLimit > 0.0)
    {
        // Доля задних граней - по ГЕОМЕТРИЧЕСКИМ лучам (фиксированным, если они есть). Именно ради
        // устойчивости этой доли фиксированные лучи и заведены: пороги ниже дискретные, и решение
        // «проба в стене» не должно менять знак от того, что веер повернулся.
        float backFrac = (float)geomBackCount / (float)max(geomRays, 1);
        float3 newOffset = probeOffset;
        float offsetLen = length(probeOffset);

        float minCell = min(ProbeGridCell.x, min(ProbeGridCell.y, ProbeGridCell.z));

        // Минимальный просвет до передней грани - тот же порог, что у эталона зовётся
        // probeMinFrontfaceDistance: ближе него проба стоять не должна ни при каком раскладе.
        float minFrontface = 0.3 * minCell;
        if (backFrac > 0.25 && closestBackT < tMax)
        {
            // Внутри геометрии. Ближайшая задняя грань и есть ближайший выход - шагаем прямо за
            // неё, с запасом в тот же отступ, которым пользуются теневые лучи.
            newOffset = probeOffset + closestBackDir * (closestBackT + ProbeRoundParams.z);
        }
        else if (closestFrontT < minFrontface && dot(closestFrontDir, farthestFrontDir) < 0.5)
        {
            // Вторая ветка оптимизатора статьи (Listing 4): проба НЕ в стене, но прижата к
            // поверхности - половина лучей бьёт в упор, оценка кипит, тест Чебышёва работает на
            // разрыве. Отходим малым шагом К САМОЙ ДАЛЬНЕЙ передней грани (= в открытое
            // пространство); условие несонаправленности - страховка статьи от пробы, видящей одну
            // единственную поверхность (дальняя = ближняя, отходить некуда).
            newOffset = probeOffset + farthestFrontDir * min(0.2 * minCell, farthestFrontT);
        }
        else if (closestFrontT > minFrontface && offsetLen > 1e-5)
        {
            // Третья ветка эталона (RTXGI-DDGI, ProbeRelocationCS.hlsl: "Probe isn't near anything,
            // try to move it back towards zero offset"). Вокруг просторно - возвращаем пробу к её
            // узлу сетки: смещённая проба ломает трилинейную интерполяцию, и держать смещение,
            // когда его причина исчезла (объект уехал, окно релокации переоткрылось), незачем.
            //
            // Раньше этой ветки здесь не было, и отвергнута она была по делу: без ограничения шага
            // проба у тонкого пола качалась - возврат протыкал пол, следующий раунд выталкивал
            // обратно, и каждый переезд сбрасывал накопители (мигание в шахматном узоре геометрии).
            // Ограничение эталона ровно от этого и страхует: шаг назад не больше, чем
            // closestFrontT - minFrontface, то есть проба НИКОГДА не подходит к ближайшей передней
            // грани ближе минимального просвета и протыкнуть её не может по построению. Второе
            // слагаемое клампа - length(offset): дальше узла возвращаться некуда.
            float moveBack = min(closestFrontT - minFrontface, offsetLen);
            newOffset = probeOffset - (probeOffset / offsetLen) * moveBack;
        }

        // Дальше предела уходить нельзя: покинув свою ячейку, проба сломает трилинейную
        // интерполяцию сильнее, чем выигрывает от того, что выбралась из стены.
        //
        // Предел ЭЛЛИПСОИДНЫЙ, по осям шага сетки (эталон, ProbeRelocationCS.hlsl:223-230), а не
        // изотропный по длине: у анизотропной сетки (высокая комната, плоский каскад) шаг по Y
        // может быть вдвое больше, чем по XZ, и общий предел в мировых единицах либо запирает
        // пробу по короткой оси, либо выпускает её из ячейки по длинной.
        //
        // И при выходе за предел смещение НЕ масштабируется, а отбрасывается целиком - старое
        // остаётся в силе. Масштабирование давало направление, не отвечающее ни одной из веток
        // выше: укороченный прыжок из стены не выводит наружу (проба остаётся замурованной, но уже
        // не в узле), а укороченный отход от грани не набирает нужного просвета. Отказ от шага
        // честнее: причина никуда не делась, и следующий раунд попробует снова.
        float3 normalizedOffset = newOffset / max(ProbeGridCell.xyz, 1e-6);
        float relocLimitCells = relocLimit / max(minCell, 1e-6);
        if (dot(normalizedOffset, normalizedOffset) > relocLimitCells * relocLimitCells)
        {
            newOffset = probeOffset;
        }

        // Проба ПЕРЕЕХАЛА - её накопленная геометрия описывает старое место и подлежит сбросу.
        // Счётчики задних граней и окто-карта глубин копятся точными суммами по ВСЕМ раундам, и без
        // сброса переезд получался бы половинчатым: радианс проба считала бы уже с нового места, а
        // валидность так и осталась бы заниженной старой статистикой (то есть выбравшаяся из стены
        // проба продолжала бы числиться замурованной), и тест Чебышёва мерил бы глубины от точки,
        // где пробы больше нет.
        //
        // Сброс - ТОЛЬКО при выходе из стены (крупный прыжок): там старая статистика намерена
        // изнутри геометрии и вредна. Малый шаг-отход от передней грани (0.2 ячейки) накопители
        // НЕ сбрасывает - поле оттуда почти валидно, а сброс тысяч приповерхностных проб на
        // плотной сетке давал волну холодных стартов (замерено: p99 64% вместо 3%).
        relocated = backFrac > 0.25 && length(newOffset - probeOffset) > relocLimit * 0.1;

        probeOffset = newOffset;
    }

    // Состояние классификации (см. probeActive): в .w слота смещений. Пишется ОДНОЙ строкой на все
    // ветки - и когда релокация выключена, и когда проба не переезжала: иначе проба с закрытым
    // окном релокации никогда бы своё состояние не обновила. В запечке (fixedRays == 0)
    // классификации нет, состояние всегда активное.
    // Модуль несёт остаток окна релокации свежей пробы (см. freshLeft выше) - раунд его тикает,
    // а знак, который единственный кто-либо читает, остаётся прежним.
    int freshNext = max(freshLeft - 1, 0);
    float probeState = ((fixedRays == 0 || nearGeometry) ? 1.0 : -1.0) * (float)(1 + freshNext);
    _ProbeOffsets[probe] = float4(probeOffset, probeState);

    // Те же значения сразу в атласы - материалы читают их без участия CPU.
    uint2 texel = ProbeAtlasTexel(probe);
    _ProbeAtlasOffset[texel] = float4(probeOffset, probeState);
    _ProbeAtlasSh0[texel] = out0;
    _ProbeAtlasSh1[texel] = out1;
    _ProbeAtlasSh2[texel] = out2;
    _ProbeAtlasSh3[texel] = out3;

    // Окто-блок видимости: средние по накопленным суммам. Пустые октанты (лучей туда за все
    // раунды не попало) заполняются средним по пробе - зеркало ProbeGiBaker.Snapshot.
    float totalT = 0.0;
    float totalCount = 0.0;
    [loop]
    for (uint i = 0; i < visRes * visRes; i++)
    {
        float3 acc = _ProbeVisibility[visBase + i].xyz;
        totalT += acc.x;
        totalCount += acc.z;
    }

    float meanAll = totalCount > 0.0 ? totalT / totalCount : 0.0;
    uint2 visTexelBase = texel * (uint)visRes;
    for (uint ty = 0; ty < (uint)visRes; ty++)
    {
        for (uint tx = 0; tx < (uint)visRes; tx++)
        {
            float3 acc = _ProbeVisibility[visBase + ty * visRes + tx].xyz;
            float mean = acc.z > 0.0 ? acc.x / acc.z : meanAll;
            float mean2 = acc.z > 0.0 ? acc.y / acc.z : meanAll * meanAll;
            _ProbeAtlasVis[visTexelBase + uint2(tx, ty)] = float4(mean, mean2, 0.0, 0.0);
        }
    }

    // Сброс геометрии переехавшей пробы - ПОСЛЕ записи атласов: этот раунд отдаёт ещё старые
    // значения (лучи-то он пустил с прежнего места), а копить с нуля начинает следующий.
    if (relocated)
    {
        _ProbeCounters[probe] = int4(0, 0, 0, 0);
        for (uint v = 0; v < visRes * visRes; v++)
        {
            _ProbeVisibility[visBase + v] = float4(0.0, 0.0, 0.0, 0.0);
        }
    }
}
