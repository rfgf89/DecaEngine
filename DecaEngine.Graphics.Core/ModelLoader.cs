using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using SharpGLTF.Schema2;
using DecaEngine.Core;
using DecaEngine.Graphics.Assets;
using System.Runtime.InteropServices;
using Diligent;
using MeshOptimizer;
using StbImageSharp;
using UnsafeCollections.Collections.Unsafe;
using DecaEngine.Animation;

namespace DecaEngine.Graphics;

public partial class ModelLoader
{
	public List<InstanceData> instances = new();

	/// <summary>Per-phase load timing and decoded-texture volume, for load diagnostics.</summary>
	public readonly record struct LoadTimings(long ParseMs, long DecodeMs, long MaterialsMs, long MeshesMs,
		long FinalizeMs, int DecodedImages, long DecodedBytes, int ShaderVariants, long ShaderMs,
		int TextureUploads, long TextureMs, int MeshUploads, long MeshMs, int Samplers, long SamplerMs, int MaterialsBuilt, long MaterialBuildMs, long MatCreateMs, long MatShaderMs);

	public LoadTimings Timings { get; internal set; }

	internal long _shaderMs;
	internal int _shaderVariants;
	internal long _textureMs;
	internal int _textureCount;
	internal long _meshMs;
	internal int _meshCount;
	internal long _samplerMs;
	internal int _samplerCount;
	internal long _materialMs;
	internal int _materialCount;
	internal long _matCreateMs;
	internal long _matShaderMs;

	// Shaders are shared by this model's materials (OwnsShaders=false); released once here.
	internal readonly List<IShaderObject> _ownedShaders = new();

	/// <summary>Releases GPU resources; caller must detach all instances and wait for the GPU first.</summary>
	public void Release()
	{
		foreach (var mesh in Meshes)
		{
			mesh?.Release();
		}

		Meshes.Clear();
		MeshHasUv.Clear();

		// Distinct instances only: the default material sits under several keys; a second
		// native Release would touch freed memory.
		var releasedMaterials = new HashSet<IMaterialObject>(ReferenceEqualityComparer.Instance
			as IEqualityComparer<IMaterialObject>);
		foreach (var material in materialObjects.Values)
		{
			if (material != null && releasedMaterials.Add(material))
			{
				material.Release();
			}
		}

		materialObjects.Clear();

		// Textures after materials: their SRBs held the texture views; ownership is here.
		foreach (var texture in _ownedTextures)
		{
			texture?.Release();
		}

		_ownedTextures.Clear();

		foreach (var streamed in StreamedTextures)
		{
			streamed.Texture?.Release();
			streamed.Texture = null;
			streamed.EncodedPixels = null;
			streamed.Bindings.Clear();
		}

		StreamedTextures.Clear();

		// Shaders after materials: live materials still hold the native shaders.
		foreach (var shader in _ownedShaders)
		{
			shader?.Release();
		}

		_ownedShaders.Clear();
		instances.Clear();
	}

	public List<IMeshObject> Meshes = new();

	/// <summary>Parallel to <see cref="Meshes"/>: had a real TEXCOORD_0, not synthesized zero UVs.</summary>
	public List<bool> MeshHasUv = new();

	/// <summary>Model skeleton, null for static models; kept CPU-side for procedural pose edits.</summary>
	public PreparedSkeleton Skeleton;

	/// <summary>Animation clips mapped onto <see cref="Skeleton"/> joints.</summary>
	public List<PreparedAnimation> Animations = new();

	/// <summary>Parallel to <see cref="Meshes"/>: per-mesh skin stream, null for static meshes.</summary>
	public List<SkinVertex[]> MeshSkin = new();

	/// <summary>Linear per-triangle albedo by meshId; empty for meshes without texture/UV/CPU pixels.</summary>
	public Dictionary<int, Vector3[]> TriangleAlbedo { get; } = new();

	/// <summary>Per-triangle metalness by meshId: MR texture B channel times MetallicFactor.</summary>
	public Dictionary<int, float[]> TriangleMetalness { get; } = new();

