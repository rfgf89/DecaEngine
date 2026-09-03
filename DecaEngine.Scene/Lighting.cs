using System.Numerics;
using Friflo.Engine.ECS;

namespace DecaEngine.Scene;

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
    // Full INNER cone angle, degrees; 0 = auto (80% of the outer angle).
    public float InnerSpotAngle;
    public float ShadowStrength; // 0.0 to 1.0
    // Apparent angular DIAMETER of the sun disc, degrees, driving PCSS penumbra; 0 = auto (1 deg).
    public float SunAngularSize;
    // World RADIUS of the punctual emitter in metres, driving PCSS penumbra; 0 = auto (5 cm).
    public float SourceRadius;
}

public struct SunComponent : IComponent
{
}