cbuffer Constants
{
    float4x4 ProjectionMatrix;
};

struct VSInput
{
    float2 pos : ATTRIB0;
    float2 uv  : ATTRIB1;
    uint col : ATTRIB2;
};

struct PSInput
{
    float4 pos : SV_POSITION;
    float2 uv  : TEX_COORD;
    float4 col : COLOR;
};

PSInput main(in VSInput input)
{
    PSInput output;
    output.pos = mul(float4(input.pos.xy, 0.f, 1.f), ProjectionMatrix);

    float r = (input.col & 0xFF) / 255.0;
    float g = ((input.col >> 8) & 0xFF) / 255.0;
    float b = ((input.col >> 16) & 0xFF) / 255.0;
    float a = ((input.col >> 24) & 0xFF) / 255.0;

    float4 inPut = float4(r,g,b,a);

    output.col = inPut;
    output.uv = input.uv;
    return output;
}