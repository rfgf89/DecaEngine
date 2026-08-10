// SSGI по МУЛЬТИСЕМПЛОВОМУ депт-таргету (MSAA-режим превью): берётся нулевой сэмпл - для
// сбора bounce-света этого достаточно, а резолвить депт-буфер нечем. См. SsgiCommon.hlsl.
Texture2DMS<float> _DepthTex;

#define DEPTH_FETCH(pixel) (_DepthTex.Load(int2(pixel), 0))

#include "SsgiCommon.hlsl"
