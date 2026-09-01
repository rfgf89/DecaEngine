using System;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Animation;
using DecaEngine.Graphics;

namespace DecaEngine.Editor
{
    public static class Test
    {
        public const int NumCubes = 1024;
        public const int NumSpheres = 1024;
        public const int NumCapsules = 1024;
        public const int NumCylinders = 1024;

        public static DiligentMesh SphereMesh { get; private set; }
        public static DiligentMaterial SphereMaterial { get; private set; }
        
        public static DiligentMesh CubeMesh { get; private set; }
        public static DiligentMaterial CubeMaterial { get; private set; }

        public static DiligentMesh CapsuleMesh { get; private set; }
        public static DiligentMaterial CapsuleMaterial { get; private set; }

        public static DiligentMesh CylinderMesh { get; private set; }
        public static DiligentMaterial CylinderMaterial { get; private set; }

        public static void Initialize(DiligentGraphicsApi diligentGraphicsApi)
        {
            // Sphere
            var sphereShaderPs = new DiligentShader(diligentGraphicsApi, "Sphere Shader Ps",
                "EditorAssets/shader", "SpherePS.hlsl", ShaderObjectType.Pixel);
            var sphereShaderVs = new DiligentShader(diligentGraphicsApi, "Sphere Shader Vs",
                "EditorAssets/shader", "SphereVS.hlsl", ShaderObjectType.Vertex);

            SphereMaterial = new DiligentMaterial("Sphere Material", diligentGraphicsApi);
            SphereMaterial.SetShader(sphereShaderPs);
            SphereMaterial.SetShader(sphereShaderVs);

            SphereMesh = new DiligentMesh("Sphere", diligentGraphicsApi.Device);
            CreateSphere(SphereMesh, 32, 32);
            
            // Cube
            var cubeShaderPs = new DiligentShader(diligentGraphicsApi, "Cube Shader Ps",
                "EditorAssets/shader", "CubeInstancePS.hlsl", ShaderObjectType.Pixel);
            var cubeShaderVs = new DiligentShader(diligentGraphicsApi, "Cube Shader Vs",
                "EditorAssets/shader", "CubeInstanceVS.hlsl", ShaderObjectType.Vertex);

            CubeMaterial = new DiligentMaterial("Cube Material", diligentGraphicsApi);
            CubeMaterial.SetShader(cubeShaderPs);
            CubeMaterial.SetShader(cubeShaderVs);

            CubeMesh = new DiligentMesh("Cube", diligentGraphicsApi.Device);
            CreateCube(CubeMesh);

            // Capsule
            CapsuleMaterial = new DiligentMaterial("Capsule Material", diligentGraphicsApi);
            CapsuleMaterial.SetShader(cubeShaderPs);
            CapsuleMaterial.SetShader(cubeShaderVs);

            CapsuleMesh = new DiligentMesh("Capsule", diligentGraphicsApi.Device);
            CreateCapsule(CapsuleMesh, 2.0f, 0.5f, 32);

            // Cylinder
            CylinderMaterial = new DiligentMaterial("Cylinder Material", diligentGraphicsApi);
            CylinderMaterial.SetShader(cubeShaderPs);
            CylinderMaterial.SetShader(cubeShaderVs);

            CylinderMesh = new DiligentMesh("Cylinder", diligentGraphicsApi.Device);
            CreateCylinder(CylinderMesh, 2.0f, 0.5f, 32);
        }

        private static void CreateCube(DiligentMesh mesh)
        {
            mesh.SetIndices(new uint[]
            {
                2, 0, 1, 2, 3, 0,
                4, 6, 5, 4, 7, 6,
                8, 10, 9, 8, 11, 10,
                12, 14, 13, 12, 15, 14,
                16, 18, 17, 16, 19, 18,
                20, 21, 22, 20, 22, 23
            });

            mesh.SetVertices(new Vertex[]
            {
                new(new Vector3(-1, -1, -1), new Vector2(0, 1)),
                new(new Vector3(-1, +1, -1), new Vector2(0, 0)),
                new(new Vector3(+1, +1, -1), new Vector2(1, 0)),
                new(new Vector3(+1, -1, -1), new Vector2(1, 1)),

                new(new Vector3(-1, -1, -1), new Vector2(0, 1)),
                new(new Vector3(-1, -1, +1), new Vector2(0, 0)),
                new(new Vector3(+1, -1, +1), new Vector2(1, 0)),
                new(new Vector3(+1, -1, -1), new Vector2(1, 1)),

                new(new Vector3(+1, -1, -1), new Vector2(0, 1)),
                new(new Vector3(+1, -1, +1), new Vector2(1, 1)),
                new(new Vector3(+1, +1, +1), new Vector2(1, 0)),
                new(new Vector3(+1, +1, -1), new Vector2(0, 0)),

                new(new Vector3(+1, +1, -1), new Vector2(0, 1)),
                new(new Vector3(+1, +1, +1), new Vector2(0, 0)),
                new(new Vector3(-1, +1, +1), new Vector2(1, 0)),
                new(new Vector3(-1, +1, -1), new Vector2(1, 1)),

                new(new Vector3(-1, +1, -1), new Vector2(1, 0)),
                new(new Vector3(-1, +1, +1), new Vector2(0, 0)),
                new(new Vector3(-1, -1, +1), new Vector2(0, 1)),
                new(new Vector3(-1, -1, -1), new Vector2(1, 1)),

                new(new Vector3(-1, -1, +1), new Vector2(1, 1)),
                new(new Vector3(+1, -1, +1), new Vector2(0, 1)),
                new(new Vector3(+1, +1, +1), new Vector2(0, 0)),
                new(new Vector3(-1, +1, +1), new Vector2(1, 0)),
            });
        }