	/// <summary>Per-triangle roughness by meshId: MR texture G channel times RoughnessFactor.</summary>
	public Dictionary<int, float[]> TriangleRoughness { get; } = new();

	/// <summary>Side of a <see cref="MaterialAlbedoTile"/> tile.</summary>
	public const int AlbedoTileSize = 128;

	/// <summary>Per-material base color tile: sRGB RGBA8 downsampled in LINEAR space, no BaseColorFactor.</summary>
	public Dictionary<int, byte[]> MaterialAlbedoTile { get; } = new();

	public OrderedDictionary<int, IMaterialObject> materialObjects = new();

	/// <summary>One streamable model texture and every material slot bound to it.</summary>
	public sealed class StreamedTexture
	{
		/// <summary>External image path for background re-decode; null for embedded images.</summary>
		public string FilePath;

		/// <summary>Encoded source of an embedded image (.glb / data URI).</summary>
		public byte[] EncodedPixels;

		/// <summary>Whether another quality step can still be produced.</summary>
		public bool HasSource => !Completed && (FilePath != null || EncodedPixels != null || DtexPath != null);

		/// <summary>Reads the encoded source; disk I/O — background thread only.</summary>
		public byte[] ReadEncoded() => EncodedPixels ?? (FilePath != null ? File.ReadAllBytes(FilePath) : null);

		/// <summary>Releases CPU-side source data once streaming is complete.</summary>
		public void ReleaseCpuData()
		{
			Completed = true;
			EncodedPixels = null;
			FilePath = null;
			DtexPath = null;
		}

		/// <summary>Larger side of the current GPU decode; 0 = still the 1x1 filler.</summary>
		public int CurrentSize;

		/// <summary>Target side; 0 = native file resolution.</summary>
		public int TargetSize;

		public bool Completed;

		/// <summary>Authored glTF sampler settings; bound once as an immutable sampler.</summary>
		public TextureAddress AddressMode;
		public TextureFilter FilterMode;

		/// <summary>Current GPU texture shared by all bindings; null = slot still on the 1x1 filler.</summary>
		public IGpuTexture Texture;

		public readonly List<(IMaterialObject Material, string Slot)> Bindings = new();

		/// <summary>Baked .dtex source from the asset cache; the mip tail is read from disk as-is.</summary>
		public string DtexPath;

		/// <summary>Level-0 size of <see cref="DtexPath"/>, to pick a mip without opening the file.</summary>
		public int DtexWidth;
		public int DtexHeight;

		/// <summary>Levels are block-compressed; outlives <see cref="DtexPath"/> for memory accounting.</summary>
		public bool IsBlockCompressed;
	}

	/// <summary>One quality step of a streamed texture, prepared in the background and handed to the graphics API.</summary>
	public sealed class StreamedTextureLevel
	{
		/// <summary>One RGBA8 element (GPU builds mips) or the whole BC mip chain (GPU cannot).</summary>
		public required byte[][] Mips { get; init; }

		/// <summary>Block-compressed format; <see cref="TextureObjectFormat.Unknown"/> = RGBA8.</summary>
		public required TextureObjectFormat Format { get; init; }

		public required int Width { get; init; }
		public required int Height { get; init; }

		/// <summary>Larger side — the streamer's quality metric.</summary>
		public int Size => Math.Max(Width, Height);

		public long ByteLength
		{
			get
			{
				long total = 0;
				foreach (var mip in Mips)
				{
					total += mip?.LongLength ?? 0;
				}

				return total;
			}
		}

		public static StreamedTextureLevel FromDecodedPixels(byte[] pixels, int width, int height) => new()
		{
			Mips = [pixels],
			Format = TextureObjectFormat.Unknown,
			Width = width,
			Height = height,
		};

		public static StreamedTextureLevel FromCompressed(TextureObjectFormat format, byte[][] mips,
			int width, int height) => new()
		{
			Mips = mips,
			Format = format,
			Width = width,
			Height = height,
		};

