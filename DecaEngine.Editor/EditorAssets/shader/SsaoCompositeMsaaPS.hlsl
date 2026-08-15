// Композит AO по МУЛЬТИСЕМПЛОВОМУ депт-таргету (MSAA-режим превью): нулевой сэмпл, как в
// GtaoMsaaPS/SsaoMsaaPS - для билатерального веса этого достаточно, а резолвить депт нечем.
// См. SsaoCompositeCommon.hlsl.
Texture2DMS<float> _DepthTex;

#define DEPTH_FETCH(pixel) (_DepthTex.Load(int2(pixel), 0))

#include "SsaoCompositeCommon.hlsl"
