using System.Reflection;
using System.Runtime.Loader;
using DecaEngine;
using DecaEngine.Core;
using DiligentEngineNET.Samples;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using DecaEngine.Graphics;

var samplesMap = new Dictionary<string, Func<GraphicsBackend, Application>>()
{
    { "Triangle Sample", (backend)=> new TriangleSample(backend) },
    { "Cube Sample", (backend)=> new CubeSample(backend) },
    { "Cube Texture Sample", (backend)=> new CubeTextureSample(backend) },
    { "Instancing Sample", (backend)=> new InstancingSample(backend, 50) },
    { "Texture Sample", (backend)=> new TextureArraySample(backend, 50) },
    { "MultiThread Sample", (backend)=> new MultiThreadSample(backend, 50) },
    { "MultiThread Sample Draw Indexed", (backend) => new MultiThreadSampleDrawIndexed(backend, 100) },
};

var sampleFn = samplesMap[samplesMap.Keys.ToArray()[3]];
var sample = sampleFn(GraphicsBackend.D3D11);

sample.Setup();

sample.Run();
