// Линеаризация ОДИНОЧНОГО (не-MSAA) депт-таргета в mip 0 цепочки глубин - см.
// GtaoDepthPrefilterCommon.hlsl.
Texture2D<float> _DepthTex;

#define DEPTH_FETCH(pixel) (_DepthTex.Load(int3((pixel), 0)))

#include "GtaoDepthPrefilterCommon.hlsl"
