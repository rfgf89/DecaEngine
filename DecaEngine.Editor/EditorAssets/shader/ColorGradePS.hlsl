// Цветокоррекция и виньетка - финальный пасс кадра (см. ColorGradePass).
//
// Работает в ОТОБРАЖАЕМОМ пространстве, по уже готовому RGBA8-кадру, а не в линейном до тонемапа, и
// это не небрежность: подъём теней, гамма и насыщенность - это классическая цветокоррекция, её
// шкалы (0.5 - средний тон, 1.0 - белое) определены именно в гамма-пространстве. Перенеси их в
// линейное - и «контраст 1.2» перестанет значить что-либо предсказуемое.
//
// Единый пасс на оба конвейера ровно поэтому же: ColorTarget всегда RGBA8 display-space - и в HDR
// (его пишет TonemapPass), и в LDR (его пишет сама геометрия). Отсюда же СВОЯ копия кадра, а не
// общий SceneCopyTarget: тот в HDR-режиме RGBA16F, и CopyTexture в него не годится по формату.
//
// Ставится ПОСЛЕ тонемапа, но ДО оверлеев (см. GraphicsPipelineSimple): контур выделения и гизмо -
// элементы интерфейса, их художественная коррекция трогать не должна.
#include "Instancing.hlsl"

Texture2D    _SceneTex;
SamplerState _SceneTex_sampler;

cbuffer View
{
    ViewData viewData;
}

// Зеркалит ColorGradeConstantsData (ColorGradePass.cs).
cbuffer GradeConstants
{
    // x - насыщенность, y - контраст, z - гамма, w - температура.
    float4 gradeParams;
    // x - оттенок (tint), y - сила виньетки, z - её радиус, w - мягкость её края.
    float4 gradeParams2;
    // xyz - тонировка теней (аддитивная), w - вытянутость виньетки к формату кадра.
    float4 gradeShadowTint;
    // xyz - тонировка светов (мультипликативная), w - резерв.
    float4 gradeHighlightTint;
    // xy - размер таргета, zw - 1/xy.
    float4 gradeTarget;
}

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

static const float3 LumaWeights = float3(0.2126, 0.7152, 0.0722);

// Баланс белого. Приближение, а не пересчёт через цветовые температуры Планка: художнику здесь
// нужна предсказуемая ручка «теплее/холоднее», а не колориметрия, - и результат всё равно судится
// глазом. Множитель НОРМИРУЕТСЯ по яркости, иначе ручка меняла бы заодно и экспозицию, и её
// пришлось бы всё время компенсировать.
float3 GradeWhiteBalance(float temperature, float tint)
{
    float3 wb = float3(
        1.0 + 0.30 * temperature,
        1.0 - 0.30 * tint,
        1.0 - 0.30 * temperature);

    return wb / max(dot(wb, LumaWeights), 1e-4);
}

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 uv = input.pos.xy * gradeTarget.zw;
    float4 scene = _SceneTex.Sample(_SceneTex_sampler, uv);
    float3 c = max(scene.rgb, 0.0);

    // 1. Баланс белого - первым: он правит источник, а не результат коррекции.
    c *= GradeWhiteBalance(gradeParams.w, gradeParams2.x);

    // 2. Тонировка теней (аддитивно) и светов (мультипликативно) - разделение по способу
    // применения, а не по маске: аддитив поднимает именно чёрное, не трогая белое, множитель -
    // наоборот. Это и есть lift/gain, только без промежуточного колеса.
    c = c * gradeHighlightTint.rgb + gradeShadowTint.rgb;

    // 3. Гамма - до контраста и насыщенности: она перераспределяет средние тона, и считать
    // контраст надо уже по ним.
    c = pow(max(c, 0.0), 1.0 / max(gradeParams.z, 1e-3));

    // 4. Контраст вокруг среднего серого. Пивот именно 0.5, а не 0.18: мы в гамма-пространстве,
    // где средний тон - это половина шкалы.
    c = (c - 0.5) * gradeParams.y + 0.5;

    // 5. Насыщенность - последней из цветовых: она обязана видеть уже финальный тон, иначе
    // приглушённый ползунком цвет вернул бы себе насыщенность гаммой и контрастом.
    float luma = dot(max(c, 0.0), LumaWeights);
    c = lerp(luma.xxx, c, gradeParams.x);

    // 6. Виньетка. Радиус считается от ЦЕНТРА в координатах, растянутых к формату кадра, - иначе
    // на широком вьюпорте круг превратился бы в овал по горизонтали.
    float2 d = uv - 0.5;
    d.x *= lerp(1.0, gradeTarget.x / max(gradeTarget.y, 1.0), saturate(gradeShadowTint.w));

    float radius = max(gradeParams2.z, 1e-3);
    float smoothWidth = max(gradeParams2.w, 1e-3);

    // smoothstep от большего к меньшему: на краю кадра множитель уходит в ноль, в центре - единица.
    float v = smoothstep(radius, max(radius - smoothWidth, 0.0), length(d));
    c *= lerp(1.0, v, saturate(gradeParams2.y));

    // Альфа - ОТ СЦЕНЫ: превью очищается прозрачным фоном, и своя альфа выбила бы подложку ImGui и
    // фон бейкера иконок (та же причина, что в FogCommon.hlsl).
    output.color = float4(saturate(c), scene.a);
    return output;
}
