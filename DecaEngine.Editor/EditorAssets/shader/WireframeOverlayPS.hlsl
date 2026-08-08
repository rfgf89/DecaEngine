// Flat-color pixel shader for the Model Preview "Highlight + Wireframe" overlay pass (see
// DecaEngine.Editor.ModelPreviewViewport). Paired with UnlitInstancedVS.hlsl and a PSO using
// RasterizerStateInfo.FillMode = Wireframe, drawn as a second pass on top of the normal
// Highlight-shaded solid mesh.
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
