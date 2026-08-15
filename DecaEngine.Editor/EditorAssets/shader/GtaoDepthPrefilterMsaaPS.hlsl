// Линеаризация МУЛЬТИСЕМПЛОВОГО депт-таргета (MSAA-режим превью) в mip 0 цепочки глубин:
// берётся нулевой сэмпл - для оценки заслонённости этого достаточно, а резолвить депт-буфер
// нечем. Единственное место всего GTAO-конвейера, которое вообще видит MSAA-депт: дальше по
// цепочке живёт уже обычная Texture2D. См. GtaoDepthPrefilterCommon.hlsl.
Texture2DMS<float> _DepthTex;

#define DEPTH_FETCH(pixel) (_DepthTex.Load(int2(pixel), 0))

#include "GtaoDepthPrefilterCommon.hlsl"
