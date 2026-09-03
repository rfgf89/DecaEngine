// One scene tracing interface, two implementations: inline RayQuery over a TLAS when
// SCENE_TRACE_HARDWARE is set, otherwise a software BVH walk matching the CPU tracer.

#ifndef SCENE_TRACE_INCLUDED
#define SCENE_TRACE_INCLUDED

struct SceneHit
{
    bool   hit;
    float  t;
    float3 position;
    float3 normal;
    float3 albedo;

    // Textured albedo, hardware path only; textureIndex -1 means no texture.
    float2 uv;
    int    textureIndex;
    float3 baseColorFactor;

    // Per-triangle; the software path leaves these at 0 and 1 (fully rough).
    float  metalness;
    float  roughness;

    // Interpolated vertex normal for shading; the software path duplicates the geometric one.
    float3 smoothNormal;

    // Hit a back face, i.e. the ray started inside geometry.
    bool   backface;
};

SceneHit SceneHitMiss()
{
    SceneHit result;
    result.hit = false;
    result.t = 0.0;
    result.position = float3(0.0, 0.0, 0.0);
    result.normal = float3(0.0, 1.0, 0.0);
    result.albedo = float3(0.0, 0.0, 0.0);
    result.uv = float2(0.0, 0.0);
    result.textureIndex = -1;
    result.baseColorFactor = float3(1.0, 1.0, 1.0);
    result.metalness = 0.0;
    result.roughness = 1.0;
    result.smoothNormal = float3(0.0, 1.0, 0.0);
    result.backface = false;
    return result;
}

#if SCENE_TRACE_HARDWARE

RaytracingAccelerationStructure _SceneTlas;

// BLAS is per mesh in object space, so CommittedPrimitiveIndex numbers primitives within the
// mesh; the instance table supplies the base index.
struct BvhTriangle
{
    // uvA/uvB/uvC hold the vertex UVs of (A, A+e1, A+e2) as two halves packed in a float.
    float3 a;      float uvA;
    float3 e1;     float uvB;
    float3 e2;     float uvC;
    float3 albedo; float metalness;
    // nA/nB/nC are object-space octahedral vertex normals; zero on the software path.
    float nA; float nB; float nC; float roughness;
};

float3 SceneUnpackOctNormal(float bits)
{
    uint packed = asuint(bits);
    float2 p = float2(f16tof32(packed & 0xFFFFu), f16tof32(packed >> 16));
    float3 n = float3(p, 1.0 - abs(p.x) - abs(p.y));
    if (n.z < 0.0)
    {
        float2 s = float2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
        n.xy = (1.0 - abs(p.yx)) * s;
    }

    return normalize(n);
}

// Material lives on the instance: one mesh may appear with different materials.
struct SceneInstance
{
    float3 albedo;
    uint   firstTriangle;   // base index of this mesh in _SceneMeshTriangles

    // Mirrors InstanceGpu in ProbeSceneAccel; textureIndex -1 means no texture.
    float3 baseColorFactor;
    int    textureIndex;
};

float2 SceneUnpackUv(float bits)
{
    uint packed = asuint(bits);
    return float2(f16tof32(packed & 0xFFFFu), f16tof32(packed >> 16));
}

StructuredBuffer<BvhTriangle>  _SceneMeshTriangles;
StructuredBuffer<SceneInstance> _SceneInstances;

