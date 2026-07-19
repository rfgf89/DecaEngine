struct PSInput 
{ 
    float4 pos   : SV_POSITION; 
    float3 color : COLOR; 
};

PSInput main(in uint vertexId : SV_VertexID) 
{
    float4 pos[3];
    pos[0] = float4(-0.5, -0.5, 0.0, 1.0);
    pos[1] = float4( 0.0, +0.5, 0.0, 1.0);
    pos[2] = float4(+0.5, -0.5, 0.0, 1.0);

    float3 colors[3];
    colors[0] = float3(1.0, 0.0, 0.0); // red
    colors[1] = float3(0.0, 1.0, 0.0); // green
    colors[2] = float3(0.0, 0.0, 1.0); // blue

    PSInput result;
    result.pos   = pos[vertexId];
    result.color = colors[vertexId];
    return result;
}