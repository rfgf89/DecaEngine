// Общее тело композита SSGI (см. SsgiCompositePS.hlsl / SsgiCompositeMsaaPS.hlsl - обёртки
// определяют DEPTH_FETCH под одиночный или мультисемпловый депт, ровно как у SsaoComposite*).
// К финальному кадру (копия в _SceneTex, см. SsgiPass) аддитивно подмешивается размытый bounce
// (_GiTex). Отдельный пасс вместо блендинга по той же причине, что и у SsaoCompositeCommon.hlsl:
// PSO-абстракция движка блендинг не описывает, а читать и писать один таргет одновременно нельзя.
//
// Размытие - БИЛАТЕРАЛЬНОЕ по глубине и с настраиваемым радиусом: прежний плоский бокс 3x3
// оставлял почти весь шум спиральных тапов (восемь тапов на пиксель, девять пикселей в окне) и
// протаскивал цветную кайму через силуэты - шторы Sponza тянули свой цвет на стену за ними.
#include "Instancing.hlsl"

Texture2D    _SceneTex;
SamplerState _SceneTex_sampler;
Texture2D    _GiTex;
SamplerState _GiTex_sampler;

cbuffer View
{
    ViewData viewData;
}

// Живые ручки композита (окно Graphics -> SsgiPassResources.SetCompositeParams).
// Зеркалит GiCompositeData (SsgiPass.cs).
cbuffer GiComposite
{
    // Радиус билатерального окна в пикселях (0 - без размытия, кламп сверху GiMaxBlurRadius).
    float giBlurRadius;
    // 0 - обычный композит, 1 - вывести один только bounce (отладка ручек SSGI).
    float giDebugView;
    float giCompositePad0;
    float giCompositePad1;
}

// CameraData near в ModelViewportEnvironment - тот же реверсивный-Z, что и в SsgiCommon.hlsl.
static const float CompositeNearPlane = 0.05;

// Допуск билатерального веса в долях глубины пикселя (как в SsaoCompositeCommon.hlsl): дальние
// пиксели разнесены по z сильнее, но сэмпл с ДРУГОЙ поверхности всё равно отбрасывается.
static const float DepthTolerance = 0.02;

// Потолок радиуса: динамические границы цикла компилятор не разворачивает, фиксируем верх.
static const int GiMaxBlurRadius = 3;

struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

float GiCompositeViewDepth(int2 pixel, float2 viewportSize)
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

    int radius = (int)clamp(giBlurRadius, 0.0, (float)GiMaxBlurRadius);
    float centerDepth = GiCompositeViewDepth(pixel, viewportSize);
    float tolerance = max(DepthTolerance * centerDepth, 1e-4);

    // Центр всегда с весом 1 - так фильтр не вырождается в ноль на изолированном пикселе
    // (тонкая ветка листвы, у которой все соседи - фон).
    float3 gi = _GiTex.Sample(_GiTex_sampler, uv).rgb;
    float weightSum = 1.0;

    [loop]
    for (int y = -GiMaxBlurRadius; y <= GiMaxBlurRadius; y++)
    {
        if (abs(y) > radius)
        {
            continue;
        }

        [loop]
        for (int x = -GiMaxBlurRadius; x <= GiMaxBlurRadius; x++)
        {
            if (abs(x) > radius || (x == 0 && y == 0))
            {
                continue;
            }

            float tapDepth = GiCompositeViewDepth(pixel + int2(x, y), viewportSize);
            float weight = saturate(1.0 - abs(tapDepth - centerDepth) / tolerance);
            gi += weight * _GiTex.Sample(_GiTex_sampler, uv + float2(x, y) * texel).rgb;
            weightSum += weight;
        }
    }
    gi /= weightSum;

    float4 scene = _SceneTex.Sample(_SceneTex_sampler, uv);

    if (giDebugView > 0.5)
    {
        // Альфа берётся от сцены: композит рисует поверх кадра, и нулевая альфа выбила бы фон
        // бейкера иконок.
        output.color = float4(gi, scene.a);
        return output;
    }

    // Аддитивно, БЕЗ модуляции цветом сцены: bounce нужен именно там, где прямого света мало
    // (затенённая сторона), а там scene.rgb тёмный и умножение съело бы весь эффект.
    // И БЕЗ saturate: в HDR-конвейере (авто-экспозиция) композит пишет в линейный RGBA16F,
    // и кламп в единицу срезал бы весь запас яркости ещё до тонемапа - небо и солнечные пятна
    // выходили плоско-белыми, а экспозиция мерила уже обрезанный кадр.
    output.color = float4(max(scene.rgb + gi, 0.0), scene.a);
    return output;
}
