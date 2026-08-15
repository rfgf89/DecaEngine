// Общий кбуфер трёх шагов авто-экспозиции (см. EyeAdaptationPass.cs - структура
// EyeAdaptationConstantsData зеркалит его байт-в-байт). Один и тот же layout у всех материалов
// цепочки: редукция яркости (LuminanceInitPS/LuminanceReducePS) и временная адаптация
// (EyeAdaptationPS) читают из него разные поля, зато пуш констант в C# один на всех.
#ifndef EYE_ADAPTATION_COMMON_HLSL
#define EYE_ADAPTATION_COMMON_HLSL

cbuffer EyeAdaptation
{
    // xy = размер ТАРГЕТА пасса в пикселях, zw = 1/xy.
    float4 EaTarget;

    // xy = размер ИСТОЧНИКА (текстуры, которую пасс читает), zw = 1/xy. У init-пасса источник -
    // HDR-кадр (меняется на ресайзе вьюпорта), у редукции - предыдущее звено цепочки.
    float4 EaSource;

    // x = key value (средняя яркость, к которой приводится кадр), y = нижний кламп измеренной
    // яркости, z = верхний, w = экспокоррекция в стопах (EV).
    float4 EaParams;

    // x = дельта времени кадра в секундах, y = скорость адаптации к свету (яркость выросла),
    // z = скорость адаптации к темноте, w - резерв.
    float4 EaParams2;
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

// Нижний предел яркости под логарифмом: в кадре с чёрным фоном (превью очищается прозрачным
// чёрным) log2(0) = -inf отравил бы среднее по всему таргету.
static const float EaLuminanceEpsilon = 1e-4;

#endif
