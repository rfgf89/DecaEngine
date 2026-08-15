// Композит контура выделения Scene View (см. SelectionOutlineOverlay): край маски силуэта
// (SelectionMaskVS/PS) рисуется оранжевой обводкой поверх готового кадра. Блендинга в PSO движка
// нет - вместо него пасс читает копию кадра (_SceneTex, снятую CopyTexture перед композитом) и
// пишет результат целиком. Фуллскрин-треугольник - SkyBackgroundVS.hlsl.

// Маска силуэта выделения (1 = объект).
Texture2D _MaskTex;

// Копия готового кадра (ColorTarget до этого пасса).
Texture2D _SceneTex;

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

float LoadMask(int2 p)
{
    // Load за краем таргета возвращает 0 - контур на границе экрана корректно обрезается.
    return _MaskTex.Load(int3(p, 0)).r;
}

float4 Main(in VSOutput input) : SV_TARGET
{
    int2 p = int2(input.pos.xy);
    float3 scene = _SceneTex.Load(int3(p, 0)).rgb;
    float center = LoadMask(p);

    // Дилатация маски кольцом тапов радиуса 1-2: контур ~2px, как в обычных редакторах.
    float ring = 0.0;
    ring = max(ring, LoadMask(p + int2( 1,  0)));
    ring = max(ring, LoadMask(p + int2(-1,  0)));
    ring = max(ring, LoadMask(p + int2( 0,  1)));
    ring = max(ring, LoadMask(p + int2( 0, -1)));
    ring = max(ring, LoadMask(p + int2( 1,  1)));
    ring = max(ring, LoadMask(p + int2(-1,  1)));
    ring = max(ring, LoadMask(p + int2( 1, -1)));
    ring = max(ring, LoadMask(p + int2(-1, -1)));
    ring = max(ring, LoadMask(p + int2( 2,  0)));
    ring = max(ring, LoadMask(p + int2(-2,  0)));
    ring = max(ring, LoadMask(p + int2( 0,  2)));
    ring = max(ring, LoadMask(p + int2( 0, -2)));

    // Край = расширенная маска минус сама маска: обводка снаружи силуэта, объект не перекрывается.
    float edge = saturate(ring - center);

    const float3 outlineColor = float3(1.0, 0.55, 0.1);
    return float4(lerp(scene, outlineColor, edge), 1.0);
}
