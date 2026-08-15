// Финал блума: готовый ореол подмешивается в кадр. См. BloomCommon.hlsl.
//
// _SourceTex - КОПИЯ кадра (читать и писать один таргет нельзя, см. BloomPass), _LowerTex -
// верхний уровень цепочки апсэмпла.
#include "BloomCommon.hlsl"

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 uv = input.pos.xy * bloomTarget.zw;

    float4 scene = _SourceTex.Sample(_SourceTex_sampler, uv);
    float3 bloom = _LowerTex.Sample(_LowerTex_sampler, uv).rgb;

    // АДДИТИВНО и в ЛИНЕЙНОМ пространстве, до тонемапа: рассеяние в оптике добавляет свет, а не
    // подменяет его. Смешивание через lerp гасило бы сам источник, ради которого ореол и рисуется.
    //
    // Нормировка на число уровней здесь же (bloomSource.x), иначе интенсивность значила бы разное
    // при разной длине цепочки, а длина зависит от разрешения вьюпорта.
    float3 result = scene.rgb + bloom * (bloomParams.w / max(bloomSource.x, 1.0));

    // Альфа - ОТ СЦЕНЫ: композит рисует поверх кадра, и своя альфа выбила бы прозрачный фон
    // бейкера иконок (та же причина, что в SsgiCompositeCommon.hlsl и FogCommon.hlsl).
    output.color = float4(max(result, 0.0), scene.a);
    return output;
}