		public CpuTextureData ToCpuTextureData(string name) => Format == TextureObjectFormat.Unknown
			? new CpuTextureData
			{
				Name = name,
				DecodedPixels = Mips[0],
				DecodedWidth = Width,
				DecodedHeight = Height,
			}
			: new CpuTextureData
			{
				Name = name,
				CompressedMips = Mips,
				CompressedFormat = Format,
				CompressedWidth = Width,
				CompressedHeight = Height,
				GenerateMips = false,
			};
	}

	/// <summary>_MainTex binding shared with the alpha-tested shadow material.</summary>
	public sealed class BaseColorBinding
	{
		public IGpuTexture Texture;
		public ISamplerObject Sampler;

		/// <summary>Streaming record; null when the texture loaded whole.</summary>
		public StreamedTexture Stream;
	}

	/// <summary>_MainTex bindings keyed like <see cref="materialObjects"/>; untextured ones are absent.</summary>
	public readonly Dictionary<int, BaseColorBinding> MaterialBaseColor = new();

	/// <summary>All real (non-filler) texture bindings per material slot, for reuse.</summary>
	public readonly Dictionary<int, Dictionary<string, BaseColorBinding>> MaterialTextureBindings = new();

	// Shared 1x1 fillers, reused by BuildAdditionalMaterialSet; released via _ownedTextures.
	internal IGpuTexture FallbackWhiteTexture;
	internal ISamplerObject FallbackSampler;
	internal IGpuTexture FallbackFlatNormalTexture;

	/// <summary>Streamed textures; empty unless <see cref="ModelLoadOptions.StreamTextures"/>.</summary>
	public readonly List<StreamedTexture> StreamedTextures = new();

	// Materials do not release textures (see DiligentMaterial.Release); ownership is here.
	internal readonly List<IGpuTexture> _ownedTextures = new();

	// Mesh topology codes: point/line glTF primitives draw via a material clone with a matching PSO.
	public const int MeshTopologyTriangles = 0;
	public const int MeshTopologyLineList = 1;
	public const int MeshTopologyLineStrip = 2;
	public const int MeshTopologyPoints = 3;

	/// <summary>Material key for a topology clone; never collides with glTF indices (-1..N).</summary>
	public static int MakeTopologyMaterialKey(int topology, int materialIndex) => 10000 * topology + materialIndex + 1;

	/// <summary>PBR factors per material, keyed like <see cref="materialObjects"/>; -1 is the default.</summary>
	public Dictionary<int, MaterialPbrFactors> MaterialPbr = new();

	/// <summary>Approximate world AABB from mesh bounding spheres; (Zero, Zero) when empty.</summary>
	public (Vector3 min, Vector3 max) ComputeBounds()
	{
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		var any = false;

		foreach (var instance in instances)
		{
			if (instance.meshId < 0 || instance.meshId >= Meshes.Count)
			{
				continue;
			}

			var mesh = Meshes[instance.meshId];
			var t = instance.transform;

			var matrix = Matrix4x4.CreateScale(t.scale.X, t.scale.Y, t.scale.Z) *
						 Matrix4x4.CreateFromQuaternion(t.rotation) *
						 Matrix4x4.CreateTranslation(t.position);

			var worldCenter = Vector3.Transform(mesh.Center, matrix);

			// Conservative: scale the sphere radius by the largest axis scale.
			var worldRadius = mesh.Radius * MathF.Max(MathF.Abs(t.scale.X),
				MathF.Max(MathF.Abs(t.scale.Y), MathF.Abs(t.scale.Z)));

			if (float.IsNaN(worldCenter.X) || float.IsNaN(worldCenter.Y) || float.IsNaN(worldCenter.Z) ||
			    float.IsNaN(worldRadius) || float.IsInfinity(worldCenter.X) || float.IsInfinity(worldCenter.Y) ||
			    float.IsInfinity(worldCenter.Z) || float.IsInfinity(worldRadius) || worldRadius <= 0)
			{
				continue;
			}

			var extent = new Vector3(worldRadius);

			min = Vector3.Min(min, worldCenter - extent);
			max = Vector3.Max(max, worldCenter + extent);
			any = true;
		}

		return any ? (min, max) : (Vector3.Zero, Vector3.Zero);
	}

