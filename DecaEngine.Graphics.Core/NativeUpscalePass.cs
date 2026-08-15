using System;
using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;

namespace DecaEngine.Core;

/// <summary>Бэкенд слота апскейлера, живущий В НАТИВНОЙ библиотеке (FSR через ffx-api, позже DLSS).
/// Реализация лежит в графическом бэкенде (см. FsrUpscalerBackend в DecaEngine.Graphics.Diligent) и
/// отдаётся конвейеру через <see cref="GraphicsPipelineSimple.SetNativeUpscaler"/> - Core о
/// нативных деталях не знает. Контракт входов тот же, что у встроенного TAAU (см.
/// <see cref="TemporalUpscalePassResources"/>): HDR-кадр сцены, глубина и векторы в
/// рендер-разрешении, джиттер кадра, выход - display-RGBA16F, который читает тонемап.</summary>
public interface INativeUpscalerBackend : IReleaseObject
{
	/// <summary>Имя для логов ("FSR 3.1.4" и т.п.).</summary>
	string DebugName { get; }

	/// <summary>Display-разрешение, RGBA16F с UAV - вход тонемапа при активном бэкенде.</summary>
	IRenderTarget OutputTarget { get; }

	/// <summary>Нативный диспатч - вызывается ИЗ РЕПЛЕЯ заморожённого буфера (см.
	/// ICommandBuffer.Callback), когда входы уже переведены в заявленные состояния. Обязан
	/// вернуть Diligent-контекст в согласованное состояние после себя (InvalidateState).</summary>
	void Dispatch();

	/// <summary>Покадровые параметры - зовётся из <see cref="GraphicsPipelineSimple.Execute"/>
	/// ПОСЛЕ наложения джиттера (джиттер именно этого кадра).</summary>
	void SetFrameParams(Vector2 jitterPixels);

	/// <summary>Длительность кадра в секундах - апскейлер темпоральный, ему нужна скорость
	/// (пуш из окружения, тем же путём, что dt авто-экспозиции).</summary>
	void SetDeltaTime(float seconds);

	/// <summary>Ресайз входов/выхода (пересоздаёт нативный контекст). Историю рвёт.</summary>
	void Resize(IGpuTexture sceneHdr, IGpuTexture depth, IGpuTexture motion,
		uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight);

	/// <summary>Следующий кадр пойдёт с флагом reset - там же, где рвётся история векторов.</summary>
	void ResetHistory();

	/// <summary>Non-null - бэкенду нужна ТИПИЗИРОВАННАЯ копия глубины (Diligent создаёт депт как
	/// R32_TYPELESS, и рантайм, строящий SRV по дескриптору ресурса, читал бы его нулями). Пасс
	/// копирует депт сюда перед диспатчем; бэкенд подаёт рантайму копию вместо оригинала.</summary>
	IGpuTexture? DepthProxy => null;

	/// <summary>Non-null - нулевые маски reactive/transparency, которые пасс ЧИСТИТ и переводит в
	/// ShaderResource в заморожённом буфере каждый кадр. Именно пасс, а не создание бэкенда:
	/// внеполосные команды на immediate-контексте посреди кадра редактора роняли процесс.</summary>
	IGpuTexture? ReactiveMask => null;

	/// <summary>См. <see cref="ReactiveMask"/>.</summary>
	IGpuTexture? TransparencyMask => null;
}

/// <summary>
/// Пасс-обёртка нативного апскейлера: объявляет графу входы/выход, переводит ресурсы в состояния,
/// заявленные бэкендом ffx-api (входы - ShaderResource, выход - UnorderedAccess), и врезает
/// нативный диспатч колбэк-командой. Сам ffx расставляет внутренние барьеры и ВОЗВРАЩАЕТ ресурсы
/// в заявленные состояния, поэтому после пасса графу не требуется ничего чинить.
///
/// Место в кадре - то же, что у <see cref="TemporalUpscalePass"/>: после всей сценовой
/// пост-обработки, перед тонемапом (аккумулировать надо линейный свет).
/// </summary>
public sealed class NativeUpscalePass : RenderGraphPass<NativeUpscalePass.PassData>
{
	public override string Name => "Native Upscale Pass";

	private readonly INativeUpscalerBackend _backend;
	private readonly IGpuTexture _sceneHdrTarget;
	private readonly IGpuTexture _depthTarget;
	private readonly IGpuTexture _motionTarget;

	public struct PassData
	{
	}

	public NativeUpscalePass(INativeUpscalerBackend backend, IGpuTexture sceneHdrTarget,
		IGpuTexture depthTarget, IGpuTexture motionTarget)
	{
		_backend = backend;
		_sceneHdrTarget = sceneHdrTarget;
		_depthTarget = depthTarget;
		_motionTarget = motionTarget;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		builder.ReadTarget(builder.ImportTexture(_sceneHdrTarget));
		builder.ReadTarget(builder.ImportTexture(_depthTarget));
		builder.ReadTarget(builder.ImportTexture(_motionTarget));
		builder.WriteTarget(builder.ImportTexture(_backend.OutputTarget));

		if (_backend.DepthProxy is { } proxy)
		{
			builder.WriteTarget(builder.ImportTexture(proxy));
		}

		if (_backend.ReactiveMask is { } reactive)
		{
			builder.WriteTarget(builder.ImportTexture(reactive));
		}

		if (_backend.TransparencyMask is { } transparency)
		{
			builder.WriteTarget(builder.ImportTexture(transparency));
		}

		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		// Состояния - ровно те, что бэкенд заявляет ffx-api (см. FsrUpscalerBackend.Dispatch).
		// Глубина в ShaderResource, а не DepthRead: пасс D3D12-only (векторов при MSAA нет, а шим
		// только под DX12), Vulkan-ограничение DEPTH_STENCIL_READ_ONLY сюда не дотягивается.
		cmd.SetRenderTarget(null, null);

		// Типизированная копия глубины - ДО остальных переходов: CopyTexture сам гоняет депт через
		// CopySource, а рантайм ждёт входы в ShaderResource (см. INativeUpscalerBackend.DepthProxy).
		if (_backend.DepthProxy is { } proxy)
		{
			cmd.CopyTexture(_depthTarget, proxy);
			cmd.TransitionResource(proxy, ResourceState.ShaderResource);
		}

		cmd.TransitionResource(_sceneHdrTarget, ResourceState.ShaderResource);
		cmd.TransitionResource(_depthTarget, ResourceState.ShaderResource);
		cmd.TransitionResource(_motionTarget, ResourceState.ShaderResource);
		cmd.TransitionResource(_backend.OutputTarget, ResourceState.UnorderedAccess);

		// Нулевые маски: чистятся В ГРАФЕ каждый кадр (копеечные клиры) - создание бэкенда не
		// делает внеполосных GPU-команд вовсе (см. INativeUpscalerBackend.ReactiveMask).
		if (_backend.ReactiveMask is { } reactiveMask)
		{
			cmd.ClearRenderTarget(reactiveMask, Vector4.Zero);
			cmd.TransitionResource(reactiveMask, ResourceState.ShaderResource);
		}

		if (_backend.TransparencyMask is { } transparencyMask)
		{
			cmd.ClearRenderTarget(transparencyMask, Vector4.Zero);
			cmd.TransitionResource(transparencyMask, ResourceState.ShaderResource);
		}

		cmd.Callback(_backend.Dispatch);
	}
}
