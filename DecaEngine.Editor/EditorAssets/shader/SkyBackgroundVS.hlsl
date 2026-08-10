// Fullscreen-triangle VS for the preview's environment background pass (see ForwardPass /
// ModelViewportEnvironment.SkyMaterial): no vertex buffer, three SV_VertexID-generated corners
// covering the screen. Paired with SkyBackgroundPS.hlsl.
struct VSOutput
{
    float4 pos : SV_POSITION;
    float2 ndc : TEXCOORD0;
};

VSOutput Main(uint vertexId : SV_VertexID)
{
    VSOutput result;

    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    result.pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    result.ndc = result.pos.xy;

    return result;
}
