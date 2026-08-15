using System;
using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;

namespace DecaEngine.Graphics.Diligent;

/// <summary>Нативный бэкенд слота апскейлера: AMD FSR через ffx-api и шим DecaFfxShim.dll (см.
/// native/DecaFfxShim/DecaFfxShim.cpp). D3D12-only. Хэндлы ресурсов идут через
/// ITexture.GetNativeHandle() (= ID3D12Resource*), командный лист кадра шим достаёт сам из
/// NativePointer immediate-контекста (Diligent::IDeviceContextD3D12::GetD3D12CommandList).
///
/// Владение: конвейер (см. GraphicsPipelineSimple.SetNativeUpscaler). Создавать через
/// <see cref="TryCreate"/> - вернёт null, если шим/DLL FSR не лежат рядом с экзешником или
/// бэкенд не D3D12, и конвейер молча останется на встроенном TAAU.</summary>
public sealed class FsrUpscalerBackend : INativeUpscalerBackend
{
	private const string ShimDll = "DecaFfxShim.dll";

	[DllImport(ShimDll, CallingConvention = CallingConvention.Cdecl)]
	private static extern int DecaFsr_Create(IntPtr anyResource, uint maxRenderW, uint maxRenderH,
		uint displayW, uint displayH, uint flags, int providerMajor, out IntPtr ctx);

	[DllImport(ShimDll, CallingConvention = CallingConvention.Cdecl)]
	private static extern int DecaFsr_Dispatch(IntPtr ctx, IntPtr diligentContext,
		IntPtr colorRes, IntPtr depthRes, IntPtr motionRes, IntPtr outputRes,
		IntPtr reactiveRes, IntPtr transparencyRes,
		float jitterX, float jitterY, float mvScaleX, float mvScaleY,
		uint renderW, uint renderH, uint upscaleW, uint upscaleH,
		float frameTimeDeltaMs, float cameraNear, float cameraFar, float fovYRad,
		int reset, int sharpen, float sharpness, int debugView);

	[DllImport(ShimDll, CallingConvention = CallingConvention.Cdecl)]
	private static extern void DecaFsr_Destroy(IntPtr ctx);

	[DllImport(ShimDll, CallingConvention = CallingConvention.Cdecl)]
	private static extern int DecaFsr_GetVersion(IntPtr ctx,
		[MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder buf, int bufLen);

	[DllImport(ShimDll, CallingConvention = CallingConvention.Cdecl)]
	private static extern IntPtr DecaFsr_LastMessage();

	[DllImport(ShimDll, CallingConvention = CallingConvention.Cdecl)]
	private static extern int DecaFsr_QueryVersion(IntPtr anyResource,
		[MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder buf, int bufLen);

	// Зеркало DecaFsrCreateFlags в шиме.
	private const uint FlagHdr = 1u << 0;
	private const uint FlagDepthInverted = 1u << 1;
	private const uint FlagDepthInfinite = 1u << 2;
	private const uint FlagAutoExposure = 1u << 3;
	private const uint FlagDebugChecking = 1u << 4;
	private const uint FlagDebugVisualization = 1u << 5;

	/// <summary>DECA_FSR_DEBUG_VIEW=1 - отладочная мозаика FSR вместо кадра (векторы, глубина,
	/// реактивность и т.д. глазами самого FSR) - главный диагност "что он на входе видит".</summary>
	private static readonly bool DebugView = Environment.GetEnvironmentVariable("DECA_FSR_DEBUG_VIEW") == "1";

	/// <summary>Диагностические множители знаков: DECA_FSR_JSIGN="x,y" - на джиттер,
	/// DECA_FSR_MVSIGN="x,y" - на масштаб векторов. Конвенции сверены аналитически, но перепутанный
	/// знак даёт ровно наблюдаемую кашу (история пересэмплируется мимо каждый кадр) - дешевле
	/// перебрать четвёрку, чем спорить с документацией.</summary>
	private static readonly Vector2 JitterSign = ParseSign("DECA_FSR_JSIGN");
	private static readonly Vector2 MvSign = ParseSign("DECA_FSR_MVSIGN");

	private static Vector2 ParseSign(string envVar)
	{
		var v = Environment.GetEnvironmentVariable(envVar);
		if (v is null) return Vector2.One;
		var parts = v.Split(',');
		return parts.Length == 2 &&
		       float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
			       System.Globalization.CultureInfo.InvariantCulture, out var x) &&
		       float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
			       System.Globalization.CultureInfo.InvariantCulture, out var y)
			? new Vector2(x, y)
			: Vector2.One;
	}

	private readonly DiligentGraphicsApi _api;
	private readonly float _cameraNear;
	private readonly float _fovYRad;

