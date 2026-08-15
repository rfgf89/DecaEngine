// Общее тело композита AO (см. SsaoCompositePS.hlsl / SsaoCompositeMsaaPS.hlsl - обёртки
// определяют DEPTH_FETCH под одиночный или мультисемпловый депт, ровно как у GtaoPS/GtaoMsaaPS).
// Финальный кадр (копия в _SceneTex, см. ForwardPass) умножается на размытое AO (_AoTex).
// Отдельный пасс вместо блендинга, потому что PSO-абстракция движка блендинг не описывает, а
// читать и писать один таргет одновременно нельзя.
//
// Размытие - БИЛАТЕРАЛЬНОЕ 3x3 по глубине: прежний плоский бокс тащил шум оценки через силуэты
// (тёмная кайма стены переползала на висящую перед ней штору и наоборот), а на тонкой геометрии
// вроде листвы усреднял вперемешку крону и фон за ней.
#include "Instancing.hlsl"

Texture2D    _SceneTex;
SamplerState _SceneTex_sampler;
Texture2D    _AoTex;
SamplerState _AoTex_sampler;

cbuffer View
{
    ViewData viewData;
}

// Отладочный вид AO (чекбокс "AO debug view" в окне Graphics, см. SsaoPassResources.SetDebugView):
// вместо умножения кадра на видимость композит выводит саму видимость в grayscale. Зеркалит
// AoCompositeData (SsaoPass.cs).
cbuffer AoComposite
{
    // 0 = обычный композит, 1 = grayscale AO поверх кадра.
    float aoDebugView;

    // Делать ли билатеральное размытие AO прямо здесь. Для SSAO - да, это его единственный
    // фильтр. Для GTAO - НЕТ: его результат уже прошёл краесохраняющий денойзер по «рёбрам»
    // (GtaoDenoisePS.hlsl), который и точнее этого блюра, и знает про наклон поверхности;
    // второй проход поверх него только размазал бы контактное затемнение.
    float aoCompositeBlur;

    float aoCompositePad1;
    float aoCompositePad2;
}

// CameraData near в ModelViewportEnvironment - тот же реверсивный-Z, что и в SsaoCommon/GtaoCommon.
static const float CompositeNearPlane = 0.05;

// Допуск билатерального веса в долях глубины пикселя: масштаб-инвариантно (дальние пиксели
// разнесены по z сильнее), и при этом сэмпл с ДРУГОЙ поверхности отбрасывается.
static const float DepthTolerance = 0.02;

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

float CompositeViewDepth(int2 pixel, float2 viewportSize)
{
    pixel = clamp(pixel, int2(0, 0), int2(viewportSize) - 1);
    float d = DEPTH_FETCH(pixel);
    // Фон (реверсивный-Z очищается нулём) - бесконечность: билатеральный вес отбросит его сам.
    return d < 1e-6 ? 1e9 : CompositeNearPlane / d;
}

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float2 viewportSize = viewData.viewport.zw;
    int2 pixel = int2(input.pos.xy);
    float2 uv = input.pos.xy / viewportSize;
    float2 texel = 1.0 / viewportSize;

    float centerDepth = CompositeViewDepth(pixel, viewportSize);
    float tolerance = max(DepthTolerance * centerDepth, 1e-4);

    // Центр всегда с весом 1 - так фильтр не вырождается в ноль на изолированном пикселе
    // (тонкая ветка листвы, у которой все восемь соседей - фон).
    float ao = _AoTex.Sample(_AoTex_sampler, uv).r;
    float weightSum = 1.0;

    [branch]
    if (aoCompositeBlur > 0.5)
    {
        [unroll]
        for (int y = -1; y <= 1; y++)
        {
            [unroll]
            for (int x = -1; x <= 1; x++)
            {
                if (x == 0 && y == 0)
                {
                    continue;
                }

                float tapDepth = CompositeViewDepth(pixel + int2(x, y), viewportSize);
                float weight = saturate(1.0 - abs(tapDepth - centerDepth) / tolerance);
                ao += weight * _AoTex.Sample(_AoTex_sampler, uv + float2(x, y) * texel).r;
                weightSum += weight;
            }
        }

        ao /= weightSum;
    }

    float4 scene = _SceneTex.Sample(_SceneTex_sampler, uv);

    if (aoDebugView > 0.5)
    {
        // AO уже в display-пространстве (таргет UNORM, шейдеры оценки пишут видимость как есть),
        // так что gamma-кодировать нечего - лишний pow только вымыл бы контраст ручек AO strength/floor.
        // Альфа берётся от сцены: композит рисует поверх кадра, и нулевая альфа выбила бы фон
        // бейкера иконок.
        output.color = float4(ao.xxx, scene.a);
        return output;
    }

    output.color = float4(scene.rgb * ao, scene.a);
    return output;
}
