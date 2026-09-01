// Дебаг-линии (скелет, коллайдеры, контакты, лучи - см. DebugDraw/DebugLineOverlay). Вершины
// приходят УЖЕ мировыми: дебаг-геометрия собирается на CPU из систем с разными пространствами, и
// приводить их к общему знаменателю здесь было бы поздно. Пара - DebugLinePS.hlsl.
#include "Instancing.hlsl"

cbuffer View
{
    ViewData viewData;
}

cbuffer DebugLineParams
{
    // x - множитель яркости (см. DebugLinePS: линии пишутся в HDR-таргет ДО тонемапа), yzw - запас.
    float4 debugLineParams;
}

struct VSInput
{
    float3 pos : ATTRIB0;
    float4 color : ATTRIB1;
};

struct VSOutput
{
    float4 pos : SV_POSITION;
    float4 color : COLOR0;
};

VSOutput Main(in VSInput input)
{
    VSOutput output;

    // Альфа - НЕ прозрачность (блендинга в PSO нет), а признак живой вершины. Хвост буфера,
    // оставшийся от кадра с бОльшим числом линий, гасится нулевой альфой, и убрать его надо именно
    // здесь: число вершин в дроу зашито в замороженную команду графа и меняться каждый кадр не
    // может. Позиция за пределами клип-пространства - вершина отсекается целиком.
    if (input.color.a <= 0.0)
    {
        output.pos = float4(2.0, 2.0, 2.0, 1.0);
        output.color = float4(0.0, 0.0, 0.0, 0.0);
        return output;
    }

    output.pos = mul(float4(input.pos, 1.0), viewData.viewProj);
    output.color = float4(input.color.rgb * debugLineParams.x, 1.0);

    return output;
}
