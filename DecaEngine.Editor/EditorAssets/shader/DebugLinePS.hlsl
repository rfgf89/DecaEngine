// Пиксель дебаг-линии - см. DebugLineVS.hlsl. Ничего, кроме интерполированного цвета: линия должна
// выглядеть ровно тем цветом, которым её задала система, иначе кодировка (спящее тело серое,
// невалидная цель красная) перестаёт читаться.
struct PSInput
{
    float4 pos : SV_POSITION;
    float4 color : COLOR0;
};

float4 Main(in PSInput input) : SV_TARGET
{
    return input.color;
}
