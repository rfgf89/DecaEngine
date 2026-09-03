// Pairs with UnlitInstancedVS.hlsl and a FillMode=Wireframe PSO; drawn as a second
// pass over the Highlight-shaded solid mesh (ModelPreviewViewport).
struct PSInput
{
    float4 pos : SV_POSITION;
};

struct PSOutput
{
    float4 color : SV_TARGET;
};

PSOutput Main(in PSInput input)
{
    PSOutput output;
    output.color = float4(0.02, 0.02, 0.02, 1.0);
    return output;
}
