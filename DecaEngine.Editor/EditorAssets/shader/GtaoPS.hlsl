// GTAO по одиночному (не-MSAA) депт-таргету - см. GtaoCommon.hlsl.
Texture2D<float> _DepthTex;

#define DEPTH_FETCH(pixel) (_DepthTex.Load(int3((pixel), 0)))

#include "GtaoCommon.hlsl"
