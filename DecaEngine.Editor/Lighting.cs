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
    public float SpotAngle; // For spot lights
    public float ShadowStrength; // 0.0 to 1.0
}

public struct SunComponent : IComponent
{
}