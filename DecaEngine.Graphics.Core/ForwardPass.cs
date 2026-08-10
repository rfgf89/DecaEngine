using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;

namespace DecaEngine.Core;

/// <summary>
/// Render-graph pass that clears/binds the back buffer and culls + draws every camera view.
/// Runs after <see cref="ShadowPass"/> so shadow maps are already populated.
/// </summary>
public sealed class ForwardPass : RenderGraphPass<ForwardPass.PassData>
{
	public override string Name => "Forward Pass";

	private readonly IBatchRenderer _batchRenderer;
	private readonly RenderCamerasData _renderScene;
	private readonly Ref<Vector2> _viewPortRef;
	private readonly IGpuTexture? _colorTarget;
	private readonly IGpuTexture? _depthTarget;
	private readonly IGpuTexture? _sceneCopy;
	private readonly SkyPassResources? _sky;
	private readonly SsaoPassResources? _ssao;
	private readonly IGpuTexture? _msaaColorTarget;
	private readonly IGpuTexture? _msaaDepthTarget;
	private readonly Vector4 _clearColor;

	public struct PassData
	{
	}

	public ForwardPass(IBatchRenderer batchRenderer, RenderCamerasData renderScene, Ref<Vector2> viewPortRef)
		: this(batchRenderer, renderScene, viewPortRef, null, null, new Vector4(0.1f, 0.1f, 0.1f, 1f))
	{
	}

