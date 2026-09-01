using System.Numerics;
using Friflo.Engine.ECS;

namespace DecaEngine.Editor.ECS;

public enum LightType
{
    Directional,
    Point,
    Spot
}

public struct LightComponent : IComponent
{
    public LightType Type;
    public Vector3 Color;
    public float Intensity;
    public float Range; // For point and spot lights
    public float SpotAngle; // For spot lights: FULL outer cone angle, degrees
    // Полный ВНУТРЕННИЙ угол конуса (градусы) - до него свет полный, дальше спад к внешнему.
    // 0 (дефолт свежедобавленного компонента) = авто: 80% внешнего (см. CullingAndRenderSystem).
    public float InnerSpotAngle;
    public float ShadowStrength; // 0.0 to 1.0
    // Видимый угловой ДИАМЕТР диска солнца, градусы - от него PCSS выводит ширину полутени
    // (см. UnlitInstancedPS.SampleWorldLightShadow). 0 (дефолт свежедобавленного компонента) =
    // авто: 1 градус (реальное солнце ~0.53, чуть крупнее - мягкость видна и на коротких тенях).
    public float SunAngularSize;
    // Мировой РАДИУС светящегося тела point/spot-света, метры - от него PCSS выводит ширину
    // полутени punctual-тени (см. кластерный цикл UnlitInstancedPS). 0 = авто: 5 см (лампочка);
    // крупным площадникам-панелям ставить их реальный полуразмер.
    public float SourceRadius;
}

public struct SunComponent : IComponent
{
}