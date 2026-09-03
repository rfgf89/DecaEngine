// Probe debug view (see ProbeDebugVS.hlsl). Color key: ball = SH L0, red = invalid
// probe, cyan rim = relocated, additive tint = cascade volume.
struct PSInput
{
    float4 pos       : SV_POSITION;
    float3 normal    : NORMAL;
    float4 color     : COLOR0;
    float  offsetLen : TEXCOORD0;
    float3 tint      : TEXCOORD1;
};

float4 Main(PSInput input) : SV_TARGET
{
    float shade = 0.6 + 0.4 * saturate(input.normal.y * 0.5 + 0.5);
    float3 color = input.color.rgb * shade;

    float invalid = 1.0 - saturate(input.color.a);
    color = lerp(color, float3(1.0, 0.05, 0.05) * shade, invalid * invalid);

    float rim = 1.0 - abs(input.normal.y);
    color += float3(0.1, 0.6, 1.0) * saturate(input.offsetLen) * rim * 0.6;

    // Additive so cascade balls stay legible on dark probes.
    color += input.tint * shade * 0.35;

    return float4(color, 1.0);
}
