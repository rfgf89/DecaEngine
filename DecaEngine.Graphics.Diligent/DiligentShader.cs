using System;
using DecaEngine.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

public class DiligentShader : IShaderObject
{
	private readonly DiligentGraphicsPipeline _pipeline;
	private IShader? _nativeShader;

	public ShaderObjectType Type { get; }
	public string Name { get; }
	public string FilePath { get; }
	public string FactoryPath { get; }
	public string EntryPoint { get; }

	public IShader NativeShader => _nativeShader ?? throw new NullReferenceException("Shader is not compiled.");

	public DiligentShader(DiligentGraphicsPipeline pipeline, string name, string factoryPath, string file, ShaderObjectType type, string entryPoint = "Main")
	{
		_pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
		Name = name;
		FilePath = file;
		FactoryPath = factoryPath;
		Type = type;
		EntryPoint = entryPoint;
	}

	public void Compile()
	{
		if (_nativeShader != null)
		{
			return;
		}

		using var shaderSourceFactory = _pipeline.EngineFactory.CreateDefaultShaderSourceStreamFactory(Path.Combine(Environment.CurrentDirectory, FactoryPath));

		var diligentType = Type switch
		{
			ShaderObjectType.Vertex => global::Diligent.ShaderType.Vertex,
			ShaderObjectType.Pixel => global::Diligent.ShaderType.Pixel,
			ShaderObjectType.Compute => global::Diligent.ShaderType.Compute,
			ShaderObjectType.Geometry => global::Diligent.ShaderType.Geometry,
			ShaderObjectType.Domain => global::Diligent.ShaderType.Domain,
			ShaderObjectType.Hull => global::Diligent.ShaderType.Hull,
			_ => throw new ArgumentOutOfRangeException()
		};

		var shaderCi = new ShaderCreateInfo
		{
			SourceLanguage = ShaderSourceLanguage.Hlsl,
			Desc = new ShaderDesc
			{
				Name = Name,
				UseCombinedTextureSamplers = true,
				ShaderType = diligentType,
			},
			EntryPoint = EntryPoint,
			CompileFlags = ShaderCompileFlags.PackMatrixRowMajor,
			FilePath = FilePath,
			ShaderSourceStreamFactory = shaderSourceFactory,
		};

		_nativeShader = _pipeline.Device.CreateShader(shaderCi, out var compilerOutput);
		compilerOutput?.Dispose();
	}

	public void Release()
	{
		_nativeShader?.Dispose();
		_nativeShader = null;
	}
}