	private IntPtr _context;
	private IGpuTexture _sceneHdr = null!;
	private IGpuTexture _depth = null!;
	private IGpuTexture _motion = null!;

	// НУЛЕВЫЕ маски reactive/transparency рендер-размера. По контракту ffx-api они опциональны, но
	// официальный сэмпл AMD всегда подаёт обе, и без них ветка 3.1.x сводила кадр в кашу (см.
	// расследование в fsr-shim-integration): полная нечувствительность к остальным параметрам +
	// залитая плитка маски в её debug-мозаике. Нулевая маска семантически и есть "маски нет".
	private IRenderTarget? _reactiveMask;
	private IRenderTarget? _transparencyMask;

	/// <summary>Типизированная R32F-копия глубины - см. INativeUpscalerBackend.DepthProxy: сам депт
	/// Diligent создаёт R32_TYPELESS, и ветка 3.1.x читала бы его нулями.</summary>
	private IRenderTarget? _typedDepth;

	public IGpuTexture? DepthProxy => _typedDepth;

	private void CreateMasks(uint renderWidth, uint renderHeight)
	{
		_reactiveMask?.Release();
		_transparencyMask?.Release();
		_typedDepth?.Release();

		_typedDepth = _api.CreateRenderTarget(new TextureInfo
		{
			name = "FSR Typed Depth",
			width = renderWidth,
			height = renderHeight,
			format = TextureObjectFormat.R32Float,
		});

		_reactiveMask = _api.CreateRenderTarget(new TextureInfo
		{
			name = "FSR Reactive Mask (zero)",
			width = renderWidth,
			height = renderHeight,
			format = TextureObjectFormat.R8G8B8A8UNorm,
		});
		_transparencyMask = _api.CreateRenderTarget(new TextureInfo
		{
			name = "FSR Transparency Mask (zero)",
			width = renderWidth,
			height = renderHeight,
			format = TextureObjectFormat.R8G8B8A8UNorm,
		});

		// БЕЗ очистки и переходов здесь: create/resize зовутся ПОСРЕДИ кадра редактора (отложка в
		// Update, ресайз-путь), и внеполосные команды на immediate-контексте вперемешку с кадром и
		// ImGui роняли процесс AV-ом на ближайшем SetPipelineState. Очистку нулём и переходы делает
		// сам NativeUpscalePass в заморожённом буфере - каждый кадр, копейки (см. ReactiveMask).
	}

	/// <summary>Маски для пасса: он их чистит нулём и переводит в ShaderResource в заморожённом
	/// буфере перед диспатчем (см. INativeUpscalerBackend).</summary>
	public IGpuTexture? ReactiveMask => _reactiveMask;

	public IGpuTexture? TransparencyMask => _transparencyMask;
	private uint _renderWidth, _renderHeight, _displayWidth, _displayHeight;
	private Vector2 _jitterPixels;
	private float _deltaTimeMs = 1000f / 60f;
	private bool _resetNextFrame = true;
	private float _sharpness;

	/// <summary>Встроенный шарпен FSR (RCAS): 0 - выключен, 0..1 - сила. Живая ручка - уходит в
	/// параметры очередного диспатча.</summary>
	public void SetSharpness(float sharpness)
	{
		_sharpness = Math.Clamp(sharpness, 0f, 1f);
	}

	/// <summary>Мажор ветки провайдера, под который создан контекст: 0 - автополитика (новейший
	/// рабочий, см. шим), 2/3 - явный выбор из UI.</summary>
	private int _providerMajor;

	public int ProviderMajor => _providerMajor;

	/// <summary>Явный выбор ветки провайдера (0 - авто, 2 - FSR 2, 3 - FSR 3.1). Печётся в
	/// создание контекста - смена пересоздаёт его; вызывающий обязан сперва дождаться GPU (см.
	/// ModelViewportEnvironment.SetUpscalerTuning). История рвётся, подпись версии обновляется.</summary>
	public void SetProvider(int providerMajor)
	{
		if (providerMajor == _providerMajor)
		{
			return;
		}

		_providerMajor = providerMajor;
		Resize(_sceneHdr, _depth, _motion, _renderWidth, _renderHeight, _displayWidth, _displayHeight);
	}

	/// <summary>"FSR 2.3.4" - версия активного провайдера запрашивается у живого контекста после
	/// создания (см. DecaFsr_GetVersion), подпись видна в окне Graphics.</summary>
	public string DebugName { get; private set; }

	public IRenderTarget OutputTarget { get; }

	private FsrUpscalerBackend(DiligentGraphicsApi api, string debugName, IRenderTarget output,
		float cameraNear, float fovYRad)
	{
		_api = api;
		DebugName = debugName;
		OutputTarget = output;
		_cameraNear = cameraNear;
		_fovYRad = fovYRad;
	}