	/// <summary>
	/// Overload used by off-screen consumers (see <see cref="DecaEngine.Editor.ModelPreviewViewport"/>)
	/// that need to draw into their own persistent color/depth targets instead of the swap chain -
	/// e.g. a separate, isolated render-graph instance rendering a .gltf/.glb preview for the
	/// Inspector, independent from the main Game View. When <paramref name="colorTarget"/> is null
	/// this behaves exactly like the swap-chain-writing constructor above.
	/// </summary>
	public ForwardPass(IBatchRenderer batchRenderer, RenderCamerasData renderScene, Ref<Vector2> viewPortRef,
		IGpuTexture? colorTarget, IGpuTexture? depthTarget, Vector4 clearColor, IGpuTexture? sceneCopy = null,
		SkyPassResources? sky = null, IGpuTexture? msaaColorTarget = null, IGpuTexture? msaaDepthTarget = null,
		SsaoPassResources? ssao = null)
	{
		_sky = sky;

		// AO рисуется инлайн МЕЖДУ opaque- и transmissive-дроу (см. SsaoPassResources.
		// WriteInlineCommands): стекло преломляет уже затенённый фон, но само экранным AO не
		// глушится - окклюзия рассеянного амбиента к преломлённому свету неприменима. Требует
		// refraction-пути (sceneCopy: композит читает снапшот), поэтому для swap-chain-пути
		// игнорируется вместе с ним.
		_ssao = colorTarget is not null && sceneCopy is not null ? ssao : null;

		// MSAA (опционально, только с офскрин-таргетом): геометрия рисуется в мультисемпловую пару,
		// затем резолвится в colorTarget (его сэмплируют ImGui/readback). Оба таргета обязаны быть
		// заданы вместе; без них - прежний одиночный путь.
		if (colorTarget is not null && msaaColorTarget is not null && msaaDepthTarget is not null)
		{
			_msaaColorTarget = msaaColorTarget;
			_msaaDepthTarget = msaaDepthTarget;
		}
		_batchRenderer = batchRenderer;
		_renderScene = renderScene;
		_viewPortRef = viewPortRef;
		_colorTarget = colorTarget;
		_depthTarget = depthTarget;
		_clearColor = clearColor;

		// Refraction-пасс имеет смысл только с явным офскрин-таргетом: back buffer свопчейна
		// копировать нечем/незачем в этом движке, так что для swap-chain-пути sceneCopy игнорируется.
		_sceneCopy = colorTarget is not null ? sceneCopy : null;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_batchRenderer.CheckAndReallocateBuffers();
		_batchRenderer.ClearIndirectDrawBuffers(cmd);

		// При включённом MSAA вся геометрия рисуется в мультисемпловую пару, а _colorTarget
		// становится resolve-приёмником в конце кадра.
		var renderColor = _msaaColorTarget ?? _colorTarget;
		var renderDepth = _msaaColorTarget is not null ? _msaaDepthTarget : _depthTarget;

		if (renderColor is not null)
		{
			cmd.SetRenderTarget(renderColor, renderDepth);
			cmd.ClearRenderTarget(renderColor, _clearColor);
			if (renderDepth is not null)
			{
				cmd.ClearDepthStencil(renderDepth, ClearDepthStencilFlags.Depth, 0.0f, 0);
			}
		}
		else
		{
			cmd.SetBackBufferTarget(context.Api);
			cmd.ClearBackBufferTarget(context.Api, _clearColor);
		}

		cmd.SetViewport(_viewPortRef);

		var views = _renderScene;
		if (views.IsCreated)
		{
			for (int i = 0; i < views.viewData.Capacity; i++)
			{
				_batchRenderer.SetupViewData(cmd, ref views.viewData.GetRef(i, false));
				_batchRenderer.SetupCullData(cmd, ref views.cullData.GetRef(i, false));
				_batchRenderer.SetupLightData(cmd, ref views.lightData.GetRef(i, false));

				// Фон-энвайронмент (см. SkyPassResources.Draw): в уже забинженный этим циклом render
				// target, ДО геометрии.
				_sky?.Draw(cmd);

				var cullResult = _batchRenderer.ExecuteComputeCulling(cmd);

				if (_sceneCopy is null)
				{
					_batchRenderer.ExecuteDrawBatching(cmd, cullResult);
					continue;
				}

				// Refraction-пасс: сначала opaque-материалы, затем снимок цветового таргета в
				// _sceneCopy, и только после - transmissive-материалы (см.
				// IBatchRenderer.SetMaterialTransparent), сэмплирующие этот снимок как "сцену за
				// стеклом" (_SceneColor в UnlitInstancedPS.hlsl). Копировать привязанный RT нельзя -
				// таргет отвязывается на время копии и привязывается обратно. С MSAA снимок
				// получается резолвом (мультисемпловую текстуру шейдер сэмплировать не может).
				//
				// Переход снимка в ShaderResource ДО opaque-дроу обязателен: _SceneColor статически
				// привязан в SRB всех материалов (в т.ч. opaque), и в первом кадре текстура ещё в
				// UNDEFINED - валидация Vulkan падает на самом первом дроу, не дойдя до копии.
				cmd.TransitionResource(_sceneCopy, ResourceState.ShaderResource);
				_batchRenderer.ExecuteDrawBatching(cmd, cullResult, BatchDrawFilter.OpaqueOnly);

				cmd.SetRenderTarget(null, null);
				if (_msaaColorTarget is not null)
				{
					cmd.ResolveTexture(_msaaColorTarget, _sceneCopy);
				}
				else
				{
					cmd.CopyTexture(_colorTarget, _sceneCopy);
				}
				cmd.TransitionResource(_sceneCopy, ResourceState.ShaderResource);

				// Экранное AO - здесь, а не пост-пассом поверх готового кадра: оценка по opaque-депту,
				// композит в render-таргет (читает свежий снапшот выше как _SceneTex), затем ПЕРЕ-съём
				// снапшота - transmissive-материалы преломляют уже затенённую сцену, но их собственные
				// вогнутости экранным AO не глушатся (см. SsaoPassResources.WriteInlineCommands).
				if (_ssao is not null)
				{
					_ssao.WriteInlineCommands(cmd, renderColor!, renderDepth!, _viewPortRef);

					cmd.SetRenderTarget(null, null);
					if (_msaaColorTarget is not null)
					{
						cmd.ResolveTexture(_msaaColorTarget, _sceneCopy);
					}
					else
					{
						cmd.CopyTexture(_colorTarget, _sceneCopy);
					}
					cmd.TransitionResource(_sceneCopy, ResourceState.ShaderResource);
				}

				cmd.SetRenderTarget(renderColor, renderDepth);
				cmd.SetViewport(_viewPortRef);
				_batchRenderer.ExecuteDrawBatching(cmd, cullResult, BatchDrawFilter.TransparentOnly);
			}
		}

		// Финальный резолв MSAA-кадра в одиночный _colorTarget - именно его сэмплируют
		// ImGui/readback; без MSAA геометрия рисовалась прямо в него, и резолв не нужен.
		if (_msaaColorTarget is not null)
		{
			cmd.SetRenderTarget(null, null);
			cmd.ResolveTexture(_msaaColorTarget, _colorTarget);
		}
	}
}
