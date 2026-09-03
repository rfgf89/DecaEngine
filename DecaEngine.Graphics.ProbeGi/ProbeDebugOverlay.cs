using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Graphics.ProbeGi;

/// <summary>Probe-GI debug view: one octahedron per probe at its ACTUAL position (grid node plus
/// relocation offset). Color = SH L0, red = invalid ("in wall"), cyan rim = relocated. No vertex
/// buffer: one Draw(24 * probeCount), everything reconstructed from SV_VertexID.</summary>
public sealed class ProbeDebugOverlay : IDisposable
{
	private struct ProbeDebugParams
	{
		public Vector4 GridOriginRadius;
		public Vector4 GridCellCount;

		// xyz = probe grid size; the VS unpacks a probe index into a node from it.
		public Vector4 GridCounts;

		public Vector4 Tint;
	}

	private readonly IMaterialObject _material;
	private readonly uint _vertexCount;
	private readonly float _radius;
	private readonly Vector3 _tint;

	// Layout the cbuffer was filled for; without checking it, spheres would lag a scrolled volume.
	private int _layoutGeneration = -1;

	public ProbeDebugOverlay(DiligentGraphicsApi dilApi, IGraphicsApi api,
		IBatchRenderer batchRenderer, ProbeGiBakeSession session, ProbeGiTextures textures,
		TextureObjectFormat colorFormat, Vector3 tint = default)
	{
		_vertexCount = (uint)session.ProbeCount * 24u;

		var vs = api.CreateShader("Probe Debug VS", "EditorAssets/shader", "ProbeDebugVS.hlsl",
			ShaderObjectType.Vertex);
		var ps = api.CreateShader("Probe Debug PS", "EditorAssets/shader", "ProbeDebugPS.hlsl",
			ShaderObjectType.Pixel);

		_material = api.CreateMaterial("Probe Debug Material");
		_material.SetShader(vs, ps);
		_material.SetState(api.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Probe Debug PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.D32Float,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			// No culling: the PSO must not depend on the shader's vertex winding order.
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			// Reversed-Z, matching the scene (ForwardPass clears depth to zero).
			DepthStencilState = new DepthStencilStateInfo
			{
				DepthEnable = true,
				DepthFunc = ComparisonFunctionType.GreaterEqual,
			},
			InputLayout = [],
		}));

		batchRenderer.BindViewConstants(_material);

		_material.SetTexture("_ProbeOffset", textures.Offset, HandleAccess.Vertex);
		_material.SetTexture("_ProbeSh0", textures.Sh0, HandleAccess.Vertex);
		_material.SetTexture("_ProbeSh1", textures.Sh1, HandleAccess.Vertex);

		// Sphere radius scales with the minimum grid step so dense grids stay readable.
		_radius = textures.MinCellSize * 0.12f;
		_tint = tint;
		Refresh(session);
	}

	/// <summary>Re-uploads volume placement; effectively runs only after a layout change.</summary>
	public void Refresh(ProbeGiBakeSession session)
	{
		if (_layoutGeneration == session.LayoutGeneration)
		{
			return;
		}

		_layoutGeneration = session.LayoutGeneration;

		var constants = new ProbeDebugParams
		{
			GridOriginRadius = new Vector4(session.Origin, _radius),
			GridCellCount = new Vector4(session.Cell, session.ProbeCount),
			GridCounts = new Vector4(session.CountX, session.CountY, session.CountZ, 0f),
			Tint = new Vector4(_tint, 0f),
		};
		_material.SetConstant("ProbeDebugParams", ref constants, HandleAccess.Vertex);
	}

	/// <summary>Draws into the ALREADY bound render target (ForwardPass inline overlay).</summary>
	public void Draw(ICommandBuffer cmd)
	{
		cmd.SetPipelineState(_material);
		cmd.CommitShaderResources(_material);
		cmd.Draw(_vertexCount);
	}

	public void Dispose()
	{
		_material.Release();
	}
}
