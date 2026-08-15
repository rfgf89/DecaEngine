// Общая кривая тонмапа превью. Раньше жила прямо в UnlitInstancedPS.hlsl и применялась в конце
// шейдинга; с HDR-конвейером (см. TonemapPass / EyeAdaptationPass) геометрия пишет ЛИНЕЙНЫЙ
// радианс в RGBA16F-таргет, а кривая применяется один раз фуллскрин-пассом после авто-экспозиции.
// Файл включают оба (UnlitInstancedPS - для LDR-режима, когда HDR-конвейер выключен, и
// TonemapPS - для HDR).
#ifndef TONEMAP_HLSL
#define TONEMAP_HLSL

// Khronos PBR Neutral tone mapper (https://github.com/KhronosGroup/ToneMapping) - the reference
// curve of the glTF Sample Viewer's "PBR Neutral" mode. Unlike Reinhard (which halves every
// midtone and is a big part of why the preview used to read as unlit), it passes values below
// ~0.76 through unchanged and only compresses the top of the range, preserving color saturation.
float3 PbrNeutralToneMap(float3 color)
{
    const float startCompression = 0.8 - 0.04;
    const float desaturation = 0.15;

    float x = min(color.r, min(color.g, color.b));
    float offset = x < 0.08 ? x - 6.25 * x * x : 0.04;
    color -= offset;

    float peak = max(color.r, max(color.g, color.b));
    if (peak < startCompression)
    {
        return color;
    }

    float d = 1.0 - startCompression;
    float newPeak = 1.0 - d * d / (peak + d - startCompression);
    color *= newPeak / peak;

    float g = 1.0 - 1.0 / (desaturation * (peak - newPeak) + 1.0);
    return lerp(color, newPeak.xxx, g);
}

// ACES, аппроксимация Narkowicz (2015) одной рациональной дробью. Классическая «киношная» кривая:
// заметный подъём контраста в средних тонах, глубокий носок и укатанные света. То, чего нарочно НЕ
// делает PBR Neutral, и то, из-за отсутствия чего кадр читается плоским.
//
// Расплата известна и её надо знать: аппроксимация уводит оттенок насыщенных ярких цветов
// (оранжевый тянет к жёлтому, красный к оранжевому) - она подгонялась по яркости, а не по
// цветовому тону. Для «покрасивее» это чаще плюс, для оценки материала - минус, поэтому кривая
// выбирается, а не назначается.
float3 AcesToneMap(float3 color)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate((color * (a * color + b)) / (color * (c * color + d) + e));
}

// AgX (Troy Sobotka), полиномиальная аппроксимация. Современный компромисс: тот же фильмический
// контраст, что у ACES, но БЕЗ сдвига оттенка - яркие насыщенные цвета уходят в белый через
// корректную десатурацию, а не через смену тона. Именно это отличает «дорогую» картинку: пересвет
// выглядит как пересвет, а не как цветное пятно.
//
// Три шага: приведение в рабочее пространство AgX, логарифмическая кодировка в диапазон
// [-12.47, +4.026] стопов, затем сигмоида-полином шестой степени по этому логу.
float3 AgxToneMap(float3 color)
{
    const float3x3 agxIn = float3x3(
        0.8425640, 0.0784000, 0.0792869,
        0.0423104, 0.8787391, 0.0791720,
        0.0423970, 0.0784000, 0.8791000);

    const float3x3 agxOut = float3x3(
         1.1968790, -0.0980210, -0.0990297,
        -0.0528968,  1.1519110, -0.0989634,
        -0.0529716, -0.0980186,  1.1510500);

    const float minEv = -12.47393;
    const float maxEv = 4.026069;

    color = mul(agxIn, max(color, 0.0));

    // Лог-кодировка. Пол 1e-10, а не ноль: log2(0) - это -inf, и он проходит сквозь полином в NaN.
    color = clamp(log2(max(color, 1e-10)), minEv, maxEv);
    color = (color - minEv) / (maxEv - minEv);

    // Сигмоида как полином 6-й степени по схеме Horner - дешевле и точнее, чем возведения в степень.
    float3 x = color;
    float3 x2 = x * x;
    float3 x4 = x2 * x2;
    color = 15.5 * x4 * x2
          - 40.14 * x4 * x
          + 31.96 * x4
          - 6.868 * x2 * x
          + 0.4298 * x2
          + 0.1191 * x
          - 0.00232;

    color = mul(agxOut, color);
    return saturate(color);
}

// Режимы кривой - зеркалит EditorSettings.ToneCurveMode. Выбор РАНТАЙМНЫЙ, а не кейвордом: кривая
// стоит считанные такты, а вариант шейдера на каждую пересобирал бы все PSO превью при движении
// выпадающего списка.
#define TONE_CURVE_PBR_NEUTRAL 0
#define TONE_CURVE_ACES        1
#define TONE_CURVE_AGX         2

float3 ApplyToneCurve(float3 color, int mode)
{
    if (mode == TONE_CURVE_ACES)
    {
        return AcesToneMap(color);
    }

    if (mode == TONE_CURVE_AGX)
    {
        return AgxToneMap(color);
    }

    return PbrNeutralToneMap(color);
}

// Фотометрическая яркость (Rec. 709) - на ней считается и авто-экспозиция (см. LuminanceInitPS).
float TonemapLuminance(float3 color)
{
    return dot(color, float3(0.2126, 0.7152, 0.0722));
}

#endif
