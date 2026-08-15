// Первое звено блума: выделение ярких мест из кадра в половинное разрешение (см. BloomCommon.hlsl).
#include "BloomCommon.hlsl"

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 uv = input.pos.xy * bloomTarget.zw;

    // Четыре тапа по углам тексела ИСТОЧНИКА, а не один по центру: половинный таргет иначе просто
    // прореживает кадр, и субпиксельный блик то попадает в тап, то нет - блум мерцает при малейшем
    // движении камеры. Билинейная выборка между текселями усредняет квадрат 2x2 за один тап.
    float2 o = bloomSource.zw;
    float3 c = _SourceTex.Sample(_SourceTex_sampler, uv + float2(-o.x, -o.y)).rgb
             + _SourceTex.Sample(_SourceTex_sampler, uv + float2( o.x, -o.y)).rgb
             + _SourceTex.Sample(_SourceTex_sampler, uv + float2(-o.x,  o.y)).rgb
             + _SourceTex.Sample(_SourceTex_sampler, uv + float2( o.x,  o.y)).rgb;
    c *= 0.25;

    // Отрицательных значений в линейном кадре быть не должно, но SSGI и туман пишут суммы, а
    // RGBA16F даёт им уплыть в минус на округлении - без клампа отсюда поедут NaN по всей цепочке.
    c = max(c, 0.0);

    // Порог считается по ОТОБРАЖАЕМОЙ яркости (см. BloomExposure), а вот вычитается он из
    // ЛИНЕЙНОГО цвета - цепочка и композит работают в линейном пространстве, как и весь кадр.
    float exposure = BloomExposure();
    float luminance = BloomLuminance(c) * exposure;

    float threshold = bloomParams.x;
    float knee = max(bloomParams.y, 1e-4);

    // Мягкое колено (Karis): жёсткое отсечение по порогу даёт видимую ступеньку на градиенте -
    // поверхность плавно светлеет, и ровно на пороге у неё вдруг включается ореол. Квадратичная
    // вставка шириной knee вокруг порога убирает разрыв и первой производной.
    float soft = clamp(luminance - threshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee);
    float weight = max(soft, luminance - threshold) / max(luminance, 1e-4);

    output.color = float4(c * weight, 1.0);
    return output;
}
