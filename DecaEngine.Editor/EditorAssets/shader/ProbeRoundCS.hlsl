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
};

// Направления лучей раунда считает CPU и присылает готовыми. Повторять формулу веера Фибоначчи в
// шейдере нельзя: расхождение в последнем бите синуса увело бы луч на соседний треугольник у
// силуэта, и сверка с CPU-эталоном перестала бы что-либо значить.
StructuredBuffer<float4> _ProbeRayDirections;

// xyz = угол кирпича в координатах виртуальной сетки, w = уровень подразделения (шаг проб внутри
// кирпича равен Cell * 2^level, см. ProbeGiBaker.BrickLevel).
StructuredBuffer<int4> _ProbeBrickOrigin;

// Карта индирекции по ячейкам САМОЙ МЕЛКОЙ сетки кирпичей: x = индекс кирпича в пуле (-1 = пусто),
// y = его уровень. Крупный кирпич прописан во все свои ячейки, поэтому чтение одно на сбор.
StructuredBuffer<int2> _ProbeBrickIndex;

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

#define PROBE_BRICK_PROBES 4
#define PROBE_BRICK_CELLS  3

// xyz = размер виртуальной сетки проб, w - резерв.
// (второй кбуфер, чтобы не ломать раскладку первого)
cbuffer ProbeGridParams
{
    float4 ProbeGridCounts;
    // xyz = размер сетки кирпичей.
    float4 ProbeBrickCounts;
    // xyz = размер вокселя кэша поверхностей, w = число живых вокселей (0 = кэш выключен).
    float4 SurfaceVoxel;
    // xyz = размер воксельной сетки кэша.
    float4 SurfaceCounts;
    // x = поворот энвайронмента (радианы), y = множитель яркости неба, z = сторона окто-карты
    // видимости (ручка «Visibility res», см. ProbeGiBakeResult.VisRes), w = кирпичей в ряду пула.
    float4 ProbeSkyParams;
};

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