        private static void CreateSphere(DiligentMesh mesh, int segments, int rings)
        {
            var vertices = new System.Collections.Generic.List<Vertex>();
            var indices = new System.Collections.Generic.List<uint>();

            for (int i = 0; i <= rings; i++)
            {
                float v = (float)i / rings;
                float phi = v * MathF.PI;

                for (int j = 0; j <= segments; j++)
                {
                    float u = (float)j / segments;
                    float theta = u * (MathF.PI * 2);

                    float x = MathF.Cos(theta) * MathF.Sin(phi);
                    float y = MathF.Cos(phi);
                    float z = MathF.Sin(theta) * MathF.Sin(phi);

                    vertices.Add(new Vertex(new Vector3(x, y, z), new Vector2(u, v)));
                }
            }

            for (uint i = 0; i < rings; i++)
            {
                for (uint j = 0; j < segments; j++)
                {
                    uint r1s1 = (uint)(i * (segments + 1)) + j;
                    uint r1s2 = (uint)(i * (segments + 1)) + (j + 1);
                    uint r2s1 = (uint)((i + 1) * (segments + 1)) + j;
                    uint r2s2 = (uint)((i + 1) * (segments + 1)) + (j + 1);

                    indices.Add(r1s1);
                    indices.Add(r1s2);
                    indices.Add(r2s1);

                    indices.Add(r1s2);
                    indices.Add(r2s2);
                    indices.Add(r2s1);
                }
            }

            mesh.SetVertices(vertices.ToArray());
            mesh.SetIndices(indices.ToArray());
        }

        private static void CreateCapsule(DiligentMesh mesh, float height, float radius, int segments)
        {
            var vertices = new System.Collections.Generic.List<Vertex>();
            var indices = new System.Collections.Generic.List<uint>();
            
            int rings = segments / 2;
            float halfHeight = height / 2 - radius;

            // Top hemisphere
            for (int i = 0; i <= rings; i++)
            {
                float v = (float)i / rings;
                float phi = v * (MathF.PI / 2);

                for (int j = 0; j <= segments; j++)
                {
                    float u = (float)j / segments;
                    float theta = u * (MathF.PI * 2);

                    float x = MathF.Cos(theta) * MathF.Sin(phi) * radius;
                    float y = MathF.Cos(phi) * radius + halfHeight;
                    float z = MathF.Sin(theta) * MathF.Sin(phi) * radius;

                    vertices.Add(new Vertex(new Vector3(x, y, z), new Vector2(u, v)));
                }
            }

            // Bottom hemisphere
            for (int i = 0; i <= rings; i++)
            {
                float v = (float)i / rings;
                float phi = (MathF.PI / 2) + v * (MathF.PI / 2);

                for (int j = 0; j <= segments; j++)
                {
                    float u = (float)j / segments;
                    float theta = u * (MathF.PI * 2);

                    float x = MathF.Cos(theta) * MathF.Sin(phi) * radius;
                    float y = MathF.Cos(phi) * radius - halfHeight;
                    float z = MathF.Sin(theta) * MathF.Sin(phi) * radius;

                    vertices.Add(new Vertex(new Vector3(x, y, z), new Vector2(u, v)));
                }
            }

            // Cylinder part
            for (int j = 0; j <= segments; j++)
            {
                float u = (float)j / segments;
                float theta = u * (MathF.PI * 2);

                float x = MathF.Cos(theta) * radius;
                float z = MathF.Sin(theta) * radius;

                vertices.Add(new Vertex(new Vector3(x, halfHeight, z), new Vector2(u, 0.5f)));
                vertices.Add(new Vertex(new Vector3(x, -halfHeight, z), new Vector2(u, 0.5f)));
            }
            
            // Indices for hemispheres
            for (uint i = 0; i < rings; i++)
            {
                for (uint j = 0; j < segments; j++)
                {
                    uint r1s1 = (uint)(i * (segments + 1)) + j;
                    uint r1s2 = (uint)(i * (segments + 1)) + (j + 1);
                    uint r2s1 = (uint)((i + 1) * (segments + 1)) + j;
                    uint r2s2 = (uint)((i + 1) * (segments + 1)) + (j + 1);

                    indices.Add(r1s1);
                    indices.Add(r1s2);
                    indices.Add(r2s1);

                    indices.Add(r1s2);
                    indices.Add(r2s2);
                    indices.Add(r2s1);
                }
            }
            
            uint offset = (uint)((rings + 1) * (segments + 1));
            for (uint i = 0; i < rings; i++)
            {
                for (uint j = 0; j < segments; j++)
                {
                    uint r1s1 = offset + (uint)(i * (segments + 1)) + j;
                    uint r1s2 = offset + (uint)(i * (segments + 1)) + (j + 1);
                    uint r2s1 = offset + (uint)((i + 1) * (segments + 1)) + j;
                    uint r2s2 = offset + (uint)((i + 1) * (segments + 1)) + (j + 1);

                    indices.Add(r1s1);
                    indices.Add(r2s1);
                    indices.Add(r1s2);

                    indices.Add(r1s2);
                    indices.Add(r2s1);
                    indices.Add(r2s2);
                }
            }

            // Indices for cylinder
            offset = (uint)(2 * (rings + 1) * (segments + 1));
            for (uint j = 0; j < segments; j++)
            {
                uint i1 = offset + j * 2;
                uint i2 = offset + j * 2 + 1;
                uint i3 = offset + (j + 1) * 2;
                uint i4 = offset + (j + 1) * 2 + 1;

                indices.Add(i1);
                indices.Add(i3);
                indices.Add(i2);

                indices.Add(i3);
                indices.Add(i4);
                indices.Add(i2);
            }

            mesh.SetVertices(vertices.ToArray());
            mesh.SetIndices(indices.ToArray());
        }

