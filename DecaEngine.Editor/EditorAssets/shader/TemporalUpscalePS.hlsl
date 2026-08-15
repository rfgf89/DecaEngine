// Темпоральный апскейл (TAAU) - встроенный управляемый бэкенд слота апскейлера (см.
// TemporalUpscalePass.cs). Сцена растеризуется в РЕНДЕР-разрешении с суб-пиксельным джиттером
// проекции; этот пасс собирает display-кадр, аккумулируя историю: за 16 фаз Halton камера
// "просматривает" каждый display-пиксель, и аккумулятор восстанавливает детализацию, которой в
// отдельном рендер-кадре нет. FSR/DLSS встанут на это же место с тем же контрактом входов:
// HDR-кадр, векторы движения, джиттер, пара разрешений.
//
// Рецепт стандартный и нарочно минимальный (это референс корректности входов, а не конкурент FSR):
//   1) текущий кадр читается с АНТИ-джиттером: джиттер +J сдвигает картинку на +J пикселей,
//      значит содержимое display-пикселя лежит в сцене на J правее/ниже;
//   2) история репроецируется векторами движения ПО ОПРЕДЕЛЕНИЮ prevUV = curUV + motion -
//      тому самому, что проверен warp-тестом (DECA_PROBE_MOTIONSHIFT);
//   3) история зажимается в 3x3-коробку соседей текущего кадра (neighborhood clamp) - против
//      шлейфов от дисокклюзий и движущихся объектов, у которых в ступени 1 векторов ещё нет;
//   4) экспоненциальное смешивание, где вес кадра МОДУЛИРОВАН БЛИЗОСТЬЮ его рендер-сэмпла к
//      центру display-пикселя (гауссиан ~0.4 рендер-пикселя). Это и есть источник резкости:
//      наивная аккумуляция БЕЗ веса свёртывает билинейный tent с равномерным распределением
//      джиттера, то есть РАСШИРЯЕТ ядро - и выходит мягче одиночного билинейного апскейла
//      (замерено: mean |grad| 4.00 против 4.66). С весом же аккумулятор статистически выбирает
//      кадры, чей джиттер попал в пиксель, и сходится к гауссову ядру уже РЕНДЕР-сэмплов.

Texture2D    _SceneTex;    // HDR-кадр сцены, РЕНДЕР-разрешение (после тумана/блума, до тонемапа).
SamplerState _SceneTex_sampler;

Texture2D    _HistoryTex;  // Аккумулированный кадр прошлого Execute, DISPLAY-разрешение.
SamplerState _HistoryTex_sampler;

Texture2D    _MotionTex;   // Векторы движения (RG16F, доли экрана), РЕНДЕР-разрешение.

// Зеркалит TemporalUpscaleConstantsData (TemporalUpscalePass.cs).
cbuffer TemporalUpscaleConstants
{
    float4 TuRender;   // xy - рендер-размер, zw - 1/рендер-размер.
    float4 TuFrame;    // xy - джиттер кадра в РЕНДЕР-пикселях (y вниз), z - alpha смешивания,
                       // w - есть ли история (0 -> взять текущий кадр как есть).
};

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

    float2 uv = input.ndc * float2(0.5, -0.5) + 0.5;

    // Вектор - из БЛИЖАЙШЕГО рендер-пикселя, без фильтрации: усреднение поперёк силуэта дало бы
    // движение там, где его нет (та же причина, что в MotionVectorDebugPS).
    int2 renderPixel = clamp(int2(uv * TuRender.xy), int2(0, 0), int2(TuRender.xy) - 1);
    float2 motion = _MotionTex.Load(int3(renderPixel, 0)).rg;

    // Анти-джиттер: проекция сдвинута на +J пикселей, значит содержимое несмещённого кадра для
    // этого uv лежит в текущем кадре на +J пикселей дальше. Оси совпадают: и джиттер, и uv - y вниз.
    float2 currentUv = uv + TuFrame.xy * TuRender.zw;
    float4 current = _SceneTex.SampleLevel(_SceneTex_sampler, currentUv, 0);

    // Коробка допустимых значений истории - 3x3 соседей ТЕКУЩЕГО кадра. Всё, что история помнит
    // за её пределами, - шлейф: дисокклюзия или объект, двигавшийся сам (векторов у таких в
    // ступени 1 нет).
    float3 neighborMin = current.rgb;
    float3 neighborMax = current.rgb;
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            int2 p = clamp(renderPixel + int2(x, y), int2(0, 0), int2(TuRender.xy) - 1);
            float3 c = _SceneTex.Load(int3(p, 0)).rgb;
            neighborMin = min(neighborMin, c);
            neighborMax = max(neighborMax, c);
        }
    }

    // Репроджекция истории по определению вектора (prevUV = curUV + motion). Ушедшее за кадр -
    // дисокклюзия по краю: истории нет, берём текущий кадр целиком.
    float2 prevUv = uv + motion;
    bool historyValid = TuFrame.w > 0.5 &&
        all(prevUv >= 0.0) && all(prevUv <= 1.0);

    // Близость ближайшего рендер-сэмпла этого кадра к точке, которую восстанавливает пиксель:
    // frac-смещение в [-0.5..0.5) рендер-пикселя, гауссиан с сигмой ~0.4. Пол 0.02 не даёт
    // истории застыть навечно там, куда фазы джиттера почти не попадают.
    float2 sampleOffset = frac(currentUv * TuRender.xy + 0.5) - 0.5;
    float proximity = max(exp(-dot(sampleOffset, sampleOffset) / 0.32), 0.02);

    float alpha = historyValid ? TuFrame.z * proximity : 1.0;

    float4 history = _HistoryTex.SampleLevel(_HistoryTex_sampler, prevUv, 0);
    history.rgb = clamp(history.rgb, neighborMin, neighborMax);

    // Альфа НЕ аккумулируется: превью компонует кадр по ней поверх подложки ImGui, и шлейф в
    // альфе рисовал бы полупрозрачный контур там, где геометрии уже нет.
    output.color = float4(lerp(history.rgb, current.rgb, alpha), current.a);
    return output;
}
