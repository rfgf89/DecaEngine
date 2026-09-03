using System;
using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Core;

namespace DecaEngine.Graphics.Diligent;

/// <summary>NVIDIA DLSS upscaler backend, driven through NGX via the DecaFfxShim.dll shim.</summary>
// nvngx_dlss.dll must sit next to the exe; TryCreate returns null without it.
// Motion is UV fractions (prevUV = curUV + motion, y down) scaled by render size; jitter in pixels.
public sealed class DlssUpscalerBackend : INativeUpscalerBackend
{
	private const string ShimDll = "DecaFfxShim.dll";

	[DllImport(ShimDll, CallingConvention = CallingConvention.Cdecl)]
	private static extern int DecaDlss_Create(IntPtr anyResource, uint renderW, uint renderH,
		uint displayW, uint displayH, int quality, out IntPtr ctx);

	[DllImport(ShimDll, CallingConvention = CallingConvention.Cdecl)]
	private static extern int DecaDlss_Dispatch(IntPtr ctx, IntPtr diligentContext,
		IntPtr colorRes, IntPtr depthRes, IntPtr motionRes, IntPtr outputRes,
		float jitterX, float jitterY, float mvScaleX, float mvScaleY,
		uint renderW, uint renderH, float frameTimeDeltaMs, int reset);

	[DllImport(ShimDll, CallingConvention = CallingConvention.Cdecl)]
	private static extern int DecaDlss_CreateFeature(IntPtr ctx, IntPtr diligentContext);

	[DllImport(ShimDll, CallingConvention = CallingConvention.Cdecl)]
	private static extern void DecaDlss_Destroy(IntPtr ctx);

	private static readonly Vector2 JitterSign = ParseSign("DECA_DLSS_JSIGN");
	private static readonly Vector2 MvSign = ParseSign("DECA_DLSS_MVSIGN");

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

	private IntPtr _context;
	private IGpuTexture _sceneHdr = null!;
	private IGpuTexture _depth = null!;
	private IGpuTexture _motion = null!;
	private uint _renderWidth, _renderHeight, _displayWidth, _displayHeight;
	private Vector2 _jitterPixels;
	private float _deltaTimeMs = 1000f / 60f;
	private bool _resetNextFrame = true;

	// NVSDK_NGX_PerfQuality_Value: 0 MaxPerf, 1 Balanced, 2 MaxQuality, 5 DLAA.
	private int _quality = 1;

	/// <summary>Sets the DLSS quality preset; the caller must wait for the GPU first.</summary>
	public void SetQuality(int quality)
	{
		if (quality == _quality)
		{
			return;
		}

		_quality = quality;
		Resize(_sceneHdr, _depth, _motion, _renderWidth, _renderHeight, _displayWidth, _displayHeight);
	}

	/// <summary>Generation and file version of nvngx_dlss.dll, e.g. "DLSS 4 / 310.7.0.0".</summary>
	public string DebugName { get; } = ResolveDebugName();

	/// <summary>Current quality preset as an NVSDK_NGX_PerfQuality_Value.</summary>
	public int Quality => _quality;

