// HiZReducePS.hlsl

Texture2D<float> inImage : register(t0);
SamplerState inImage_sampler : register(s0);

struct PSInput
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD0;
};

float main(PSInput input) : SV_Target
{
    float4 samples = inImage.Gather(inImage_sampler, input.UV, int2(0, 0));
    return min(min(samples.x, samples.y), min(samples.z, samples.w));
}