// Первое звено цепочки глубин XeGTAO (XeGTAO_PrefilterDepths16x16, mip 0): сырой депт-буфер
// один раз линеаризуется в ВЬЮ-СПЕЙСНУЮ глубину и кладётся в отдельный таргет, из которого
// дальше читают и построитель мипов (GtaoDepthMipPS.hlsl), и сам GTAO (GtaoCommon.hlsl).
//
// Зачем отдельный проход, а не линеаризация на месте:
//  - главный пасс делает SliceCount*StepsPerSlice*2 выборок глубины на пиксель, и каждая иначе
//    тянула бы за собой деление; здесь оно выполняется ровно один раз на пиксель;
//  - MSAA перестаёт течь дальше по конвейеру: мультисемпловый депт читается ТОЛЬКО здесь
//    (см. GtaoDepthPrefilterMsaaPS.hlsl), а все последующие пассы работают с обычной Texture2D
//    и не нуждаются в парных обёртках;
//  - и главное - без линейной глубины в отдельном таргете невозможна мип-цепочка, на которой
//    держится выбор уровня по дальности сэмпла в главном пассе.
//
// Обёртки (GtaoDepthPrefilterPS.hlsl / GtaoDepthPrefilterMsaaPS.hlsl) определяют DEPTH_FETCH -
// ровно тот же приём, что у пары GtaoPS/GtaoMsaaPS до переезда на цепочку.
#include "Instancing.hlsl"

cbuffer View
{
    ViewData viewData;
}

// CameraData near в ModelViewportEnvironment - тот же infinite reversed-Z, что и в SsaoCommon.hlsl.
static const float PrefilterNearPlane = 0.05;

// Потолок вью-спейсной глубины: таргет цепочки - RGBA16F, и всё, что не влезает в half,
// превратилось бы в inf, а inf в фильтре мипов отравляет весь взвешенный средний (см.
// XeGTAO_ClampDepth, там ровно та же константа).
static const float MaxViewDepth = 65504.0;

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
    int2 pixel = int2(input.pos.xy);
    float raw = DEPTH_FETCH(pixel);

    // Фон: reversed-Z очищается нулём, то есть бесконечно далеко. Именно потолок, а не «ноль»:
    // так фон честно проигрывает любой falloff-проверке в главном пассе вместо того, чтобы
    // притвориться вплотную прижатым окклюдером.
    float z = raw < 1e-7 ? MaxViewDepth : min(PrefilterNearPlane / raw, MaxViewDepth);

    PSOutput output;
    output.color = float4(z, 0.0, 0.0, 1.0);
    return output;
}