SceneHit SceneTraceClosest(float3 origin, float3 direction, float tMax)
{
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0;
    ray.TMax = tMax;

    // No back-face culling: probes must learn about hits from inside geometry.
    RayQuery<RAY_FLAG_NONE> query;
    query.TraceRayInline(_SceneTlas, RAY_FLAG_NONE, 0xFF, ray);
    query.Proceed();

    if (query.CommittedStatus() != COMMITTED_TRIANGLE_HIT)
    {
        return SceneHitMiss();
    }

    SceneInstance instance = _SceneInstances[query.CommittedInstanceID()];
    BvhTriangle tri = _SceneMeshTriangles[instance.firstTriangle + query.CommittedPrimitiveIndex()];

    // Normal from world-space edges, not a transformed object normal: avoids the inverse-transpose
    // issue under non-uniform scale and preserves winding for the back-face test.
    float3x4 objectToWorld = query.CommittedObjectToWorld3x4();
    float3 e1 = mul((float3x3)objectToWorld, tri.e1);
    float3 e2 = mul((float3x3)objectToWorld, tri.e2);
    float3 n = normalize(cross(e1, e2));

    SceneHit result = SceneHitMiss();
    result.hit = true;
    result.t = query.CommittedRayT();
    result.position = origin + direction * result.t;
    result.normal = n;
    // Per-triangle albedo, matching the software path; instance albedo is one flat color.
    result.albedo = tri.albedo;

    // DXR convention: bary.x weights vertex A+e1, bary.y weights A+e2.
    float2 bary = query.CommittedTriangleBarycentrics();
    float2 uvA = SceneUnpackUv(tri.uvA);
    result.uv = uvA + (SceneUnpackUv(tri.uvB) - uvA) * bary.x
                    + (SceneUnpackUv(tri.uvC) - uvA) * bary.y;
    result.textureIndex = instance.textureIndex;
    result.baseColorFactor = instance.baseColorFactor;
    result.metalness = tri.metalness;
    result.roughness = tri.roughness;

    // mul(row-vector, WorldToObject3x4) is the inverse-transpose transform normals require;
    // clamped to the geometric hemisphere, since interpolation can flip it on silhouettes.
    float3 nSmoothObj = SceneUnpackOctNormal(tri.nA) * (1.0 - bary.x - bary.y)
                      + SceneUnpackOctNormal(tri.nB) * bary.x
                      + SceneUnpackOctNormal(tri.nC) * bary.y;
    float3x4 worldToObject = query.CommittedWorldToObject3x4();
    float3 nSmooth = normalize(mul(nSmoothObj, (float3x3)worldToObject));
    result.smoothNormal = dot(nSmooth, n) < 0.0 ? n : nSmooth;

    result.backface = dot(n, direction) > 0.0;
    return result;
}

bool SceneTraceAnyHit(float3 origin, float3 direction, float tMax)
{
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0;
    ray.TMax = tMax;

    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> query;
    query.TraceRayInline(_SceneTlas, RAY_FLAG_NONE, 0xFF, ray);
    query.Proceed();
    return query.CommittedStatus() == COMMITTED_TRIANGLE_HIT;
}

#else // ---- Software BVH traversal ---------------------------------------------------------------

// Layout must match BvhNodeGpu/BvhTriangleGpu (ProbeGi.cs).
struct BvhNode
{
    float3 boundsMin;
    int    left;      // < 0 = leaf; start/count then slice into _SceneBvhOrder
    float3 boundsMax;
    int    start;     // inner node: index of the right child
    int    count;

    // Three scalars, not an int3: a float3 at offset 36 breaks 16-byte alignment and SPIR-V
    // rejects the whole buffer.
    int    pad0, pad1, pad2;
};

struct BvhTriangle
{
    // Layout must match BvhTriangleGpu (80 bytes); the padding here is the hardware path's data.
    float3 a;      float pad0;
    float3 e1;     float pad1;
    float3 e2;     float pad2;
    float3 albedo; float pad3;
    float pad4; float pad5; float pad6; float pad7;
};

StructuredBuffer<BvhNode>     _SceneBvhNodes;
StructuredBuffer<uint>        _SceneBvhOrder;
StructuredBuffer<BvhTriangle> _SceneBvhTriangles;

// Matches the CPU tracer's stack depth; median splits keep real scenes well under 64 levels.
#define SCENE_BVH_STACK 64

bool SceneRayBox(float3 origin, float3 invDir, float tMax, BvhNode node)
{
    float3 t0 = (node.boundsMin - origin) * invDir;
    float3 t1 = (node.boundsMax - origin) * invDir;
    float3 tsmall = min(t0, t1);
    float3 tbig = max(t0, t1);
    float tmin = max(max(tsmall.x, tsmall.y), tsmall.z);
    float tmax = min(min(tbig.x, tbig.y), tbig.z);
    return tmax >= max(tmin, 0.0) && tmin <= tMax;
}