        private static void CreateCylinder(DiligentMesh mesh, float height, float radius, int segments)
        {
            var vertices = new System.Collections.Generic.List<Vertex>();
            var indices = new System.Collections.Generic.List<uint>();
            float halfHeight = height / 2;

            // Top cap
            vertices.Add(new Vertex(new Vector3(0, halfHeight, 0), new Vector2(0.5f, 0.5f)));
            for (int i = 0; i <= segments; i++)
            {
                float angle = (float)i / segments * 2.0f * MathF.PI;
                float x = MathF.Cos(angle) * radius;
                float z = MathF.Sin(angle) * radius;
                vertices.Add(new Vertex(new Vector3(x, halfHeight, z), new Vector2((x / radius + 1) / 2, (z / radius + 1) / 2)));
            }

            // Bottom cap
            vertices.Add(new Vertex(new Vector3(0, -halfHeight, 0), new Vector2(0.5f, 0.5f)));
            for (int i = 0; i <= segments; i++)
            {
                float angle = (float)i / segments * 2.0f * MathF.PI;
                float x = MathF.Cos(angle) * radius;
                float z = MathF.Sin(angle) * radius;
                vertices.Add(new Vertex(new Vector3(x, -halfHeight, z), new Vector2((x / radius + 1) / 2, (z / radius + 1) / 2)));
            }

            // Sides
            for (int i = 0; i <= segments; i++)
            {
                float angle = (float)i / segments * 2.0f * MathF.PI;
                float x = MathF.Cos(angle) * radius;
                float z = MathF.Sin(angle) * radius;
                vertices.Add(new Vertex(new Vector3(x, halfHeight, z), new Vector2((float)i / segments, 0)));
                vertices.Add(new Vertex(new Vector3(x, -halfHeight, z), new Vector2((float)i / segments, 1)));
            }

            // Top cap indices
            for (uint i = 1; i <= segments; i++)
            {
                indices.Add(0);
                indices.Add(i + 1);
                indices.Add(i);
            }

            // Bottom cap indices
            uint bottomCenterIndex = (uint)(segments + 2);
            for (uint i = 1; i <= segments; i++)
            {
                indices.Add(bottomCenterIndex);
                indices.Add(bottomCenterIndex + i);
                indices.Add(bottomCenterIndex + i + 1);
            }

            // Side indices
            uint sideStartIndex = (uint)(2 * (segments + 2));
            for (uint i = 0; i < segments; i++)
            {
                uint tl = sideStartIndex + i * 2;
                uint tr = sideStartIndex + (i + 1) * 2;
                uint bl = sideStartIndex + i * 2 + 1;
                uint br = sideStartIndex + (i + 1) * 2 + 1;

                indices.Add(tl);
                indices.Add(tr);
                indices.Add(bl);

                indices.Add(tr);
                indices.Add(br);
                indices.Add(bl);
            }

            mesh.SetVertices(vertices.ToArray());
            mesh.SetIndices(indices.ToArray());
        }

        private struct Vertex
        {
            public Vector3 Pos;
            public Vector2 Uv;

            public Vertex(Vector3 pos, Vector2 uv)
            {
                Pos = pos;
                Uv = uv;
            }
        }
    }
}