// SSAO variant for a single-sample (non-MSAA) depth target.
Texture2D<float> _DepthTex;

#define DEPTH_FETCH(pixel) (_DepthTex.Load(int3((pixel), 0)))

#include "SsaoCommon.hlsl"
