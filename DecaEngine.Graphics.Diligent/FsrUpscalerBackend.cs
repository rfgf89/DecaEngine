using System;
using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Core;

namespace DecaEngine.Graphics.Diligent;

/// <summary>AMD FSR upscaler via ffx-api and DecaFfxShim.dll; D3D12 only, TryCreate returns null otherwise.</summary>
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

	// Mirrors DecaFsrCreateFlags in the shim.
	private const uint FlagHdr = 1u << 0;
	private const uint FlagDepthInverted = 1u << 1;
	private const uint FlagDepthInfinite = 1u << 2;
	private const uint FlagAutoExposure = 1u << 3;
	private const uint FlagDebugChecking = 1u << 4;
	private const uint FlagDebugVisualization = 1u << 5;

	// DECA_FSR_DEBUG_VIEW=1 renders FSR's debug mosaic (its view of the inputs).
	private static readonly bool DebugView = Environment.GetEnvironmentVariable("DECA_FSR_DEBUG_VIEW") == "1";

	// DECA_FSR_JSIGN/MVSIGN="x,y": diagnostic sign flips for jitter and MV scale.
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

	// Optional per ffx-api, but the 3.1.x provider garbles the frame unless both are bound.
	private IRenderTarget? _reactiveMask;
	private IRenderTarget? _transparencyMask;

	// Typed R32F depth copy: Diligent's depth is R32_TYPELESS and 3.1.x reads it as zeros.
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

		// No clears or transitions here: create/resize run mid-frame, and out-of-band commands on
		// the immediate context crash on the next SetPipelineState. NativeUpscalePass does it.
	}

	/// <summary>Zero mask; the upscale pass clears and transitions it before dispatch.</summary>
	public IGpuTexture? ReactiveMask => _reactiveMask;

	public IGpuTexture? TransparencyMask => _transparencyMask;
	private uint _renderWidth, _renderHeight, _displayWidth, _displayHeight;
	private Vector2 _jitterPixels;
	private float _deltaTimeMs = 1000f / 60f;
	private bool _resetNextFrame = true;
	private float _sharpness;

	/// <summary>Built-in FSR sharpening (RCAS): 0 disables, 0..1 sets strength.</summary>
	public void SetSharpness(float sharpness)
	{
		_sharpness = Math.Clamp(sharpness, 0f, 1f);
	}

	// Provider branch the context was created for: 0 is auto (newest working one).
	private int _providerMajor;

	public int ProviderMajor => _providerMajor;

	/// <summary>Picks the provider branch (0 auto, 2, 3); recreates the context, so idle the GPU first.</summary>
	public void SetProvider(int providerMajor)
	{
		if (providerMajor == _providerMajor)
		{
			return;
		}

		_providerMajor = providerMajor;
		Resize(_sceneHdr, _depth, _motion, _renderWidth, _renderHeight, _displayWidth, _displayHeight);
	}

	/// <summary>Signature of the active provider, e.g. "FSR 2 / 2.3.4".</summary>
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

	/// <summary>Last message reported by the FSR runtime or the shim.</summary>
	public static string LastNativeMessage()
	{
		try
		{
			var ptr = DecaFsr_LastMessage();
			return ptr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(ptr) ?? "";
		}
		catch (DllNotFoundException)
		{
			return "DecaFfxShim.dll not found";
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

		// UAV output: FSR writes it from a compute shader.
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
				Console.WriteLine($"[fsr] context not created: {error}; {LastNativeMessage()}");
				output.Release();
				return null;
			}
		}
		catch (DllNotFoundException)
		{
			// Missing shim is a normal case: the pipeline stays on TAAU.
			Console.WriteLine("[fsr] DecaFfxShim.dll not found next to the executable - native upscaler unavailable");
			output.Release();
			return null;
		}

		var versions = new System.Text.StringBuilder(256);
		if (DecaFsr_QueryVersion(NativeHandleOf(sceneHdr), versions, versions.Capacity) > 0)
		{
			Console.WriteLine($"[fsr] providers: {versions}");
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

		// Reversed infinite Z and linear HDR are pipeline constants; auto exposure saves a target.
		var flags = FlagHdr | FlagDepthInverted | FlagDepthInfinite | FlagAutoExposure;

		// DECA_FSR_FLAGS=<n> overrides context flags (see the Flag* constants above).
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
		error = rc == 0 ? "" : $"code {rc}";
		return rc == 0;
	}

	/// <summary>ID3D12Resource* of the wrapped texture.</summary>
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

	// The ffx provider number is not the marketing version: 2.3.4 is the "FSR 2" branch.
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

		// Motion is UV, y down (prevUV = curUV + motion); FSR wants pixels, so scale = render size.
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
			// far stays finite: float.MaxValue turns into inf/NaN inside FSR's linearization.
			_deltaTimeMs, _cameraNear, 10000f, _fovYRad,
			_resetNextFrame ? 1 : 0, _sharpness > 0f ? 1 : 0, _sharpness, DebugView ? 1 : 0);

		_resetNextFrame = false;

		if (rc != 0)
		{
			Console.WriteLine($"[fsr] dispatch rc={rc}: {LastNativeMessage()}");
		}

		// ffx wrote to the command list behind Diligent's back: Flush must precede InvalidateState.
		_api.ImmediateContext.Flush();
		_api.ImmediateContext.InvalidateState();
	}

	public void Resize(IGpuTexture sceneHdr, IGpuTexture depth, IGpuTexture motion,
		uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight)
	{
		// The native context bakes in max sizes, so recreate it; the caller has idled the GPU.
		if (_context != IntPtr.Zero)
		{
			DecaFsr_Destroy(_context);
			_context = IntPtr.Zero;
		}

		OutputTarget.Resize(new Vector2(displayWidth, displayHeight));

		if (!CreateContext(sceneHdr, depth, motion, renderWidth, renderHeight,
			    displayWidth, displayHeight, out var error))
		{
			Console.WriteLine($"[fsr] context recreation after resize failed: {error}; {LastNativeMessage()}");
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
