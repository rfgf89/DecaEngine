// Композит SSGI по МУЛЬТИСЕМПЛОВОМУ депт-таргету (MSAA-режим превью): берётся нулевой сэмпл -
// для билатерального веса этого достаточно, а резолвить депт-буфер нечем. Сам композит рисует
// в уже разрешённый 1x цветовой таргет (SSGI идёт после ForwardPass). См.
// SsgiCompositeCommon.hlsl.
Texture2DMS<float> _DepthTex;

#define DEPTH_FETCH(pixel) (_DepthTex.Load(int2(pixel), 0))

#include "SsgiCompositeCommon.hlsl"
