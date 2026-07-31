struct PSInput
{
    float4 pos : SV_POSITION;
};

PSInput main(in uint vertexId : SV_VertexID)
{
    // Two triangles (6 vertices) forming a unit square, generated procedurally.
    float2 positions[6];
    positions[0] = float2(-1.0, -1.0);
    positions[1] = float2(-1.0, +1.0);
    positions[2] = float2(+1.0, +1.0);
    positions[3] = float2(-1.0, -1.0);
    positions[4] = float2(+1.0, +1.0);
    positions[5] = float2(+1.0, -1.0);

    PSInput result;
    result.pos = float4(positions[vertexId], 0.0, 1.0);
    return result;
}
