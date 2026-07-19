struct PSInput 
{ 
    float4 pos   : SV_POSITION; 
    float3 color : COLOR; 
};

struct PSOutput
{ 
    float4 color : SV_TARGET; 
};

PSOutput main(in PSInput input)
{
    PSOutput output;
    output.color = float4(input.color, 1.0);
    return output;
}
