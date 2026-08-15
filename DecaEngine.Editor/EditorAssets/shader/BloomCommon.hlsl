// Общая обвязка пасса блума (см. BloomPrefilterPS / BloomDownPS / BloomUpPS / BloomCompositePS).
//
// Зачем блум вообще: он не «делает ярче», он делает источник ЧИТАЕМЫМ КАК СВЕТ. Дисплей физически
// не способен показать яркость лампы - её пик и белая бумага в кадре упираются в одну и ту же
// единицу. Разница между ними в реальности передаётся глазу рассеянием в самом глазу и в оптике, и
// блум - это оно и есть. Без него яркие места остаются просто «белыми пикселями», а не светом.
//
// Схема - прогрессивная цепочка (Jimenez, SIGGRAPH 2014 / Call of Duty): 13-тапный даунсэмпл вниз
// по мипам, затем тентовый апсэмпл вверх с накоплением. Дешевле и заметно устойчивее одного
// широкого гауссова размытия: большое ядро на полном разрешении и стоит дорого, и мерцает на
// субпиксельных бликах, потому что тап либо попал в блик, либо нет.
//
// Отдельные пассы вместо аддитивного блендинга - по той же причине, что у тумана и композита SSGI:
// PSO-абстракция движка блендинг не описывает (см. GraphicsStateInfo), а читать и писать один
// таргет одновременно нельзя.
#ifndef BLOOM_COMMON_INCLUDED
#define BLOOM_COMMON_INCLUDED

#include "Instancing.hlsl"

Texture2D    _SourceTex;
SamplerState _SourceTex_sampler;

// Нижний (более размытый) уровень цепочки - только у апсэмпла и композита.
Texture2D    _LowerTex;
SamplerState _LowerTex_sampler;

// 1x1 адаптированная яркость кадра (см. EyeAdaptationPS.hlsl) - та же, по которой делит тонемап.
// В LDR-режиме адаптации нет, сюда привязан плейсхолдер и он НЕ ЧИТАЕТСЯ (см. bloomExposureRelative).
Texture2D    _AdaptTex;

cbuffer View
{
    ViewData viewData;
}

// Зеркалит BloomConstantsData (BloomPass.cs). Свой экземпляр на КАЖДЫЙ материал цепочки: у звеньев
// разные размеры источника и таргета.
cbuffer BloomConstants
{
    // xy - размер таргета в пикселях, zw - 1/xy.
    float4 bloomTarget;
    // xy - размер источника в пикселях, zw - 1/xy.
    float4 bloomSource;

    // x - порог яркости, y - ширина мягкого колена, z - радиус тента апсэмпла,
    // w - интенсивность в композите.
    float4 bloomParams;

    // x - >0.5, если порог задан ОТНОСИТЕЛЬНО экспозиции, y - key value авто-экспозиции.
    float4 bloomExposure;
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

float BloomLuminance(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

// Множитель приведения линейного кадра к ОТОБРАЖАЕМЫМ единицам - тот же, что делает тонемап
// (key / adapted, см. TonemapPS.hlsl). Порог блума обязан задаваться именно в них: в абсолютных
// линейных единицах он был бы бесполезен ровно по той же причине, что и цвет тумана - абсолютный
// масштаб сцены произволен, и одно и то же число значило бы разное на светлой улице и в тёмном
// зале (замерено на Sponza, см. историю FogPass).
float BloomExposure()
{
    if (bloomExposure.x < 0.5)
    {
        // LDR-конвейер: кадр уже display-referred.
        return 1.0;
    }

    float adapted = max(_AdaptTex.Load(int3(0, 0, 0)).r, 1e-4);
    return max(bloomExposure.y, 1e-4) / adapted;
}

#endif // BLOOM_COMMON_INCLUDED
