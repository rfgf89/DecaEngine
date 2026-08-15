// Композит AO по одиночному (не-MSAA) депт-таргету - см. SsaoCompositeCommon.hlsl.
Texture2D<float> _DepthTex;

#define DEPTH_FETCH(pixel) (_DepthTex.Load(int3((pixel), 0)))

#include "SsaoCompositeCommon.hlsl"
