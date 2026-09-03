// Temporal eye adaptation into a 1x1 target: exponential approach to the measured luminance.
// Ping-pong runs as two draws per frame (A->B, B->A, half dt each) because the render graph
// freezes the command buffer and bindings cannot alternate by frame parity.
#include "EyeAdaptationCommon.hlsl"

// End of the reduction chain: 1x1 average log2(luminance).
Texture2D _LumTex;

// Ping-pong partner: previous adapted luminance, linear (not log).
Texture2D _PrevTex;

PSOutput Main(in VSOutput input)
{
    PSOutput output;

    float avgLuminance = exp2(_LumTex.Load(int3(0, 0, 0)).r);
    float target = clamp(avgLuminance, EaParams.y, EaParams.z);

    float prev = _PrevTex.Load(int3(0, 0, 0)).r;

    // A fresh ping-pong target holds garbage and cannot be cleared once (frozen command
    // buffer; an out-of-graph clear breaks Vulkan layouts). "!(prev > 0)" also catches NaN.
    if (!(prev > 0.0))
    {
        prev = target;
    }
    prev = clamp(prev, EaParams.y, EaParams.z);

    float speed = target > prev ? EaParams2.y : EaParams2.z;
    float blend = saturate(1.0 - exp(-max(EaParams2.x, 0.0) * max(speed, 0.0)));

    output.color = (prev + (target - prev) * blend).xxxx;
    return output;
}
