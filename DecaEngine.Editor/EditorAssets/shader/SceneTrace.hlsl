// Трассировка лучей по сцене - ОДИН интерфейс, две реализации. Всё, что пускает лучи (обновление
// проб, кэш поверхностей, final gather), пишется против него один раз и не знает, на чём едет:
//
//   SCENE_TRACE_HARDWARE  - inline-трассировка (RayQuery, DXR 1.1) по TLAS. Основной путь на
//                           современном железе: обход делает сам GPU, динамика решается
//                           пересборкой TLAS (см. DiligentRayTracingScene).
//   иначе                 - обход собственного BVH в structured-буферах. Запасной путь для железа
//                           без аппаратной трассировки; структура и математика в точности те же,
//                           что у CPU-трассировщика в ProbeGiBaker, поэтому оба пути сверяются
//                           луч в луч (см. смоук-тест в PreviewProbe).
//
// Кейворд ставит вызывающий при компиляции варианта шейдера (см. IGraphicsApi.CreateShader с
// keywords), исходя из IGraphicsApi.RayTracing.

#ifndef SCENE_TRACE_INCLUDED
#define SCENE_TRACE_INCLUDED

// Результат трассировки. Нормаль и альбедо отдаются сразу: вызывающему они нужны всегда, а достать
// их самому он бы не смог - материалов у него нет.
struct SceneHit
{
    bool   hit;
    float  t;
    float3 position;
    float3 normal;
    float3 albedo;

    // Луч вышел изнутри геометрии (попал в заднюю грань) - для проб это признак «проба в стене».
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
    result.backface = false;
    return result;
}

#if SCENE_TRACE_HARDWARE

RaytracingAccelerationStructure _SceneTlas;

// Атрибуты попадания собираются через ИНДИРЕКЦИЮ по инстансу: BLAS строится на меш в ОБЪЕКТНОМ
// пространстве (см. ProbeSceneAccel), поэтому CommittedPrimitiveIndex нумерует примитивы внутри
// меша, а не сцены. Плюс индирекции - геометрия не зависит от поз, и движение объекта стоит одной
// пересборки TLAS вместо перестройки всей структуры; это и есть цена динамики.
struct BvhTriangle
{
    float3 a;      float pad0;
    float3 e1;     float pad1;
    float3 e2;     float pad2;
    float3 albedo; float pad3;
};

// Альбедо здесь у ИНСТАНСА, а не у треугольника: один меш законно стоит в сцене с разными
// материалами, а геометрия у его инстансов общая.
struct SceneInstance
{
    float3 albedo;
    uint   firstTriangle;   // база нумерации примитивов меша в _SceneMeshTriangles
};

StructuredBuffer<BvhTriangle>  _SceneMeshTriangles;
StructuredBuffer<SceneInstance> _SceneInstances;

SceneHit SceneTraceClosest(float3 origin, float3 direction, float tMax)
{
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0;
    ray.TMax = tMax;

    // Без отбрасывания задних граней: пробам нужно ЗНАТЬ о попадании изнутри геометрии (признак
    // «проба в стене»), ровно как в программном пути.
    RayQuery<RAY_FLAG_NONE> query;
    query.TraceRayInline(_SceneTlas, RAY_FLAG_NONE, 0xFF, ray);
    query.Proceed();

    if (query.CommittedStatus() != COMMITTED_TRIANGLE_HIT)
    {
        return SceneHitMiss();
    }

    SceneInstance instance = _SceneInstances[query.CommittedInstanceID()];
    BvhTriangle tri = _SceneMeshTriangles[instance.firstTriangle + query.CommittedPrimitiveIndex()];

    // Нормаль считается по рёбрам, УЖЕ перенесённым в мир, а не переносом готовой объектной нормали:
    // для нормали правильный перенос - обратно-транспонированной матрицей, и на неравномерном
    // масштабе наивный mul дал бы наклон. Через рёбра вопрос не встаёт вовсе - векторное
    // произведение переносится вместе с ними, - и заодно сохраняется обход вершин, от которого
    // зависит признак задней грани.
    float3x4 objectToWorld = query.CommittedObjectToWorld3x4();
    float3 e1 = mul((float3x3)objectToWorld, tri.e1);
    float3 e2 = mul((float3x3)objectToWorld, tri.e2);
    float3 n = normalize(cross(e1, e2));

    SceneHit result = SceneHitMiss();
    result.hit = true;
    result.t = query.CommittedRayT();
    result.position = origin + direction * result.t;
    result.normal = n;
    result.albedo = instance.albedo;
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

    // ACCEPT_FIRST_HIT - теневому лучу ближайшее попадание не нужно, любое обрывает обход.
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> query;
    query.TraceRayInline(_SceneTlas, RAY_FLAG_NONE, 0xFF, ray);
    query.Proceed();
    return query.CommittedStatus() == COMMITTED_TRIANGLE_HIT;
}

#else // ---- Программный обход BVH ---------------------------------------------------------------

// Раскладка обязана совпадать с BvhNodeGpu/BvhTriangleGpu (ProbeGi.cs).
struct BvhNode
{
    float3 boundsMin;
    int    left;      // < 0 - лист; start/count тогда задают срез в _SceneBvhOrder
    float3 boundsMax;
    int    start;     // внутренний узел: индекс ПРАВОГО ребёнка (левый - в left)
    int    count;

    // Паддинг ТРЕМЯ скалярами, а не int3: трёхкомпонентный вектор обязан лежать по адресу,
    // кратному 16, а здесь смещение 36 - SPIR-V такой буфер отвергает целиком, и шейдер молча
    // перестаёт работать (поймано сверкой с CPU).
    int    pad0, pad1, pad2;
};

struct BvhTriangle
{
    float3 a;      float pad0;
    float3 e1;     float pad1;
    float3 e2;     float pad2;
    float3 albedo; float pad3;
};

StructuredBuffer<BvhNode>     _SceneBvhNodes;
StructuredBuffer<uint>        _SceneBvhOrder;
StructuredBuffer<BvhTriangle> _SceneBvhTriangles;

// Глубина стека обхода - как у CPU-трассировщика (Span<int> stack на 64): BVH строится делением
// пополам, так что 64 уровня недостижимы на любой реальной сцене.
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

// Мёллер-Трумбор без предварительного отбрасывания задних граней: пробам нужно ЗНАТЬ о попадании в
// заднюю грань (признак «внутри геометрии»), а не молча его пропускать.
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
    // Деление на строгий ноль дало бы NaN в пересечении с коробкой; подменяем на крошечное со
    // знаком - ровно как в CPU-версии.
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
