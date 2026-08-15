// Общее тело пасса тумана (см. FogPS.hlsl / FogMsaaPS.hlsl - обёртки определяют макрос
// DEPTH_FETCH под одиночный или мультисемпловый депт, ровно как у Ssgi*/Ssao*).
//
// Зачем это вообще: воздушная перспектива - главный источник ощущения ГЛУБИНЫ в кадре, и никакая
// глобальная иллюминация её не заменяет. Без неё дальняя геометрия имеет тот же контраст, что
// ближняя, и сцена читается как плоская коробка независимо от того, насколько правильно посчитан
// свет. Дымка разводит планы по контрасту, и именно она делает «красивый» кадр красивым.
//
// Отдельный пасс, читающий КОПИЮ кадра (_SceneTex, см. FogPass), а не блендинг поверх таргета -
// по той же причине, что у SsgiCompositeCommon.hlsl: PSO-абстракция движка блендинг не описывает
// (см. GraphicsStateInfo), а читать и писать один таргет одновременно нельзя.
//
// Работает в ЛИНЕЙНОМ пространстве, до тонемапа и до замера авто-экспозиции (см.
// GraphicsPipelineSimple.SignalGraph): туман - это свет, рассеянный средой, он обязан участвовать
// в экспозиции наравне с остальной сценой. Применять его после тонемапа значило бы подмешивать
// display-значения к линейным.
#include "Instancing.hlsl"

Texture2D    _SceneTex;
SamplerState _SceneTex_sampler;

// 1x1 адаптированная яркость кадра (см. EyeAdaptationPS.hlsl) - та же, по которой делит тонемап.
// В LDR-режиме адаптации нет, сюда привязан плейсхолдер и он НЕ ЧИТАЕТСЯ (см. fogExposureRelative):
// слот объявлен безусловно, а пустой дескриптор роняет валидацию Vulkan (VUID-08114).
Texture2D    _AdaptTex;

cbuffer View
{
    ViewData viewData;
}

// Живые ручки тумана (окно Graphics -> FogPassResources.SetParams). Паддинг СКАЛЯРАМИ, не float3:
// трёхкомпонентный вектор по невыровненному смещению SPIR-V отвергает целиком (см. историю в
// SsaoCommon.hlsl). Зеркалит FogConstantsData (FogPass.cs).
cbuffer FogConstants
{
    // Плотность среды на уровне fogHeightRef, 1/единица_мира.
    float fogDensity;
    // Скорость спада плотности по высоте, 1/единица_мира. 0 - однородный туман без высоты.
    float fogHeightFalloff;
    // Высота, на которой плотность равна fogDensity.
    float fogHeightRef;
    // Дистанция, до которой тумана нет вовсе - иначе дымка садится на предметы в руках.
    float fogStartDistance;

    // Цвет самой среды (тень дымки) - линейный.
    float fogColorR, fogColorG, fogColorB;
    // Потолок непрозрачности: 1 - дальний план уходит в цвет тумана полностью, меньше - сквозь
    // дымку всегда что-то видно. Художественная ручка, физического смысла не имеет.
    float fogMaxOpacity;

    // Цвет подсветки среды солнцем - линейный, обычно теплее и ярче fogColor.
    float fogSunColorR, fogSunColorG, fogSunColorB;
    // Сила подсветки: 0 - туман одноцветный, 1 - в сторону солнца он полностью fogSunColor.
    float fogSunStrength;

    // Направление НА солнце в мире (нормализовано на CPU).
    float fogSunDirX, fogSunDirY, fogSunDirZ;
    // Резкость солнечного пятна в дымке (показатель степени): малые значения - широкое мягкое
    // свечение на полнеба, большие - компактный ореол вокруг диска.
    float fogSunSharpness;

    // Мировой базис камеры - ЕДИНИЧНЫЕ right/up/forward. Считается на CPU (см.
    // FogPassResources.SetCamera) прямо из eye/target, а не разбором матрицы вида: соглашение о
    // строках/столбцах здесь легко перепутать, а ошибка в нём дала бы туман, «приклеенный» к
    // экрану вместо мира. Масштаб FOV/аспекта накладывается ниже, ровно как в SkyBackgroundPS, -
    // так базис не приходится перепушивать на каждый ресайз вьюпорта.
    float fogRightX, fogRightY, fogRightZ;
    float fogMaxDistance;

    float fogUpX, fogUpY, fogUpZ;
    float fogPad0;

    float fogForwardX, fogForwardY, fogForwardZ;
    float fogPad1;

    // >0.5 - цвет тумана задан ОТНОСИТЕЛЬНО экспозиции (см. FogExposureScale ниже).
    float fogExposureRelative;
    // Тот же key value, что уходит в авто-экспозицию и тонемап (TonemapConstants.x).
    float fogExposureKey;
    float fogPad2, fogPad3;
}

// CameraData near/fov в ModelViewportEnvironment - тот же реверсивный-Z и тот же фиксированный
//FOV, что в SsgiCommon.hlsl и SkyBackgroundPS.hlsl.
static const float FogNearPlane = 0.05;
static const float FogTanHalfFov = 0.41421356; // tan(45deg / 2)

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

