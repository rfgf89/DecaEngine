// SSAO по МУЛЬТИСЕМПЛОВОМУ депт-таргету (MSAA-режим превью): берётся нулевой сэмпл - для
// оценки заслонённости этого достаточно, а резолвить депт-буфер нечем. См. SsaoCommon.hlsl.
Texture2DMS<float> _DepthTex;

#define DEPTH_FETCH(pixel) (_DepthTex.Load(int2(pixel), 0))

#include "SsaoCommon.hlsl"