	internal static TextureAddress ToAddressMode(TextureWrapMode wrapMode)
	{
		return wrapMode switch
		{
			TextureWrapMode.CLAMP_TO_EDGE => TextureAddress.Clamp,
			TextureWrapMode.MIRRORED_REPEAT => TextureAddress.Mirror,
			TextureWrapMode.REPEAT => TextureAddress.Wrap,
			_ => TextureAddress.Wrap
		};
	}

	internal static TextureFilter ToFilter(TextureMipMapFilter minFilter, TextureInterpolationFilter magFilter)
	{
		if (magFilter == TextureInterpolationFilter.LINEAR)
		{
			return minFilter switch
			{
				TextureMipMapFilter.LINEAR => TextureFilter.Linear,
				TextureMipMapFilter.NEAREST => TextureFilter.Point,
				TextureMipMapFilter.LINEAR_MIPMAP_LINEAR => TextureFilter.Linear,
				TextureMipMapFilter.LINEAR_MIPMAP_NEAREST => TextureFilter.Linear,
				TextureMipMapFilter.NEAREST_MIPMAP_LINEAR => TextureFilter.Point,
				TextureMipMapFilter.NEAREST_MIPMAP_NEAREST => TextureFilter.Point,
				_ => TextureFilter.Linear
			};
		}
		else // magFilter is NEAREST
		{
			return minFilter switch
			{
				TextureMipMapFilter.LINEAR => TextureFilter.Linear,
				TextureMipMapFilter.NEAREST => TextureFilter.Point,
				TextureMipMapFilter.LINEAR_MIPMAP_LINEAR => TextureFilter.Linear,
				TextureMipMapFilter.LINEAR_MIPMAP_NEAREST => TextureFilter.Linear,
				TextureMipMapFilter.NEAREST_MIPMAP_LINEAR => TextureFilter.Point,
				TextureMipMapFilter.NEAREST_MIPMAP_NEAREST => TextureFilter.Point,
				_ => TextureFilter.Point
			};
		}
	}

	/// <summary>Lightweight placeholder model bundled with the editor, used when no model is selected.</summary>
	public const string DefaultModelPath = "EditorAssets/models/result.gltf";

	internal ModelLoader()
	{
	}

	/// <summary>Starts the CPU-side load in the background; finalize on the graphics API's own thread.</summary>
	public static ModelLoadRequest BeginLoadAsync(IGraphicsApi graphicsApi, string modelPath, ModelLoadOptions options,
		IProgress<float> progress = null, CancellationToken cancellationToken = default)
	{
		if (!Path.IsPathRooted(modelPath))
		{
			modelPath = Path.Combine(Environment.CurrentDirectory, modelPath);
		}

		if (!File.Exists(modelPath))
		{
			throw new FileNotFoundException(
				$"Model scene not found: '{modelPath}'.",
				modelPath);
		}

		return new ModelLoadRequest(graphicsApi, modelPath, options, progress, cancellationToken);
	}

	/// <summary>Synchronously loads and finalizes a model; blocks the calling thread for the whole load.</summary>
	public static ModelLoader Load(IGraphicsApi graphicsApi, string modelPath, ModelLoadOptions options)
	{
		var request = BeginLoadAsync(graphicsApi, modelPath, options);
		request.PrepareTask.GetAwaiter().GetResult();
		return request.FinalizeOnMainThread();
	}

	/// <summary>In-flight load: await <see cref="PrepareTask"/>, then finalize on the graphics thread.</summary>
	public sealed class ModelLoadRequest
	{
		private readonly IGraphicsApi _graphicsApi;
		private readonly ModelLoadOptions _options;
		private readonly ProgressTracker _progressTracker = new();

		public string ModelPath { get; }
		public Task PrepareTask { get; }
		public float Progress => _progressTracker.Value;

		private PreparedModel _prepared;