// Интеграл экспоненциальной по высоте плотности вдоль отрезка, делённый на его длину, - то есть
// СРЕДНЯЯ плотность на луче. Аналитически, а не маршем: у однородно-экспоненциальной среды
// интеграл берётся в замкнутом виде, и марш здесь был бы просто медленнее и шумнее.
//
//   d(h) = exp(-k * (h - href))
//   (1/L) * S d(h(t)) dt  =  (d(h0) - d(h1)) / (k * (h1 - h0))
//
// Вырожденная ветка (луч почти горизонтален) берётся по пределу k*(h1-h0) -> 0, иначе здесь
// деление на ноль и белый экран на любом взгляде вдоль горизонта.
float FogAverageDensity(float h0, float h1)
{
    if (fogHeightFalloff < 1e-5)
    {
        return 1.0;
    }

    float d0 = exp(-fogHeightFalloff * (h0 - fogHeightRef));
    float dh = h1 - h0;
    if (abs(dh) < 1e-4)
    {
        return d0;
    }

    float d1 = exp(-fogHeightFalloff * (h1 - fogHeightRef));
    return (d0 - d1) / (fogHeightFalloff * dh);
}

// Множитель яркости тумана. Тонемап делит кадр на adapted и умножает на key (см. TonemapPS.hlsl),
// поэтому чтобы дымка выглядела ИМЕННО так, как её задал художник, её линейное значение обязано
// быть домножено на обратную величину - adapted/key. Тогда после экспозиции от неё остаётся ровно
// заданный цвет, в какой бы абсолютной яркости ни жила сцена.
//
// Без этого ручка цвета была бесполезна: цвет задаётся в АБСОЛЮТНЫХ линейных единицах, а
// абсолютный масштаб сцены произволен и художнику неизвестен. Замерено на интерьере Sponza с
// авто-экспозицией: дефолтная дымка (~0.5 линейных) против тамошней радиансы залила кадр целиком -
// средняя яркость 36 -> 104, минимум 2 -> 55.
float FogExposureScale()
{
    if (fogExposureRelative < 0.5)
    {
        // LDR-конвейер: кадр уже display-referred, пересчитывать нечего.
        return 1.0;
    }

    float adapted = max(_AdaptTex.Load(int3(0, 0, 0)).r, 1e-4);
    return adapted / max(fogExposureKey, 1e-4);
}

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 viewportSize = viewData.viewport.zw;
    int2 pixel = clamp(int2(input.pos.xy), int2(0, 0), int2(viewportSize) - 1);
    float2 uv = input.pos.xy / viewportSize;

    float4 scene = _SceneTex.Sample(_SceneTex_sampler, uv);

    // Луч пикселя в МИРЕ. НЕ нормализуется намеренно: forward единичный и перпендикулярен
    // right/up, поэтому проекция такого луча на ось камеры равна РОВНО единице - а значит мировая
    // точка пикселя получается как camPos + луч * zView, где zView и есть глубина вдоль оси, без
    // единого деления. Нормализация здесь всё бы сломала.
    float aspect = viewData.viewport.z / max(viewData.viewport.w, 1.0);
    float3 ray = float3(fogForwardX, fogForwardY, fogForwardZ)
        + float3(fogRightX, fogRightY, fogRightZ) * (input.ndc.x * FogTanHalfFov * aspect)
        + float3(fogUpX, fogUpY, fogUpZ) * (input.ndc.y * FogTanHalfFov);

    // Реверсивный-Z: ноль - это ФОН (таргет очищается нулём), а не нулевая глубина. Небу отдаём
    // предельную дальность - иначе горизонт остался бы единственным местом без дымки, и вся
    // воздушная перспектива развалилась бы ровно там, где она заметнее всего.
    float depth = DEPTH_FETCH(pixel);
    float zView = depth < 1e-6 ? fogMaxDistance : FogNearPlane / depth;

    float rayLength = length(ray);
    float distance = min(zView * rayLength, fogMaxDistance);

    float3 camPos = viewData.CameraWorldPos;
    float3 worldPos = camPos + ray * zView;

    // Ближняя зона вырезается ИЗ ДЛИНЫ ЛУЧА, а не порогом «есть туман / нет»: порог дал бы видимое
    // кольцо на дистанции отсечки.
    float fogged = max(distance - fogStartDistance, 0.0);
    float optical = fogDensity * fogged * FogAverageDensity(camPos.y, worldPos.y);
    float amount = saturate(1.0 - exp(-optical)) * saturate(fogMaxOpacity);

    // Подсветка среды солнцем - то, ради чего туман вообще ставят: дымка перестаёт быть серой
    // пеленой и начинает светиться со стороны источника. Это дешёвая замена настоящему
    // однократному рассеянию, и в кадре она отвечает почти за весь эффект.
    float3 viewDir = ray / max(rayLength, 1e-6);
    float sunDot = saturate(dot(viewDir, float3(fogSunDirX, fogSunDirY, fogSunDirZ)));
    float sunAmount = pow(sunDot, max(fogSunSharpness, 1e-3)) * saturate(fogSunStrength);

    float3 fogColor = lerp(
        float3(fogColorR, fogColorG, fogColorB),
        float3(fogSunColorR, fogSunColorG, fogSunColorB),
        sunAmount) * FogExposureScale();

    // Альфа берётся ОТ СЦЕНЫ: пасс рисует поверх кадра, и своя альфа выбила бы фон бейкера иконок
    // (та же причина, что в SsgiCompositeCommon.hlsl).
    output.color = float4(lerp(scene.rgb, fogColor, amount), scene.a);
    return output;
}
