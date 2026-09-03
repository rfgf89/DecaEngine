// Debug lines pass through the color unshaded: it encodes state and must stay exact.
struct PSInput
{
    float4 pos : SV_POSITION;
    float4 color : COLOR0;
};

float4 Main(in PSInput input) : SV_TARGET
{
    return input.color;
}
