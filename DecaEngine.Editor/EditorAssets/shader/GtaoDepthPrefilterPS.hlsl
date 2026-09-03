// Linearizes a single-sample (non-MSAA) depth target into mip 0 of the depth chain.
Texture2D<float> _DepthTex;

#define DEPTH_FETCH(pixel) (_DepthTex.Load(int3((pixel), 0)))

#include "GtaoDepthPrefilterCommon.hlsl"
