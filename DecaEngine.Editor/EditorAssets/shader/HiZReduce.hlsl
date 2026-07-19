// HiZReduce.hlsl

RWTexture2D<float> outImage : register(u0);
Texture2D<float> inImage : register(t0);

[numthreads(8, 8, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint2 outCoord = dispatchThreadID.xy;
    uint2 inCoord = outCoord * 2;

    float d0 = inImage.Load(int3(inCoord.x,     inCoord.y,     0)).r;
    float d1 = inImage.Load(int3(inCoord.x + 1, inCoord.y,     0)).r;
    float d2 = inImage.Load(int3(inCoord.x,     inCoord.y + 1, 0)).r;
    float d3 = inImage.Load(int3(inCoord.x + 1, inCoord.y + 1, 0)).r;

    // Reversed-Z: ????? ??????? ????? ????? ??????????? ???????? Z (????? ? 0)
    float minDepth = min(min(d0, d1), min(d2, d3));

    outImage[outCoord] = minDepth;
}