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

// Sample List
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

/*Console.WriteLine("- Choose one of samples:");
var sampleKeys = samplesMap.Keys.ToArray();
for (var i = 0; i < sampleKeys.Length; i++)
    Console.WriteLine($"[{i}] {sampleKeys[i]}");
var selectedKey = (uint)Convert.ToInt32(Console.ReadLine());
if(selectedKey >= samplesMap.Count)
    throw new Exception("Invalid selection");

Console.WriteLine("- Choose Graphics Backend:");
var backendTypes = Enum.GetValues<GraphicsBackend>();
for(var i = 0; i < backendTypes.Length; i++)
    Console.WriteLine($"[{i}] {backendTypes[i]}");

var selectedBackend = (uint)Convert.ToInt32(Console.ReadLine());
if(selectedBackend >= backendTypes.Length)
    throw new Exception("Invalid selection");

Console.Clear();
Console.WriteLine($"[{selectedBackend}] Running sample: {sampleKeys[selectedKey]}");*/

// Finally run sample
var sampleFn = samplesMap[samplesMap.Keys.ToArray()[3]];
var sample = sampleFn(GraphicsBackend.D3D11);

sample.Setup();

//var platform = new DecaAssemblyLoadContext("sf");
//platform.Load();

sample.Run();