	/// <summary>Последнее сообщение рантайма FSR/шима - для логов пробы.</summary>
	public static string LastNativeMessage()
	{
		try
		{
			var ptr = DecaFsr_LastMessage();
			return ptr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(ptr) ?? "";
		}
		catch (DllNotFoundException)
		{
			return "DecaFfxShim.dll не найден";
		}
	}

	public static FsrUpscalerBackend? TryCreate(IGraphicsApi graphicsApi, string colorTargetName,
		IGpuTexture sceneHdr, IGpuTexture depth, IGpuTexture motion,
		uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight,
		float cameraNear, float fovYRad)
	{
		if (graphicsApi is not DiligentGraphicsApi api)
		{
			return null;
		}

		// Выход с UAV: FSR пишет compute-шейдером (см. DiligentResourceFormats - Compute в access
		// добавляет BindFlags.UnorderedAccess).
		var output = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " FSR Output",
			width = displayWidth,
			height = displayHeight,
			format = TextureObjectFormat.R16G16B16A16Float,
			access = HandleAccess.Pixel | HandleAccess.Compute,
		});

		var backend = new FsrUpscalerBackend(api, "FSR (ffx-api)", output, cameraNear, fovYRad);

		try
		{
			if (!backend.CreateContext(sceneHdr, depth, motion,
				    renderWidth, renderHeight, displayWidth, displayHeight, out var error))
			{
				Console.WriteLine($"[fsr] контекст не создан: {error}; {LastNativeMessage()}");
				output.Release();
				return null;
			}
		}
		catch (DllNotFoundException)
		{
			// Шим не собран/не скопирован - штатный случай, конвейер остаётся на TAAU.
			Console.WriteLine("[fsr] DecaFfxShim.dll не найден рядом с экзешником - нативный апскейлер недоступен");
			output.Release();
			return null;
		}

		var versions = new System.Text.StringBuilder(256);
		if (DecaFsr_QueryVersion(NativeHandleOf(sceneHdr), versions, versions.Capacity) > 0)
		{
			Console.WriteLine($"[fsr] провайдеры: {versions}");
		}

		backend.RefreshVersion();
		return backend;
	}

	private bool CreateContext(IGpuTexture sceneHdr, IGpuTexture depth, IGpuTexture motion,
		uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight, out string error)
	{
		_sceneHdr = sceneHdr;
		_depth = depth;
		_motion = motion;
		_renderWidth = renderWidth;
		_renderHeight = renderHeight;
		_displayWidth = displayWidth;
		_displayHeight = displayHeight;

		// Реверсивный бесконечный Z и линейный HDR-кадр - константы конвейера превью (см.
		// MakePerspectiveReversedZ и PipelineRenderTargets). Экспозиция - авто: свой 1x1-замер FSR
		// дешевле, чем прокидывать таргет адаптации.
		var flags = FlagHdr | FlagDepthInverted | FlagDepthInfinite | FlagAutoExposure;

		// DECA_FSR_FLAGS=<число> - полное переопределение флагов контекста (диагностика: бит0 hdr,
		// бит1 inverted, бит2 infinite, бит3 autoExposure, бит4 checking, бит5 debugView).
		if (uint.TryParse(Environment.GetEnvironmentVariable("DECA_FSR_FLAGS"), out var flagsOverride))
		{
			flags = flagsOverride;
		}

		if (Environment.GetEnvironmentVariable("DECA_FSR_DEBUG") == "1")
		{
			flags |= FlagDebugChecking;
		}

		if (DebugView)
		{
			flags |= FlagDebugVisualization;
		}

		CreateMasks(renderWidth, renderHeight);

		var rc = DecaFsr_Create(NativeHandleOf(sceneHdr), renderWidth, renderHeight,
			displayWidth, displayHeight, flags, _providerMajor, out _context);
		error = rc == 0 ? "" : $"код {rc}";
		return rc == 0;
	}

	/// <summary>ID3D12Resource* обёрнутой текстуры - им пользуется и <see cref="DlssUpscalerBackend"/>.</summary>
	internal static IntPtr NativeHandleOf(IGpuTexture texture)
	{
		var native = texture switch
		{
			DiligentRenderTarget rt => rt.Texture,
			DiligentGpuTexture t => t.Texture,
			_ => throw new ArgumentException($"Unexpected texture type {texture.GetType().Name}"),
		};

		return (IntPtr)(long)(ulong)native.GetNativeHandle();
	}

	/// <summary>Обновляет подпись: промо-имя поколения + технический номер провайдера через " / ".
	/// Номер провайдера ffx-рантайма - НЕ маркетинговая версия: 2.3.4 - это поддерживаемая ветка
	/// поколения "FSR 2", 3.1.x - "FSR 3.1", 4.x - "FSR 4".</summary>
	private void RefreshVersion()
	{
		if (_context == IntPtr.Zero)
		{
			return;
		}

		var version = new System.Text.StringBuilder(64);
		if (DecaFsr_GetVersion(_context, version, version.Capacity) != 0)
		{
			DebugName = "FSR (ffx-api)";
			return;
		}

		var raw = version.ToString();
		var promo = raw.Length > 0 ? raw[0] switch
		{
			'2' => "FSR 2",
			'3' => "FSR 3.1",
			'4' => "FSR 4",
			_ => "FSR",
		} : "FSR";

		DebugName = $"{promo} / {raw}";
	}

	public void SetFrameParams(Vector2 jitterPixels)
	{
		_jitterPixels = jitterPixels;
	}

	public void SetDeltaTime(float seconds)
	{
		_deltaTimeMs = MathF.Max(seconds, 1e-4f) * 1000f;
	}

	public void Dispatch()
	{
		if (_context == IntPtr.Zero)
		{
			return;
		}

		// Векторы у нас в UV-долях экрана (prevUV = curUV + motion, y вниз); FSR ждёт пиксели того
		// же направления и того же y-вниз - масштаб равен рендер-размеру. Джиттер - в пикселях, той
		// же конвенции, что вбита в проекцию (см. ApplyTemporalJitter: ndc = (+2jx/W, -2jy/H)).
		var rc = DecaFsr_Dispatch(_context,
			((global::Diligent.IDeviceContext)_api.ImmediateContext).NativePointer,
			NativeHandleOf(_sceneHdr),
			_typedDepth is not null ? NativeHandleOf(_typedDepth) : NativeHandleOf(_depth),
			NativeHandleOf(_motion),
			NativeHandleOf(OutputTarget),
			_reactiveMask is not null ? NativeHandleOf(_reactiveMask) : IntPtr.Zero,
			_transparencyMask is not null ? NativeHandleOf(_transparencyMask) : IntPtr.Zero,
			_jitterPixels.X * JitterSign.X, _jitterPixels.Y * JitterSign.Y,
			_renderWidth * MvSign.X, _renderHeight * MvSign.Y,
			_renderWidth, _renderHeight, _displayWidth, _displayHeight,
			// far - конечное большое число, не float.MaxValue: при DEPTH_INFINITE поле обещано
			// игнорируемым, но 3.4e38 в чужой формуле линеаризации - это inf/NaN на ровном месте.
			_deltaTimeMs, _cameraNear, 10000f, _fovYRad,
			_resetNextFrame ? 1 : 0, _sharpness > 0f ? 1 : 0, _sharpness, DebugView ? 1 : 0);

		_resetNextFrame = false;

		if (rc != 0)
		{
			Console.WriteLine($"[fsr] dispatch rc={rc}: {LastNativeMessage()}");
		}

		// Командный лист трогали мимо Diligent - сперва Flush (сабмит листа с чужими командами,
		// ровно как советует ворнинг "Invalidating context that has outstanding commands"), потом
		// InvalidateState по уже чистому контексту. ffx вернул ресурсы в заявленные состояния,
		// так что дальше графу чинить нечего.
		_api.ImmediateContext.Flush();
		_api.ImmediateContext.InvalidateState();
	}

	public void Resize(IGpuTexture sceneHdr, IGpuTexture depth, IGpuTexture motion,
		uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight)
	{
		// Нативный контекст пекёт максимальные размеры - пересоздаём. Вызывающий (ресайз-путь
		// вьюпорта) уже дождался GPU.
		if (_context != IntPtr.Zero)
		{
			DecaFsr_Destroy(_context);
			_context = IntPtr.Zero;
		}

		OutputTarget.Resize(new Vector2(displayWidth, displayHeight));

		if (!CreateContext(sceneHdr, depth, motion, renderWidth, renderHeight,
			    displayWidth, displayHeight, out var error))
		{
			Console.WriteLine($"[fsr] пересоздание контекста после ресайза не удалось: {error}; {LastNativeMessage()}");
		}

		RefreshVersion();
		ResetHistory();
	}

	public void ResetHistory()
	{
		_resetNextFrame = true;
	}

	public void Release()
	{
		if (_context != IntPtr.Zero)
		{
			DecaFsr_Destroy(_context);
			_context = IntPtr.Zero;
		}

		OutputTarget.Release();
		_reactiveMask?.Release();
		_reactiveMask = null;
		_transparencyMask?.Release();
		_transparencyMask = null;
		_typedDepth?.Release();
		_typedDepth = null;
	}
}
