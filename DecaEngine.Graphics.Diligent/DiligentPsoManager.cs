using System;
using System.Collections.Generic;
using System.IO;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

/// <summary>
/// Global PSO cache manager; one instance per application.
///
/// The cache file must be per-backend: D3D12 pipeline libraries and Vulkan pipeline caches are
/// incompatible formats, and the editor (D3D12) and CLI probes (Vulkan) share a working dir.
///
/// DECA_PSO_CACHE=0 disables the cache; DECA_PSO_CACHE=clear deletes the file at startup.
/// </summary>
public class DiligentPsoManager : IDisposable
{
	private readonly IRenderDevice _device;
	private readonly string _cacheFilePath;
	private readonly bool _disabled;
	private IPipelineStateCache? _psoCache;

	public DiligentPsoManager(IRenderDevice device, string cacheFilePath)
	{
		_device = device ?? throw new ArgumentNullException(nameof(device));
		_cacheFilePath = cacheFilePath;

		var mode = Environment.GetEnvironmentVariable("DECA_PSO_CACHE");
		_disabled = mode == "0";
		if (_disabled)
		{
			return;
		}

		if (string.Equals(mode, "clear", StringComparison.OrdinalIgnoreCase))
		{
			TryDeleteCacheFile();
		}

		InitializeCache();
	}

	private unsafe void InitializeCache()
	{
		byte[] cacheData;
		try
		{
			cacheData = File.Exists(_cacheFilePath) ? File.ReadAllBytes(_cacheFilePath) : Array.Empty<byte>();
		}
		catch (IOException)
		{
			cacheData = Array.Empty<byte>();
		}

		if (cacheData.Length > 0)
		{
			fixed (byte* ptr = cacheData)
			{
				var createInfo = new PipelineStateCacheCreateInfo
				{
					Desc = new PipelineStateCacheDesc
					{
						Name = "Global PSO Cache",
						Mode = PsoCacheMode.LoadStore,
						Flags = PsoCacheFlags.None
					},
					CacheData = new IntPtr(ptr),
					CacheDataSize = (uint)cacheData.Length
				};

				// A corrupt or foreign cache file must not kill startup, and must not survive.
				try
				{
					_psoCache = _device.CreatePipelineStateCache(createInfo);
				}
				catch
				{
					_psoCache = null;
				}

				if (_psoCache is null)
				{
					TryDeleteCacheFile();
				}
			}
		}

		if (_psoCache is null)
		{
			// No file, or the load failed: create a store-only cache.
			var createInfo = new PipelineStateCacheCreateInfo
			{
				Desc = new PipelineStateCacheDesc
				{
					Name = "Global PSO Cache",
					Mode = PsoCacheMode.Store,
					Flags = PsoCacheFlags.None
				},
				CacheData = IntPtr.Zero,
				CacheDataSize = 0
			};
			_psoCache = _device.CreatePipelineStateCache(createInfo);
		}
	}

	// Process-wide diagnostics: how many graphics PSOs were created and what they cost.
	public static long DiagCreateMs;
	public static int DiagCreateCount;

	// Per-name breakdown; the PSO name fully describes the configuration.
	public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, long Ms)> DiagByName = new();

	// PSO names already served by _psoCache in this process, to catch repeat creations.
	private readonly HashSet<string> _seenPsoNames = new();
	private readonly object _seenPsoNamesLock = new();

	/// <summary>Creates a graphics PSO. Only the first creation of a given name uses the disk
	/// cache: a D3D12 pipeline library re-loaded under an already registered name hands back an
	/// invalid object with no error and no null, and binding it faults.</summary>
	public IPipelineState CreateGraphicsPipelineState(GraphicsPipelineStateCreateInfo createInfo)
	{
		var name = createInfo.PSODesc.Name;
		bool reused;
		lock (_seenPsoNamesLock)
		{
			reused = !_seenPsoNames.Add(name);
		}

		createInfo.PSOCache = reused ? null : _psoCache;

		var sw = System.Diagnostics.Stopwatch.StartNew();
		var pso = _device.CreateGraphicsPipelineState(createInfo);
		var ms = sw.ElapsedMilliseconds;
		DiagCreateMs += ms;
		DiagCreateCount++;
		DiagByName.AddOrUpdate(name, (1, ms), (_, prev) => (prev.Count + 1, prev.Ms + ms));
		return pso;
	}

	// Owned by the manager: materials must not dispose a shared PSO.
	private readonly Dictionary<string, IPipelineState> _sharedPsos = new();
	private readonly object _sharedPsosLock = new();

	public static int DiagSharedHits;

	// Identity numbers, not hashes: a collision in a PSO key would draw with a foreign pipeline.
	private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _objectIds = new();
	private int _nextObjectId;

	/// <summary>Stable per-process identity number for an object taking part in a PSO key.</summary>
	public int ObjectId(object obj)
	{
		lock (_objectIds)
		{
			if (_objectIds.TryGetValue(obj, out var existing))
			{
				return (int)existing;
			}

			int id = ++_nextObjectId;
			_objectIds.Add(obj, id);
			return id;
		}
	}

	/// <summary>
	/// Returns a graphics PSO per CONFIGURATION rather than per material.
	///
	/// The key must describe the configuration in full - state object, native shaders, immutable
	/// samplers, variable layout - because a partial key silently draws with a foreign pipeline.
	/// </summary>
	public IPipelineState? GetOrCreateSharedGraphicsPipelineState(string key,
		GraphicsPipelineStateCreateInfo createInfo)
	{
		lock (_sharedPsosLock)
		{
			if (_sharedPsos.TryGetValue(key, out var cached))
			{
				DiagSharedHits++;
				return cached;
			}

			var pso = CreateGraphicsPipelineState(createInfo);
			if (pso != null)
			{
				_sharedPsos[key] = pso;
			}

			return pso;
		}
	}

	/// <summary>Creates a compute PSO deliberately WITHOUT the disk cache: a D3D12 pipeline
	/// library returns an invalid compute PSO from a previous run with no error and no null,
	/// and binding it faults.</summary>
	public IPipelineState CreateComputePipelineState(ComputePipelineStateCreateInfo createInfo)
	{
		return _device.CreateComputePipelineState(createInfo);
	}

	/// <summary>Writes the accumulated cache to disk; call on shutdown or after a level load.</summary>
	public unsafe void SaveCache()
	{
		if (_psoCache == null)
		{
			return;
		}

		var dataBlob = _psoCache.GetData();
		if (dataBlob != null)
		{
			try
			{
				var dataSpan = new ReadOnlySpan<byte>(dataBlob.GetDataPtr().ToPointer(), (int)dataBlob.GetSize());
				File.WriteAllBytes(_cacheFilePath, dataSpan.ToArray());
			}
			catch (IOException)
			{
				// Not fatal: the next run just recompiles the PSOs.
			}
			finally
			{
				dataBlob.Dispose();
			}
		}
		else
		{
			// Serialization failed, so any file on disk is now stale: better no cache than a bad one.
			TryDeleteCacheFile();
		}
	}

	private void TryDeleteCacheFile()
	{
		try
		{
			if (File.Exists(_cacheFilePath))
			{
				File.Delete(_cacheFilePath);
			}
		}
		catch (IOException)
		{
		}
	}

	public void Dispose()
	{
		SaveCache();

		lock (_sharedPsosLock)
		{
			foreach (var pso in _sharedPsos.Values)
			{
				pso.Dispose();
			}

			_sharedPsos.Clear();
		}

		_psoCache?.Dispose();
	}
}
