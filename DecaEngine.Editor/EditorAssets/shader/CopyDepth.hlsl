// CopyDepth.hlsl

RWTexture2D<float> outImage : register(u0);
Texture2D<float> inImage : register(t0);

[numthreads(8, 8, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint2 coord = dispatchThreadID.xy;
    outImage[coord] = inImage.Load(int3(coord, 0)).r;
}