// Тексель пробы в пуле - зеркало ProbeGiBaker.ProbeTexel.
uint2 ProbeAtlasTexel(uint slot)
{
    const uint perBrick = PROBE_BRICK_PROBES * PROBE_BRICK_PROBES * PROBE_BRICK_PROBES;
    uint brick = slot / perBrick;
    uint local = slot - brick * perBrick;
    uint poolColumns = max((uint)ProbeSkyParams.w, 1u);

    return uint2(
        (brick % poolColumns) * PROBE_BRICK_PROBES + local % PROBE_BRICK_PROBES,
        (brick / poolColumns) * PROBE_BRICK_PROBES * PROBE_BRICK_PROBES
            + (local / (PROBE_BRICK_PROBES * PROBE_BRICK_PROBES)) * PROBE_BRICK_PROBES
            + (local / PROBE_BRICK_PROBES % PROBE_BRICK_PROBES));
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

    int3 fineBrick = min((int3)(f / PROBE_BRICK_CELLS), (int3)ProbeBrickCounts.xyz - 1);
    int cellIndex = (fineBrick.z * (int)ProbeBrickCounts.y + fineBrick.y) * (int)ProbeBrickCounts.x
                  + fineBrick.x;
    int2 indirection = _ProbeBrickIndex[cellIndex];
    if (indirection.x < 0)
    {
        // Кирпича нет - вокруг пустота, поля здесь не запечено.
        return 0.0;
    }

    int brick = indirection.x;
    int step = 1 << indirection.y;
    float3 brickOrigin = (float3)_ProbeBrickOrigin[brick].xyz;

    // Локальные координаты считаются в ШАГЕ ПРОБ кирпича, а не в мелких ячейках: у крупного
    // кирпича пробы стоят через 2^level ячеек.
    float3 lf = (f - brickOrigin) / (float)step;
    int3 l = clamp((int3)floor(lf), 0, PROBE_BRICK_CELLS - 1);
    float3 t = saturate(lf - (float3)l);

    uint slotBase = (uint)brick * PROBE_BRICK_PROBES * PROBE_BRICK_PROBES * PROBE_BRICK_PROBES;

    float3 sh0 = 0.0, shX = 0.0, shY = 0.0, shZ = 0.0;
    float fracSum = 0.0;
    float weightSum = 0.0;

    [unroll]
    for (int corner = 0; corner < 8; corner++)
    {
        int3 o = int3(corner & 1, (corner >> 1) & 1, corner >> 2);
        int3 lp = l + o;
        uint index = slotBase
            + (uint)((lp.z * PROBE_BRICK_PROBES + lp.y) * PROBE_BRICK_PROBES + lp.x);

        float4 field1 = _ProbeFieldRead[index * 4 + 1];
        float w = (o.x ? t.x : 1.0 - t.x) * (o.y ? t.y : 1.0 - t.y) * (o.z ? t.z : 1.0 - t.z)
                * field1.a;

        // Смещение соседа учитывается и здесь: вес по нормали меряет направление НА пробу, а она
        // могла быть отодвинута из стены. Чтение буфера, который этот же диспатч пишет, здесь
        // безобидно - в худшем случае сосед отдаст смещение прошлого раунда, а разница между ними
        // заведомо меньше отступа, на который влияет вес.
        float3 probePos = origin + (brickOrigin + (float3)lp * (float)step) * cell
                        + _ProbeOffsets[index].xyz;
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

    const uint perBrick = PROBE_BRICK_PROBES * PROBE_BRICK_PROBES * PROBE_BRICK_PROBES;
    uint brick = probe / perBrick;
    uint local = probe - brick * perBrick;

    int4 brickOrigin = _ProbeBrickOrigin[brick];
    int step = 1 << brickOrigin.w;
    int3 cell = brickOrigin.xyz + int3(
        (int)(local % PROBE_BRICK_PROBES) * step,
        (int)(local / PROBE_BRICK_PROBES % PROBE_BRICK_PROBES) * step,
        (int)(local / (PROBE_BRICK_PROBES * PROBE_BRICK_PROBES)) * step);

    // СОН ПРОБ (Majercik 2021 §6, упрощённая машина состояний): проба, чьё поле давно спокойно
    // (счётчик calm в _ProbeCounters.w), обновляется раз в 4 раунда - фаза по номеру пробы против
    // фазы веера. Включается ТОЛЬКО в устоявшемся реальном времени (см. RunRound: вес на полу и
    // окно релокации закрыто); смена света поднимает вес раунда и будит всех, движение сцены
    // открывает релокацию - тоже всех, а проснувшаяся проба с изменившимся полем сама обнуляет
    // calm внизу. Экономия - до 3/4 лучей кадра на спокойной сцене.
    int4 countersPrev = _ProbeCounters[probe];
    int sleepPhase = (int)ProbeRelocation.w;
    if (sleepPhase > 0 && countersPrev.w > 16 && ((int)probe & 3) != sleepPhase - 1)
    {
        return;
    }

    // Состояние OFF (§6 статьи): проба, оставшаяся В СТЕНЕ после окна релокации (окно закрыто,
    // статистики достаточно, большинство лучей в задние грани), не трассируется вовсе - её
    // валидность и так ноль, выборка её не читает, а лучи она жгла бы вслепую. Движение сцены
    // переоткрывает окно (ProbeRelocation.x > 0), и проба получает новый шанс выбраться.
    if (ProbeRelocation.z > 0.5 && ProbeRelocation.x == 0.0
        && countersPrev.x >= 64 && countersPrev.z * 2 > countersPrev.x)
    {
        return;
    }

    // Трассируем из АКТУАЛЬНОЙ позиции пробы - с учётом накопленной релокации. Иначе статистика
    // задних граней описывала бы узел сетки, а не то место, где проба реально стоит, и релокация
    // не сошлась бы: сдвинувшись наружу, проба продолжала бы считать себя замурованной.
    float3 probeOffset = _ProbeOffsets[probe].xyz;
    float3 probePos = ProbeGridOrigin.xyz + (float3)cell * ProbeGridCell.xyz + probeOffset;

    int rays = (int)ProbeSunDirection.w;
    float tMax = ProbeGridCell.w;
    float sceneEpsilon = ProbeRoundParams.x;
    float visMax = ProbeRoundParams.y;
    float domega = 4.0 * 3.14159265 / (float)rays;

    float3 sum0 = 0.0, sumX = 0.0, sumY = 0.0, sumZ = 0.0;
    float sunLum = 0.0, totalLum = 0.0;
    int missCount = 0, backCount = 0;

    // Для релокации: ближайшая ЗАДНЯЯ грань - это ближайший выход наружу (если проба внутри
    // геометрии, луч, ушедший в стену, протыкает её изнутри и выходит именно там), а ближайшая
    // передняя - мера того, сколько вокруг свободного места.
    float closestBackT = tMax; float3 closestBackDir = float3(0.0, 1.0, 0.0);
    float closestFrontT = tMax; float3 closestFrontDir = float3(0.0, 1.0, 0.0);
    float farthestFrontT = 0.0; float3 farthestFrontDir = float3(0.0, 1.0, 0.0);
    uint visBase = probe * visRes * visRes;

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
    for (int r = 0; r < rays; r++)
    {
        float3 dir = _ProbeRayDirections[r].xyz;
        dir = float3(fanCos * dir.x + fanSin * dir.z, dir.y,
                     fanCos * dir.z - fanSin * dir.x);
        SceneHit hit = SceneTraceClosest(probePos, dir, tMax);

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
                // Луч вышел изнутри геометрии - проба в стене.
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
                // ПОРЯДОК ВАЖЕН: релокации нужна ПОЛНАЯ дистанция до задней грани - это её
                // ближайший выход наружу, и по укороченной проба вышла бы из стены на пятую часть
                // нужного пути и осталась внутри. Укорачивание касается только записи в глубину.
                if (hitT < closestBackT)
                {
                    closestBackT = hitT;
                    closestBackDir = dir;
                }

                hitT *= 0.2;
            }
            else
            {
                if (hitT < closestFrontT)
                {
                    closestFrontT = hitT;
                    closestFrontDir = dir;
                }

                if (hitT > farthestFrontT)
                {
                    farthestFrontT = hitT;
                    farthestFrontDir = dir;
                }
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

    // Вес раунда - бегущее среднее, посчитанное на CPU (разгонные раунды кладутся целиком).
    float alpha = ProbeGridOrigin.w;
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
    int4 counters = _ProbeCounters[probe] + int4(rays, missCount, backCount, 0);
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
    bool bigChange = false;

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
    // bigChange - мимо ограничителя: смена распределения на 80%+ это реальное событие (объект
    // уехал, свет щёлкнул), и вести к нему поле шагами по 3% значило бы секунды видимой лжи.
    float maxStep = ProbeChunk.w;
    if (maxStep > 0.0 && alphaEff < 1.0 && !bigChange)
    {
        float3 delta = out0.rgb - prev0.rgb;
        float deltaLen = length(delta);

        // Масштаб по ПОЛУСУММЕ старого и нового: от одного лишь старого проба, стоящая в нуле
        // (свет ещё не дошёл), не смогла бы тронуться с места вовсе.
        float scale = 0.5 * (length(prev0.rgb) + length(out0.rgb)) + 1e-4;
        float limit = maxStep * scale;
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

    _ProbeField[fieldBase + 0] = out0;
    _ProbeField[fieldBase + 1] = out1;
    _ProbeField[fieldBase + 2] = out2;
    _ProbeField[fieldBase + 3] = out3;

    // Релокация: проба, стоящая внутри стены или колонны, отодвигается наружу. Это главное лекарство
    // густой сетки - чем мельче ячейка, тем больше проб оказывается замуровано, а такая проба и
    // мигает (её лучи мечутся между задними гранями и небом за краем), и течёт светом сквозь стену.
    float relocLimit = ProbeRelocation.x;
    bool relocated = false;
    if (relocLimit > 0.0)
    {
        float backFrac = (float)backCount / (float)rays;
        float3 newOffset = probeOffset;
        float offsetLen = length(probeOffset);

        float minCell = min(ProbeGridCell.x, min(ProbeGridCell.y, ProbeGridCell.z));
        if (backFrac > 0.25 && closestBackT < tMax)
        {
            // Внутри геометрии. Ближайшая задняя грань и есть ближайший выход - шагаем прямо за
            // неё, с запасом в тот же отступ, которым пользуются теневые лучи.
            newOffset = probeOffset + closestBackDir * (closestBackT + ProbeRoundParams.z);
        }
        else if (closestFrontT < 0.3 * minCell && dot(closestFrontDir, farthestFrontDir) < 0.5)
        {
            // Вторая ветка оптимизатора статьи (Listing 4): проба НЕ в стене, но прижата к
            // поверхности - полверia лучей бьёт в упор, оценка кипит, тест Чебышёва работает на
            // разрыве. Отходим малым шагом К САМОЙ ДАЛЬНЕЙ передней грани (= в открытое
            // пространство); условие несонаправленности - страховка статьи от пробы, видящей одну
            // единственную поверхность (дальняя = ближняя, отходить некуда).
            newOffset = probeOffset + farthestFrontDir * min(0.2 * minCell, farthestFrontT);
        }
        // Возврата к узлу НЕТ намеренно (статья §5 пробы тоже не возвращает): проверка «вокруг
        // просторно» смотрела от текущей позиции, а не вдоль пути к узлу, и проба у тонкого пола
        // качалась - возврат протыкал пол, следующий раунд снова выталкивал, и каждый переезд
        // сбрасывал накопители: мигание свеже-стартовыми значениями в шахматном узоре геометрии.
        // Устаревшее смещение при движении сцены снимает переоткрытие окна релокации: замурованную
        // выталкивает заново, а лишнее смещение безвредно - проба остаётся в своей ячейке.

        // Дальше предела уходить нельзя: покинув свою ячейку, проба сломает трилинейную
        // интерполяцию сильнее, чем выигрывает от того, что выбралась из стены.
        float newLen = length(newOffset);
        if (newLen > relocLimit)
        {
            newOffset *= relocLimit / newLen;
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
        _ProbeOffsets[probe] = float4(newOffset, 1.0);
    }

    // Те же значения сразу в атласы - материалы читают их без участия CPU.
    uint2 texel = ProbeAtlasTexel(probe);
    _ProbeAtlasOffset[texel] = float4(probeOffset, 1.0);
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
