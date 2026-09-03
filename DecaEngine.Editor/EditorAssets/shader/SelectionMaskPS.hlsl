// Pair of SelectionMaskVS.hlsl: fills the selected object's silhouette with 1;
// SelectionOutlinePS.hlsl extracts the outline from the mask.
float4 Main(float4 pos : SV_POSITION) : SV_TARGET
{
    return float4(1.0, 1.0, 1.0, 1.0);
}