		// Incremental finalization state (see FinalizeChunk); persists across frames.
		private ModelLoader _finalizing;
		private long _finalizeMs;
		private IEnumerator<long> _finalizeSteps;

		internal ModelLoadRequest(IGraphicsApi graphicsApi, string modelPath, ModelLoadOptions options,
			IProgress<float> externalProgress, CancellationToken cancellationToken)
		{
			_graphicsApi = graphicsApi;
			_options = options;
			ModelPath = modelPath;

			var combinedProgress = new Progress<float>(p =>
			{
				_progressTracker.Value = p;
				externalProgress?.Report(p);
			});

			PrepareTask = Task.Run(() =>
			{
				_prepared = ModelImporter.PrepareModel(modelPath, options, combinedProgress, cancellationToken);
				// Warm shader variants here in the background, not lazily on the GPU thread.
				PrecompileShaderVariants(graphicsApi, options, _prepared, cancellationToken);
			}, cancellationToken);
		}

		/// <summary>Upload-heap pages recycle only at Present, so this bounds host-visible memory.</summary>
		public const long DefaultFinalizeBudgetBytes = 96L << 20;

		/// <summary>Finalizes the whole model in one blocking call on the graphics thread.</summary>
		public ModelLoader FinalizeOnMainThread() => FinalizeChunk(long.MaxValue, long.MaxValue);

		/// <summary>Incremental finalize on the graphics thread, returning null until done; a Present
		/// must pass between calls, and abandoning mid-way leaks the GPU resources made so far.</summary>
		public ModelLoader FinalizeChunk(long budgetBytes = DefaultFinalizeBudgetBytes,
			long budgetMs = FinalizeBudgetMs)
		{
			if (!PrepareTask.IsCompletedSuccessfully)
			{
				throw new InvalidOperationException(
					"FinalizeChunk called before the background load finished successfully.");
			}

			if (_prepared == null)
			{
				throw new InvalidOperationException("FinalizeChunk called after finalization already completed.");
			}

			if (_finalizeSteps == null)
			{
				_finalizing = new ModelLoader();
				_finalizeSteps = BuildFromPreparedIncremental(_graphicsApi, _options, _prepared, _finalizing);
			}

			var swFinalize = System.Diagnostics.Stopwatch.StartNew();

			long uploadedBytes = 0;
			while (uploadedBytes < budgetBytes)
			{
				if (!_finalizeSteps.MoveNext())
				{
					var ready = _finalizing;
					_finalizeMs += swFinalize.ElapsedMilliseconds;
					ready.Timings = new LoadTimings(_prepared.MsParse, _prepared.MsDecode,
						_prepared.MsMaterials, _prepared.MsMeshes, _finalizeMs,
						_prepared.DecodedImages, _prepared.DecodedBytes,
						ready._shaderVariants, ready._shaderMs, ready._textureCount, ready._textureMs,
						ready._meshCount, ready._meshMs, ready._samplerCount, ready._samplerMs, ready._materialCount, ready._materialMs, ready._matCreateMs, ready._matShaderMs);

					_finalizeSteps.Dispose();
					_finalizeSteps = null;
					_finalizing = null;
					_prepared = null;

					// Persist the PSO cache now that every pipeline of the model exists.
					_graphicsApi.SavePipelineCache();
					return ready;
				}

				uploadedBytes += _finalizeSteps.Current;

				// Time budget too: shader compilation uploads no bytes yet can cost ~1s per variant.
				if (swFinalize.ElapsedMilliseconds >= budgetMs)
				{
					break;
				}
			}

			_finalizeMs += swFinalize.ElapsedMilliseconds;

			return null;
		}

		/// <summary>Per-call render-thread time budget; one iterator step is uninterruptible, so a frame may overrun.</summary>
		public const long FinalizeBudgetMs = 8;

		private sealed class ProgressTracker
		{
			private float _value;
			public float Value
			{
				get => Volatile.Read(ref _value);
				set => Volatile.Write(ref _value, value);
			}
		}
	}
}