	private static string ResolveDebugName()
	{
		try
		{
			var dll = System.IO.Path.Combine(AppContext.BaseDirectory, "nvngx_dlss.dll");
			// NVIDIA's version resource is comma-separated ("310,7,0,0").
			var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).FileVersion
				?.Replace(",", ".").Replace(" ", "");
			if (string.IsNullOrEmpty(version))
			{
				return "DLSS (NGX)";
			}

			var promo = int.TryParse(version.Split('.')[0], out var major) ? major switch
			{
				>= 300 => "DLSS 4",
				3 => "DLSS 3",
				2 => "DLSS 2",
				_ => "DLSS",
			} : "DLSS";

			return $"{promo} / {version}";
		}
		catch (Exception)
		{
			return "DLSS (NGX)";
		}
	}

	public IRenderTarget OutputTarget { get; }

	private DlssUpscalerBackend(DiligentGraphicsApi api, IRenderTarget output)
	{
		_api = api;
		OutputTarget = output;
	}

	public static DlssUpscalerBackend? TryCreate(IGraphicsApi graphicsApi, string colorTargetName,
		IGpuTexture sceneHdr, IGpuTexture depth, IGpuTexture motion,
		uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight)
	{
		if (graphicsApi is not DiligentGraphicsApi api)
		{
			return null;
		}

		var output = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " DLSS Output",
			width = displayWidth,
			height = displayHeight,
			format = TextureObjectFormat.R16G16B16A16Float,
			access = HandleAccess.Pixel | HandleAccess.Compute,
		});

		var backend = new DlssUpscalerBackend(api, output);

		try
		{
			if (!backend.CreateContext(sceneHdr, depth, motion,
				    renderWidth, renderHeight, displayWidth, displayHeight, out var error))
			{
				Console.WriteLine($"[dlss] context not created: {error}; {FsrUpscalerBackend.LastNativeMessage()}");
				output.Release();
				return null;
			}
		}
		catch (DllNotFoundException)
		{
			Console.WriteLine("[dlss] DecaFfxShim.dll not found next to the executable - DLSS unavailable");
			output.Release();
			return null;
		}

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

		var rc = DecaDlss_Create(FsrUpscalerBackend.NativeHandleOf(sceneHdr),
			renderWidth, renderHeight, displayWidth, displayHeight, _quality, out _context);
		if (rc != 0)
		{
			error = $"code {rc}";
			return false;
		}

		// Create the feature outside a frame: its init commands must reach the GPU before evaluate.
		var featureRc = DecaDlss_CreateFeature(_context,
			((global::Diligent.IDeviceContext)_api.ImmediateContext).NativePointer);
		_api.ImmediateContext.Flush();
		_api.ImmediateContext.WaitForIdle();
		_api.ImmediateContext.InvalidateState();

		if (featureRc != 0)
		{
			error = $"feature: code {featureRc}";
			DecaDlss_Destroy(_context);
			_context = IntPtr.Zero;
			return false;
		}

		error = "";
		return true;
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

		var rc = DecaDlss_Dispatch(_context,
			((global::Diligent.IDeviceContext)_api.ImmediateContext).NativePointer,
			FsrUpscalerBackend.NativeHandleOf(_sceneHdr), FsrUpscalerBackend.NativeHandleOf(_depth),
			FsrUpscalerBackend.NativeHandleOf(_motion), FsrUpscalerBackend.NativeHandleOf(OutputTarget),
			_jitterPixels.X * JitterSign.X, _jitterPixels.Y * JitterSign.Y,
			_renderWidth * MvSign.X, _renderHeight * MvSign.Y,
			_renderWidth, _renderHeight, _deltaTimeMs, _resetNextFrame ? 1 : 0);

		_resetNextFrame = false;

		if (rc != 0)
		{
			Console.WriteLine($"[dlss] dispatch rc={rc}: {FsrUpscalerBackend.LastNativeMessage()}");
		}

		// Flush must precede InvalidateState; same interop contract as FsrUpscalerBackend.Dispatch.
		_api.ImmediateContext.Flush();
		_api.ImmediateContext.InvalidateState();
	}

	public void Resize(IGpuTexture sceneHdr, IGpuTexture depth, IGpuTexture motion,
		uint renderWidth, uint renderHeight, uint displayWidth, uint displayHeight)
	{
		// Sizes are baked into the NGX feature, so the context is recreated; caller waited for GPU.
		if (_context != IntPtr.Zero)
		{
			DecaDlss_Destroy(_context);
			_context = IntPtr.Zero;
		}

		OutputTarget.Resize(new Vector2(displayWidth, displayHeight));

		if (!CreateContext(sceneHdr, depth, motion, renderWidth, renderHeight,
			    displayWidth, displayHeight, out var error))
		{
			Console.WriteLine($"[dlss] recreation after resize failed: {error}; {FsrUpscalerBackend.LastNativeMessage()}");
		}

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
			DecaDlss_Destroy(_context);
			_context = IntPtr.Zero;
		}

		OutputTarget.Release();
	}
}
