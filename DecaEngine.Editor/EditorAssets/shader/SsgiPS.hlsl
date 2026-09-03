// SSGI over a single-sample (non-MSAA) depth target - see SsgiCommon.hlsl.
Texture2D<float> _DepthTex;

#define DEPTH_FETCH(pixel) (_DepthTex.Load(int3((pixel), 0)))

#include "SsgiCommon.hlsl"
