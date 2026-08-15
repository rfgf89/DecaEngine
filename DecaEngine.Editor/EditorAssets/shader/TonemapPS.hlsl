// Финал HDR-конвейера превью: линейный RGBA16F-кадр -> экспозиция от авто-адаптации -> кривая
// PBR Neutral -> ручной sRGB-энкод в отображаемый RGBA8-таргет (тот, что показывает ImGui и
// читает readback превью-пробы). В LDR-режиме (HDR-конвейер выключен) этого пасса нет вовсе, а
// те же две операции делает сам UnlitInstancedPS.hlsl.
#include "Tonemap.hlsl"

Texture2D    _SceneTex;
SamplerState _SceneTex_sampler;

// 1x1 адаптированная яркость кадра (см. EyeAdaptationPS.hlsl).
Texture2D _AdaptTex;

// Зеркалит TonemapConstantsData (TonemapPass.cs).
cbuffer TonemapConstants
{
    // x = key value (средняя яркость, к которой приводится кадр), y = экспокоррекция в стопах,
    // z = 1 - режим passthrough (кадр копируется как есть), w = режим кривой (см. Tonemap.hlsl).
    float4 TmParams;

    // x = 1 - экспозиция по ЗАМЕРЕННОЙ яркости (_AdaptTex), 0 - ручная: экспозиция равна exp2(EV) и
    // от содержимого кадра не зависит. y = 1 - форсировать альфу в 1: нативный апскейлер (FSR)
    // альфу не переносит, его выход приходит с alpha 0, и композит превью по ней выкидывал бы ВЕСЬ
    // кадр (см. TonemapPassResources.SetForceOpaque). zw - резерв.
    float4 TmParams2;
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

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    // Билинейный сэмпл по UV, а не Load по пикселю: тонемап - точка апскейла конвейера, и при
    // масштабе рендера меньше 1 HDR-кадр МЕНЬШЕ отображаемого таргета (см.
    // GraphicsPipelineSimple.SetRenderScale). При 1:1 UV попадает ровно в центры текселей и фильтр
    // вырождается в точное чтение, так что отдельной ветки для полного разрешения нет.
    float2 uv = input.ndc * float2(0.5, -0.5) + 0.5;
    float4 scene = _SceneTex.SampleLevel(_SceneTex_sampler, uv, 0);

    // Отладочные режимы превью (каналы нормалей/UV, AO debug view, probe debug) пишут в кадр
    // УЖЕ отображаемые значения - экспозиция и кривая исказили бы ровно то, что художник смотрит
    // как есть. Тумблер живой (см. TonemapPassResources.SetPassthrough).
    if (TmParams.z > 0.5)
    {
        output.color = scene;
        return output;
    }

    // В ручном режиме кадр экспонируется одной лишь экспокоррекцией: EV 0 даёт множитель 1, то есть
    // ровно то, что раньше писал сам UnlitInstancedPS в LDR-конвейере.
    float adapted = max(_AdaptTex.Load(int3(0, 0, 0)).r, 1e-4);
    float autoScale = TmParams2.x > 0.5 ? (TmParams.x / adapted) : 1.0;
    float exposure = autoScale * exp2(TmParams.y);

    float3 mapped = ApplyToneCurve(max(scene.rgb, 0.0) * exposure, (int)TmParams.w);

    // Альфа проходит насквозь: превью очищается прозрачным фоном, под моделью ImGui рисует свою
    // подложку (см. ModelPreviewViewport.Render), а бейкер иконок сохраняет её в PNG.
    output.color = float4(pow(saturate(mapped), 1.0 / 2.2), max(scene.a, TmParams2.y));
    return output;
}