// Moller-Trumbore without back-face culling: probes need to see hits from inside geometry.
float SceneRayTri(float3 origin, float3 direction, BvhTriangle tri)
{
    float3 pv = cross(direction, tri.e2);
    float det = dot(tri.e1, pv);
    if (abs(det) < 1e-12)
    {
        return -1.0;
    }

    float invDet = 1.0 / det;
    float3 tv = origin - tri.a;
    float u = dot(tv, pv) * invDet;
    if (u < 0.0 || u > 1.0)
    {
        return -1.0;
    }

    float3 qv = cross(tv, tri.e1);
    float v = dot(direction, qv) * invDet;
    if (v < 0.0 || u + v > 1.0)
    {
        return -1.0;
    }

    return dot(tri.e2, qv) * invDet;
}

float3 SceneSafeInvDir(float3 direction)
{
    // Exact zero would make the slab test NaN; substitute a signed epsilon, as the CPU tracer does.
    float3 d;
    d.x = abs(direction.x) < 1e-12 ? (direction.x < 0.0 ? -1e-12 : 1e-12) : direction.x;
    d.y = abs(direction.y) < 1e-12 ? (direction.y < 0.0 ? -1e-12 : 1e-12) : direction.y;
    d.z = abs(direction.z) < 1e-12 ? (direction.z < 0.0 ? -1e-12 : 1e-12) : direction.z;
    return 1.0 / d;
}

SceneHit SceneTraceClosest(float3 origin, float3 direction, float tMax)
{
    float3 invDir = SceneSafeInvDir(direction);

    float hitT = tMax;
    int hitTri = -1;

    int stack[SCENE_BVH_STACK];
    int sp = 0;
    stack[sp++] = 0;

    [loop]
    while (sp > 0)
    {
        BvhNode node = _SceneBvhNodes[stack[--sp]];
        if (!SceneRayBox(origin, invDir, hitT, node))
        {
            continue;
        }

        if (node.left < 0)
        {
            [loop]
            for (int i = node.start; i < node.start + node.count; i++)
            {
                uint triIndex = _SceneBvhOrder[i];
                float t = SceneRayTri(origin, direction, _SceneBvhTriangles[triIndex]);
                if (t > 0.0 && t < hitT)
                {
                    hitT = t;
                    hitTri = (int)triIndex;
                }
            }
        }
        else if (sp + 2 <= SCENE_BVH_STACK)
        {
            stack[sp++] = node.left;
            stack[sp++] = node.start;
        }
    }

    if (hitTri < 0)
    {
        return SceneHitMiss();
    }

    BvhTriangle tri = _SceneBvhTriangles[hitTri];
    float3 n = normalize(cross(tri.e1, tri.e2));

    SceneHit result = SceneHitMiss();
    result.hit = true;
    result.t = hitT;
    result.position = origin + direction * hitT;
    result.normal = n;
    result.smoothNormal = n;
    result.albedo = tri.albedo;
    result.backface = dot(n, direction) > 0.0;
    return result;
}

bool SceneTraceAnyHit(float3 origin, float3 direction, float tMax)
{
    float3 invDir = SceneSafeInvDir(direction);

    int stack[SCENE_BVH_STACK];
    int sp = 0;
    stack[sp++] = 0;

    [loop]
    while (sp > 0)
    {
        BvhNode node = _SceneBvhNodes[stack[--sp]];
        if (!SceneRayBox(origin, invDir, tMax, node))
        {
            continue;
        }

        if (node.left < 0)
        {
            [loop]
            for (int i = node.start; i < node.start + node.count; i++)
            {
                float t = SceneRayTri(origin, direction, _SceneBvhTriangles[_SceneBvhOrder[i]]);
                if (t > 0.0 && t < tMax)
                {
                    return true;
                }
            }
        }
        else if (sp + 2 <= SCENE_BVH_STACK)
        {
            stack[sp++] = node.left;
            stack[sp++] = node.start;
        }
    }

    return false;
}

#endif // SCENE_TRACE_HARDWARE

#endif // SCENE_TRACE_INCLUDED
