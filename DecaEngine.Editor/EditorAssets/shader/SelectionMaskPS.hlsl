// Пара к SelectionMaskVS.hlsl: заливает силуэт выделенного объекта единицей - контур из маски
// вытягивает SelectionOutlinePS.hlsl.
float4 Main(float4 pos : SV_POSITION) : SV_TARGET
{
    return float4(1.0, 1.0, 1.0, 1.0);
}
