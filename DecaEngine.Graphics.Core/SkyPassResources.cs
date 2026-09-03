using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Owns the GPU resources for the procedural sky background (fullscreen equirect-sample
/// material). Created once by <see cref="GraphicsPipelineSimple"/> when a sky background is enabled -
/// unlike <see cref="SsaoPassResources"/> it draws inline inside <see cref="ForwardPass"/>'s per-view
/// loop rather than as its own render-graph pass, since it shares that pass's already-bound render
/// target instead of needing one of its own.</summary>
public sealed class SkyPassResources : IReleaseObject
{
	private readonly IMaterialObject _material;

	// colorFormat must match the target ForwardPass has bound, or the PSO is invalid.
	public SkyPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, IGpuTexture environmentMap,
		TextureObjectFormat colorFormat = TextureObjectFormat.R8G8B8A8UNorm)
	{
		var skyVs = graphicsApi.CreateShader("Sky Background VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var skyPs = graphicsApi.CreateShader("Sky Background PS", "EditorAssets/shader", "SkyBackgroundPS.hlsl", ShaderObjectType.Pixel);

		_material = graphicsApi.CreateMaterial("Sky Background Material");
		_material.SetShader(skyVs, skyPs);
		_material.SetState(graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Sky Background PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.D32Float,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		}));

		batchRenderer.BindViewConstants(_material);

		var skySampler = graphicsApi.CreateSampler(
			name: "_EnvMap_Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Wrap,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);
		_material.SetTexture("_EnvMap", environmentMap);
		_material.SetImmutableSampler("_EnvMap", skySampler);

		// SkySettings must be bound before the first Draw: otherwise the descriptor is empty
		// (VUID-vkCmdDraw-None-08114) and the shader reads garbage.
		_hdrOutput = colorFormat != TextureObjectFormat.R8G8B8A8UNorm;
		SetEnvironmentYaw(0f);
	}

	// Layout of the SkySettings cbuffer in SkyBackgroundPS.hlsl.
	private struct SkySettingsData
	{
		public float EnvYawRadians;

		// >0.5 writes linear luminance instead of a display encode.
		public float HdrOutput;

		public float Pad1, Pad2;
	}

	private bool _hdrOutput;
	private float _envYawRadians;

	/// <summary>Environment yaw around Y, radians; must match the model materials' PbrEnvYaw.</summary>
	public void SetEnvironmentYaw(float radians)
	{
		_envYawRadians = radians;
		PushSettings();
	}

	/// <summary>Whether the sky writes linear luminance.</summary>
	// Normally follows the pipeline HDR mode, but must be cleared for tonemap passthrough modes,
	// which copy the frame to the display target unencoded.
	public void SetHdrOutput(bool hdrOutput)
	{
		_hdrOutput = hdrOutput;
		PushSettings();
	}

	private void PushSettings()
	{
		var data = new SkySettingsData { EnvYawRadians = _envYawRadians, HdrOutput = _hdrOutput ? 1f : 0f };
		_material.SetConstant("SkySettings", ref data, HandleAccess.Pixel);
	}

	/// <summary>Draws the sky as a fullscreen triangle with depth test off.</summary>
	// Must run BEFORE geometry, and the caller must have bound the render target already.
	public void Draw(ICommandBuffer cmd)
	{
		cmd.SetPipelineState(_material);
		cmd.CommitShaderResources(_material);
		cmd.Draw(3);
	}

	public void Release()
	{
		_material.Release();
	}
}
