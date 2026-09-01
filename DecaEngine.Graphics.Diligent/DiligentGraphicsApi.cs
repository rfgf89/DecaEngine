using System;
using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Core;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.Diligent.RenderGraph;
using Diligent;
using SharpGLTF.Schema2;
using StbImageSharp;
// Прямая работа с нативным контекстом - состояния здесь Diligent-овские, не из ICommandBuffer.
using ClearDepthStencilFlags = Diligent.ClearDepthStencilFlags;
using ResourceState = Diligent.ResourceState;
using TextureInfo = DecaEngine.Graphics.TextureInfo;
using Version = Diligent.Version;

namespace DecaEngine.Graphics.Diligent
{
	public class DiligentGraphicsApi : IGraphicsApi
	{
		private IEngineFactory? _engineFactory;
		private IRenderDevice? _renderDevice;
		private IDeviceContext? _immediateContext;
		private IDeviceContext[] _deferredDevices = [];
		private ISwapChain? _swapChain;
		private IFence? _frameFence;
		private ulong _nextFrameValue = 1;

		public IEngineFactory EngineFactory => _engineFactory ?? throw new NullReferenceException();
		public IRenderDevice Device => _renderDevice ?? throw new NullReferenceException();
		public IDeviceContext ImmediateContext => _immediateContext ?? throw new NullReferenceException();
		public IDeviceContext[] DeferredContexts => _deferredDevices ?? [];
		public ISwapChain SwapChain => _swapChain ?? throw new NullReferenceException();

		public event Action<GraphicsPipelineSetupInfo> OnCreateSetupInfo;
		public event Action OnSwapChainInfo;

		/// <summary>
		/// Срабатывает для каждого диагностического сообщения от нативного движка Diligent
		/// (переданного через IEngineFactory.SetMessageCallback). Позволяет внешнему коду
		/// (например, редактору) перехватывать эти сообщения со всей структурированной
		/// информацией вместо парсинга текста из консоли. Если на событие никто не подписан,
		/// сообщение по умолчанию печатается в Console.
		/// </summary>
		public static event Action<DebugMessageSeverity, string, string, string, int>? DebugMessage;

		public IWindowHandle WindowHandle { get; set; }
		public DiligentPsoManager PsoManager { get; private set; }

		/// <summary>Дисковый кеш байткода шейдеров (HLSL -> DXIL/DXBC/SPIR-V), null - выключен
		/// (DECA_SHADER_CACHE=0 или не удалось создать директорию). Дополняет PsoManager: тот
		/// кеширует только драйверную часть PSO, а секунды на большие варианты UnlitInstancedPS
		/// съедает именно компилятор - см. DiligentShaderBytecodeCache.</summary>
		public DiligentShaderBytecodeCache? ShaderBytecodeCache { get; private set; }

		/// <summary>См. <see cref="IGraphicsApi.PresentInterval"/>. Дефолт 1 (vsync) - осознанный, см.
		/// комментарий в <see cref="Present"/>; переменная окружения DECA_VSYNC перекрывает его при
		/// старте (0 - без ожидания, 1..4 - интервалы синхронизации).</summary>
		public int PresentInterval { get; set; } = ResolvePresentInterval();

		/// <summary>Сколько кадров CPU вправе убежать вперёд GPU (см. фенс в <see cref="Present"/>).
		/// Меньше - ниже задержка, больше - ровнее загрузка; 2 - прежнее поведение.</summary>
		public int FramesInFlight { get; set; } = 2;

		private static int ResolvePresentInterval()
		{
			var env = Environment.GetEnvironmentVariable("DECA_VSYNC");
			return env != null && int.TryParse(env, out var interval)
				? Math.Clamp(interval, 0, 4)
				: 1;
		}

		/// <summary>Слои валидации GPU: по умолчанию только в Debug-сборке, DECA_GPU_VALIDATION
		/// ("1"/"0") перекрывает дефолт в обе стороны. В Release валидация стоит заметного CPU-времени
		/// на каждый вызов API и не нужна.</summary>
		private static bool ResolveValidationEnabled()
		{
			var env = Environment.GetEnvironmentVariable("DECA_GPU_VALIDATION");
			if (env == "1")
			{
				return true;
			}

			if (env == "0")
			{
				return false;
			}

#if DEBUG
			return true;
#else
			return false;
#endif
		}

		/// <summary>См. <see cref="IGraphicsApi.RayTracing"/>. Фича запрашивается опционально (см.
		/// Initialize), поэтому мало проверить флаги адаптера - надо убедиться, что устройство её
		/// действительно включило.</summary>
		public RayTracingSupport RayTracing
		{
			get
			{
				if (_renderDevice == null ||
					Device.GetDeviceInfo().Features.RayTracing != DeviceFeatureState.Enabled)
				{
					return RayTracingSupport.None;
				}

				var capFlags = Device.GetAdapterInfo().RayTracing.CapFlags;
				return capFlags.HasFlag(RayTracingCapFlags.InlineRayTracing)
					? RayTracingSupport.Inline
					: RayTracingSupport.Pipeline;
			}
		}

		/// <summary>Включил ли девайс динамическую индексацию массивов шейдерных ресурсов
		/// (ShaderResourceRuntimeArrays, запрошена Optional в Initialize) - гейт bindless-режима
		/// текстур RT-хитов SSR. На D3D12 есть всегда; на Vulkan зависит от драйвера.</summary>
		public bool SupportsShaderResourceArrays =>
			_renderDevice != null &&
			Device.GetDeviceInfo().Features.ShaderResourceRuntimeArrays == DeviceFeatureState.Enabled;

		public DiligentGraphicsApi(IWindowHandle windowHandle)
		{
			WindowHandle = windowHandle;
		}

		private static void OnMessageCallback(DebugMessageSeverity severity, string message, string function, string file, int line)
		{
			if (DebugMessage is not null)
			{
				DebugMessage.Invoke(severity, message, function, file, line);
			}
			else
			{
				Console.WriteLine($"[{severity}] {message} ({function}): {file}, {line}");
			}
		}

		public void SetBackBufferTarget(Vector4 color)
		{
			var rtv = SwapChain.GetCurrentBackBufferRTV();
			var dsv = SwapChain.GetDepthBufferDSV();

			ImmediateContext.SetRenderTargets(new[] { rtv }, dsv, ResourceStateTransitionMode.Transition);
			ImmediateContext.ClearRenderTarget(rtv, color, ResourceStateTransitionMode.Transition);
			if (dsv != null)
				ImmediateContext.ClearDepthStencil(dsv, global::Diligent.ClearDepthStencilFlags.Depth, 1.0f, 0, ResourceStateTransitionMode.Transition);
		}


		public IRenderGraph CreateRenderGraph()
		{
			return new DiligentRenderGraph(this);
		}

		public IMeshObject CreateMesh(string name)
		{
			return new DiligentMesh(name, Device);
		}

		public IMaterialObject CreateMaterial(string name)
		{
			return new DiligentMaterial(name, this);
		}

		public IComputeMaterial CreateComputeMaterial(string name)
		{
			return new DiligentComputeMaterial(name, this);
		}

		public IStateObject CreateGraphicsState(GraphicsStateInfo info)
		{
			return new DiligentGraphicsStateObject(info.Name ?? "Graphics State", info);
		}

		public IStateObject CreateComputeState(ComputeStateInfo info)
		{
			return new DiligentComputeStateObject(info.Name ?? "Compute State", info);
		}

		public TextureObjectFormat SwapChainColorFormat => DiligentResourceFormats.ToTextureObjectFormat(SwapChain.GetDesc().ColorBufferFormat);
		public TextureObjectFormat SwapChainDepthFormat => DiligentResourceFormats.ToTextureObjectFormat(SwapChain.GetDesc().DepthBufferFormat);

		/// <summary>Кэш скомпилированных шейдеров на весь срок жизни api: ключ - файл + точка входа +
		/// тип + набор кейвордов варианта. Раньше кэш вариантов жил ВНУТРИ одной загрузки модели
		/// (см. ModelLoader.BuildFromPreparedIncremental), то есть каждая следующая модель - и каждое
		/// переключение в Asset Browser - компилировала ровно те же варианты UnlitInstancedPS заново,
		/// синхронно на потоке рендера. Шейдеров десятки, память копеечная, а компиляция большого
		/// PS-варианта стоит секунды.</summary>
		private readonly Dictionary<string, DiligentShader> _shaderCache = new();

		/// <summary>Кэш дёргают и фоновые потоки загрузки (см. ModelLoader.PrecompileShaderVariants),
		/// и главный - словарь без замка рвался бы гонкой на ресайзе.</summary>
		private readonly object _shaderCacheLock = new();

		/// <summary>
		/// Шейдер из общего кэша - ТОЛЬКО для потребителей, чьи материалы помечены OwnsShaders=false
		/// (сейчас это загрузка моделей, см. ModelLoader.BuildFromPreparedIncremental). Обычный
		/// <see cref="CreateShader(string,string,string,ShaderObjectType)"/> кэш не трогает намеренно:
		/// материалы пассов движка владеют своими шейдерами и при Release освобождают НАТИВНЫЙ объект
		/// напрямую - выдай им шарёный экземпляр, и первое же пересоздание окружения убило бы шейдер
		/// под живыми материалами модели.
		/// </summary>
		public IShaderObject CreateSharedShader(string name, string factoryPath, string filePath,
			ShaderObjectType type, string entryPoint = "Main", IReadOnlyList<string> keywords = null)
		{
			return GetOrCreateShader(name, factoryPath, filePath, type, entryPoint, keywords);
		}

		private IShaderObject GetOrCreateShader(string name, string factoryPath, string filePath,
			ShaderObjectType type, string entryPoint, IReadOnlyList<string> keywords)
		{
			var key = string.Concat(factoryPath, "|", filePath, "|", entryPoint, "|", ((int)type).ToString(), "|",
				keywords == null ? "" : string.Join(";", keywords));

			lock (_shaderCacheLock)
			{
				if (_shaderCache.TryGetValue(key, out var cached))
				{
					return cached;
				}

				// Шареный между моделями объект: его Release игнорируется (см. DiligentShader.IsShared) -
				// иначе освобождение одной модели убило бы нативный шейдер, которым пользуются живые
				// материалы другой.
				var shader = new DiligentShader(this, name, factoryPath, filePath, type, entryPoint, keywords)
				{
					IsShared = true,
				};

				_shaderCache[key] = shader;
				return shader;
			}
		}

		public IShaderObject CreateShader(string name, string factoryPath, string filePath, ShaderObjectType type)
		{
			return new DiligentShader(this, name, factoryPath, filePath, type);
		}

		public IShaderObject CreateShader(string name, string factoryPath, string filePath, ShaderObjectType type, string entryPoint)
		{
			return new DiligentShader(this, name, factoryPath, filePath, type, entryPoint);
		}

		public IShaderObject CreateShader(string name, string factoryPath, string filePath, ShaderObjectType type, string entryPoint, IReadOnlyList<string> keywords)
		{
			return new DiligentShader(this, name, factoryPath, filePath, type, entryPoint, keywords);
		}

		/// <summary>Освобождает кэш шейдеров (их Release игнорирует IsShared - только отсюда).</summary>
		private void ReleaseShaderCache()
		{
			lock (_shaderCacheLock)
			{
				foreach (var shader in _shaderCache.Values)
				{
					shader.ReleaseShared();
				}

				_shaderCache.Clear();
			}
		}

		private static TextureAddressMode ToDiligent(TextureAddress mode)
		{
			return mode switch
			{
				TextureAddress.Wrap => TextureAddressMode.Wrap,
				TextureAddress.Mirror => TextureAddressMode.Mirror,
				TextureAddress.Clamp => TextureAddressMode.Clamp,
				TextureAddress.Border => TextureAddressMode.Border,
				TextureAddress.MirrorOnce => TextureAddressMode.MirrorOnce,
				_ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
			};
		}

		private static FilterType ToDiligent(TextureFilter filter)
		{
			return filter switch
			{
				TextureFilter.Anisotropic => FilterType.Anisotropic,
				TextureFilter.ComparisonLinear => FilterType.ComparisonLinear, // Added this line
				_ => FilterType.Linear
			};
		}

		public ISamplerObject CreateSampler(string name,
			TextureFilter filter,
			TextureAddress address,
			CompFunction comparisonFunction,
			Vector4 border,
			float mipLodBias = 0f)
		{
			var desc = new SamplerDesc
			{
				BorderColor = border,
				ComparisonFunc = (ComparisonFunction)comparisonFunction,
				MipLODBias = mipLodBias,
				// НЕ дефолт C#-структуры: new SamplerDesc{} зануляет MaxLOD, а MaxLOD == 0 зажимает
				// выборку в mip 0 - мипы не работают ВООБЩЕ (и любой mip bias сложится и заклампится
				// в ноль). Нативный Diligent дефолтит MaxLOD в +FLT_MAX - возвращаем его семантику.
				MinLOD = 0f,
				MaxLOD = float.MaxValue,
				MinFilter = ToDiligent(filter),
				MagFilter = ToDiligent(filter),
				MipFilter = ToDiligent(filter),
				AddressU = ToDiligent(address),
				AddressV = ToDiligent(address),
				AddressW = ToDiligent(address),
				// Без MaxAnisotropy анизотропный фильтр вырождается в обычный линейный - дефолт
				// дескриптора 0.
				MaxAnisotropy = filter == TextureFilter.Anisotropic ? 8u : 0u,
				Name = name,
			};

			var sampler = Device.CreateSampler(desc);
			return new DiligentSamplerObject(sampler, desc);
		}

		public unsafe IGpuTexture CreateTexture(CpuTextureData data)
		{
			// Запечённая BC-текстура из кеша ассетов: мип-цепочка уже готова, ужимать и генерировать
			// нечего - данные уезжают в VRAM ровно теми байтами, какими лежали на диске.
			if (data.IsCompressed)
			{
				return CreateCompressedTexture(data);
			}

			byte[] pixels;
			int width, height;

			if (data.DecodedPixels != null)
			{
				// Decoding already happened off the GPU thread (see ModelLoader's background load
				// pipeline) - avoid paying the decode cost again here.
				pixels = data.DecodedPixels;
				width = data.DecodedWidth;
				height = data.DecodedHeight;
			}
			else
			{
				ImageResult imageResult =
					ImageResult.FromMemory(data.Image.Content.Content.ToArray(), ColorComponents.RedGreenBlueAlpha);
				pixels = imageResult.Data;
				width = imageResult.Width;
				height = imageResult.Height;
			}

			// Полная мип-цепочка (GenerateMips на GPU): без неё минификация шумит, а анизотропный
			// сэмплер не работает. Требует Default-usage + RT-bind; 1x1 и запрошенные без мипов
			// текстуры идут старым immutable-путём.
			bool generateMips = data.GenerateMips && width > 1 && height > 1;

			var desc = new TextureDesc
			{
				Name = data.Name,
				Type = ResourceDimension.Tex2d,
				Width = (uint)width,
				Height = (uint)height,
				Format = TextureFormat.RGBA8_UNorm,
				BindFlags = generateMips ? BindFlags.ShaderResource | BindFlags.RenderTarget : BindFlags.ShaderResource,
				Usage = generateMips ? Usage.Default : Usage.Immutable,
				MipLevels = generateMips ? 0u : 1u,
				MiscFlags = generateMips ? MiscTextureFlags.GenerateMips : MiscTextureFlags.None,
			};

			ITexture nativeTexture;
			fixed (byte* pData = pixels)
			{
				var subResource = new TextureSubResData
				{
					Data = (IntPtr)pData, Stride = (uint)(width * 4)
				};

				if (generateMips)
				{
					nativeTexture = Device.CreateTexture(desc);
					// При нехватке VRAM Diligent возвращает null (лог уже содержит его ошибку), а
					// UpdateTexture по null-хендлу - это AV, убивающий процесс без managed-исключения.
					if (nativeTexture == null)
					{
						throw new InvalidOperationException(
							$"Failed to create texture '{data.Name}' ({width}x{height}, mips) - likely out of GPU memory.");
					}
					var box = new Box { MaxX = (uint)width, MaxY = (uint)height, MaxZ = 1 };
					ImmediateContext.UpdateTexture(nativeTexture, 0, 0, box, subResource,
						ResourceStateTransitionMode.Transition, ResourceStateTransitionMode.Transition);
					ImmediateContext.GenerateMips(nativeTexture.GetDefaultView(TextureViewType.ShaderResource));
				}
				else
				{
					var textureData = new TextureData { SubResources = [subResource], Context = ImmediateContext };
					nativeTexture = Device.CreateTexture(desc, textureData);
					if (nativeTexture == null)
					{
						throw new InvalidOperationException(
							$"Failed to create texture '{data.Name}' ({width}x{height}) - likely out of GPU memory.");
					}
				}
			}

			// DECA_TEX_DIAG=1 - фактическое число мипов КАЖДОЙ созданной текстуры. Диагностика тракта
			// mip bias: если у текстур один мип, LOD клампится в [0,0] и любые ручки сэмплера
			// (bias/анизотропия/MaxLOD) физически не могут ничего изменить.
			if (Environment.GetEnvironmentVariable("DECA_TEX_DIAG") == "1")
			{
				Console.WriteLine($"[texdiag] {data.Name}: {width}x{height} " +
					$"mips={nativeTexture.GetDesc().MipLevels} generate={generateMips}");
			}

			// Add a transition to ShaderResource. Diligent's engine doesn't automatically transition
			// the underlying native resource to ShaderResource correctly if you skip Context update
			// transition barriers in materials or if it's used inside indirect draw setups.
			ImmediateContext.TransitionResourceStates(
			[
				new StateTransitionDesc()
				{
					Resource = nativeTexture,
					OldState = global::Diligent.ResourceState.Unknown,
					NewState = global::Diligent.ResourceState.ShaderResource,
					Flags = StateTransitionFlags.UpdateState
				}
			]);

			var textureInfo = new DecaEngine.Graphics.TextureInfo
			{
				name = data.Name,
				width = (uint)width,
				height = (uint)height,
				format = TextureObjectFormat.R8G8B8A8UNorm,
				type = TextureType.Texture2D,
			};

			return new DiligentGpuTexture(data.Name, textureInfo, nativeTexture);
		}

		/// <summary>
		/// Texture2DArray из готовых CPU-слоёв ОДНОГО размера (RGBA8, один мип на слой). Нужна
		/// атласу текстур RT-хитов (см. SsrHitTextures): остальные пути создания сэмплируемых
		/// текстур прибиты к Tex2d. Формат - UNorm без sRGB, как у всех текстур движка: перевод в
		/// линейное пространство делает шейдер (pow 2.2), см. DiligentResourceFormats.
		/// </summary>
		public unsafe IGpuTexture CreateTextureArray(string name, int width, int height,
			IReadOnlyList<byte[]> layers)
		{
			if (layers.Count == 0)
			{
				throw new ArgumentException("Texture array needs at least one layer", nameof(layers));
			}

			var desc = new TextureDesc
			{
				Name = name,
				Type = ResourceDimension.Tex2dArray,
				Width = (uint)width,
				Height = (uint)height,
				ArraySizeOrDepth = (uint)layers.Count,
				Format = TextureFormat.RGBA8_UNorm,
				BindFlags = BindFlags.ShaderResource,
				Usage = Usage.Immutable,
				MipLevels = 1,
			};

			var handles = new System.Runtime.InteropServices.GCHandle[layers.Count];
			try
			{
				var subResources = new TextureSubResData[layers.Count];
				for (int i = 0; i < layers.Count; i++)
				{
					if (layers[i].Length < width * height * 4)
					{
						throw new ArgumentException(
							$"Texture array '{name}' layer {i} holds {layers[i].Length} bytes, expected {width * height * 4}");
					}

					handles[i] = System.Runtime.InteropServices.GCHandle.Alloc(
						layers[i], System.Runtime.InteropServices.GCHandleType.Pinned);
					subResources[i] = new TextureSubResData
					{
						Data = handles[i].AddrOfPinnedObject(),
						Stride = (uint)(width * 4),
					};
				}

				var textureData = new TextureData { SubResources = subResources, Context = ImmediateContext };
				var nativeTexture = Device.CreateTexture(desc, textureData);
				if (nativeTexture == null)
				{
					throw new InvalidOperationException(
						$"Failed to create texture array '{name}' ({width}x{height}x{layers.Count}) - likely out of GPU memory.");
				}

				ImmediateContext.TransitionResourceStates(
				[
					new StateTransitionDesc()
					{
						Resource = nativeTexture,
						OldState = global::Diligent.ResourceState.Unknown,
						NewState = global::Diligent.ResourceState.ShaderResource,
						Flags = StateTransitionFlags.UpdateState
					}
				]);

				var textureInfo = new DecaEngine.Graphics.TextureInfo
				{
					name = name,
					width = (uint)width,
					height = (uint)height,
					format = TextureObjectFormat.R8G8B8A8UNorm,
					type = TextureType.Texture2DArray,
				};

				return new DiligentGpuTexture(name, textureInfo, nativeTexture);
			}
			finally
			{
				foreach (var handle in handles)
				{
					if (handle.IsAllocated)
					{
						handle.Free();
					}
				}
			}
		}

		/// <summary>
		/// Создаёт текстуру из готовой блочно-сжатой мип-цепочки (см. DtexFile / ассет-пайплайн).
		///
		/// Ни GenerateMips, ни RenderTarget-бинда здесь нет и быть не может: BC-формат не
		/// фильтруется и не пишется как render target, а цепочка и так забейкана оффлайн. Ресурс
		/// создаётся Immutable одним вызовом со всеми уровнями сразу - это самый дешёвый путь
		/// «данные -&gt; VRAM», который вообще даёт API.
		/// </summary>
		private IGpuTexture CreateCompressedTexture(CpuTextureData data)
		{
			var format = data.CompressedFormat;
			var nativeFormat = DiligentResourceFormats.ToNativeFormat(format);
			if (nativeFormat == TextureFormat.Unknown)
			{
				throw new InvalidOperationException(
					$"Texture '{data.Name}' declares compressed format {format}, which has no Diligent mapping.");
			}

			var mips = data.CompressedMips;
			int width = data.CompressedWidth;
			int height = data.CompressedHeight;

			var desc = new TextureDesc
			{
				Name = data.Name,
				Type = ResourceDimension.Tex2d,
				Width = (uint)width,
				Height = (uint)height,
				Format = nativeFormat,
				BindFlags = BindFlags.ShaderResource,
				Usage = Usage.Immutable,
				MipLevels = (uint)mips.Length,
			};

			var handles = new GCHandle[mips.Length];
			var subResources = new TextureSubResData[mips.Length];
			ITexture nativeTexture;

			try
			{
				for (int i = 0; i < mips.Length; i++)
				{
					handles[i] = GCHandle.Alloc(mips[i], GCHandleType.Pinned);

					// Stride блочного формата - это длина РЯДА БЛОКОВ, а не строки текселей: один ряд
					// покрывает 4 строки по высоте, а ширина округляется вверх до кратной 4. Ошибка
					// здесь не падает, а тихо разъезжает текстуру по диагонали.
					int mipWidth = Math.Max(1, width >> i);
					subResources[i] = new TextureSubResData
					{
						Data = handles[i].AddrOfPinnedObject(),
						Stride = (uint)TextureFormatLayout.RowPitch(format, mipWidth),
					};
				}

				var textureData = new TextureData { SubResources = subResources, Context = ImmediateContext };
				nativeTexture = Device.CreateTexture(desc, textureData);
			}
			finally
			{
				foreach (var handle in handles)
				{
					if (handle.IsAllocated)
					{
						handle.Free();
					}
				}
			}

			// Как и в CreateTexture: при нехватке VRAM Diligent пишет ошибку в лог и возвращает null,
			// а использование null-хендла дальше - AV без managed-исключения.
			if (nativeTexture == null)
			{
				throw new InvalidOperationException(
					$"Failed to create compressed texture '{data.Name}' ({width}x{height}, {format}, " +
					$"{mips.Length} mips) - likely out of GPU memory.");
			}

			if (Environment.GetEnvironmentVariable("DECA_TEX_DIAG") == "1")
			{
				Console.WriteLine($"[texdiag] {data.Name}: {width}x{height} {format} mips={mips.Length} (baked)");
			}

			// Тот же явный переход в ShaderResource, что и в CreateTexture - см. комментарий там.
			ImmediateContext.TransitionResourceStates(
			[
				new StateTransitionDesc()
				{
					Resource = nativeTexture,
					OldState = global::Diligent.ResourceState.Unknown,
					NewState = global::Diligent.ResourceState.ShaderResource,
					Flags = StateTransitionFlags.UpdateState
				}
			]);

			var textureInfo = new DecaEngine.Graphics.TextureInfo
			{
				name = data.Name,
				width = (uint)width,
				height = (uint)height,
				mipLevels = (uint)mips.Length,
				format = format,
				type = TextureType.Texture2D,
			};

			return new DiligentGpuTexture(data.Name, textureInfo, nativeTexture);
		}

		/// <summary>См. <see cref="IGraphicsApi.CreateTexture2DWithMips"/>.</summary>
		public unsafe IGpuTexture CreateTexture2DWithMips(string name, IReadOnlyList<byte[]> mipPixels, int width, int height, bool floatFormat = false)
		{
			int bytesPerPixel = floatFormat ? 8 : 4;

			var desc = new TextureDesc
			{
				Name = name,
				Type = ResourceDimension.Tex2d,
				Width = (uint)width,
				Height = (uint)height,
				Format = floatFormat ? TextureFormat.RGBA16_Float : TextureFormat.RGBA8_UNorm,
				BindFlags = BindFlags.ShaderResource,
				Usage = Usage.Immutable,
				MipLevels = (uint)mipPixels.Count,
			};

			var handles = new GCHandle[mipPixels.Count];
			var subResources = new TextureSubResData[mipPixels.Count];
			ITexture nativeTexture;

			try
			{
				for (int i = 0; i < mipPixels.Count; i++)
				{
					int mipWidth = Math.Max(1, width >> i);
					handles[i] = GCHandle.Alloc(mipPixels[i], GCHandleType.Pinned);
					subResources[i] = new TextureSubResData
					{
						Data = handles[i].AddrOfPinnedObject(),
						Stride = (uint)(mipWidth * bytesPerPixel),
					};
				}

				var textureData = new TextureData { SubResources = subResources, Context = ImmediateContext };
				nativeTexture = Device.CreateTexture(desc, textureData);
			}
			finally
			{
				foreach (var handle in handles)
				{
					if (handle.IsAllocated)
					{
						handle.Free();
					}
				}
			}

			// Тот же явный переход в ShaderResource, что и в CreateTexture выше - см. комментарий там.
			ImmediateContext.TransitionResourceStates(
			[
				new StateTransitionDesc()
				{
					Resource = nativeTexture,
					OldState = global::Diligent.ResourceState.Unknown,
					NewState = global::Diligent.ResourceState.ShaderResource,
					Flags = StateTransitionFlags.UpdateState
				}
			]);

			var textureInfo = new DecaEngine.Graphics.TextureInfo
			{
				name = name,
				width = (uint)width,
				height = (uint)height,
				format = floatFormat ? TextureObjectFormat.R16G16B16A16Float : TextureObjectFormat.R8G8B8A8UNorm,
				type = TextureType.Texture2D,
			};

			return new DiligentGpuTexture(name, textureInfo, nativeTexture);
		}

		/// <summary>См. <see cref="IGraphicsApi.CreateTexture2DMutable"/>.</summary>
		public IGpuTexture CreateTexture2DMutable(string name, int width, int height,
			bool floatFormat = false, bool unorderedAccess = false)
		{
			var desc = new TextureDesc
			{
				Name = name,
				Type = ResourceDimension.Tex2d,
				Width = (uint)width,
				Height = (uint)height,
				Format = floatFormat ? TextureFormat.RGBA16_Float : TextureFormat.RGBA8_UNorm,
				BindFlags = unorderedAccess
					? BindFlags.ShaderResource | BindFlags.UnorderedAccess
					: BindFlags.ShaderResource,
				// Default (не Dynamic): заливаем целиком командой UpdateTexture, а не через Map -
				// Dynamic-текстуры в D3D12/Vulkan-бэкендах Diligent не поддерживаются.
				Usage = Usage.Default,
				MipLevels = 1,
			};

			var nativeTexture = Device.CreateTexture(desc, new TextureData());
			if (nativeTexture == null)
			{
				// Diligent сообщает об отказе в лог и возвращает null. Без этой проверки null уезжал
				// бы дальше и падал уже на первом использовании - падение процесса вместо внятной
				// ошибки, которую вызывающий мог бы обработать откатом.
				throw new InvalidOperationException(
					$"Failed to create texture '{name}' ({width}x{height}, " +
					$"{(floatFormat ? "RGBA16F" : "RGBA8")}{(unorderedAccess ? ", UAV" : string.Empty)}) - " +
					"see the graphics log above; the device may have been removed");
			}

			// Тот же явный переход в ShaderResource, что и в CreateTexture выше - см. комментарий там.
			ImmediateContext.TransitionResourceStates(
			[
				new StateTransitionDesc()
				{
					Resource = nativeTexture,
					OldState = global::Diligent.ResourceState.Unknown,
					NewState = global::Diligent.ResourceState.ShaderResource,
					Flags = StateTransitionFlags.UpdateState
				}
			]);

			var textureInfo = new DecaEngine.Graphics.TextureInfo
			{
				name = name,
				width = (uint)width,
				height = (uint)height,
				format = floatFormat ? TextureObjectFormat.R16G16B16A16Float : TextureObjectFormat.R8G8B8A8UNorm,
				type = TextureType.Texture2D,
			};

			return new DiligentGpuTexture(name, textureInfo, nativeTexture);
		}

		/// <summary>См. <see cref="IGraphicsApi.UpdateTexture2D"/>.</summary>
		public unsafe void UpdateTexture2D(IGpuTexture texture, byte[] pixels)
		{
			if (texture is not DiligentGpuTexture gpuTexture)
			{
				throw new ArgumentException($"Expected a Diligent texture, got {texture.GetType().Name}",
					nameof(texture));
			}

			var info = gpuTexture.Info;
			int bytesPerPixel = info.format == TextureObjectFormat.R16G16B16A16Float ? 8 : 4;
			long expected = (long)info.width * info.height * bytesPerPixel;
			if (pixels.LongLength < expected)
			{
				throw new ArgumentException(
					$"Texture '{texture.Name}' needs {expected} bytes, got {pixels.LongLength}", nameof(pixels));
			}

			var box = new Box
			{
				MinX = 0, MaxX = info.width,
				MinY = 0, MaxY = info.height,
				MinZ = 0, MaxZ = 1,
			};

			var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
			try
			{
				var subResource = new TextureSubResData
				{
					Data = handle.AddrOfPinnedObject(),
					Stride = (uint)(info.width * bytesPerPixel),
				};

				ImmediateContext.UpdateTexture(gpuTexture.Texture, 0, 0, box, subResource,
					ResourceStateTransitionMode.Transition, ResourceStateTransitionMode.Transition);
			}
			finally
			{
				handle.Free();
			}

			// UpdateTexture оставляет текстуру в CopyDest. SRB материалов уже собраны и ждут её в
			// ShaderResource, а автоматический переход на месте использования их не спасает -
			// Vulkan-валидация ловит несовпадение layout-а дескриптора (VUID-vkCmdDraw-None-09600).
			// Возвращаем состояние явно, здесь же.
			ImmediateContext.TransitionResourceStates(
			[
				new StateTransitionDesc()
				{
					Resource = gpuTexture.Texture,
					OldState = global::Diligent.ResourceState.CopyDest,
					NewState = global::Diligent.ResourceState.ShaderResource,
					Flags = StateTransitionFlags.UpdateState
				}
			]);
		}

		public IRenderTarget CreateRenderTarget(TextureInfo info)
		{
			return new DiligentRenderTarget(Device, info);
		}

		public IBufferHandle CreateBuffer(BufferInfo info)
		{
			var buffer = new DiligentBufferHandle(Device);
			buffer.Alloc(info);
			return buffer;
		}

		public ICommandBuffer CreateCommandBuffer()
		{
			return new DiligentCommandBuffer(ImmediateContext);
		}

		public ITextureView GetBackBufferColorView()
		{
			return SwapChain.GetCurrentBackBufferRTV();
		}

		public ITextureView GetBackBufferDepthView()
		{
			return SwapChain.GetDepthBufferDSV();
		}

		public IBufferHandle CreateBuffer<T>(BufferInfo info)
		{
			info.sizeInBytes = (uint)Marshal.SizeOf<T>();
			var handle = new DiligentBufferHandle(Device);
			handle.Alloc(info);
			return handle;
		}

		public IBufferHandle CreateBuffer<T>(int count, BufferInfo info)
		{
			info.stride = (uint)Marshal.SizeOf<T>();
			info.sizeInBytes = (uint)count * info.stride;
			var handle = new DiligentBufferHandle(Device);
			handle.Alloc(info);
			return handle;
		}

		public IRenderHandle CreateRenderHandle(TextureInfo info)
		{
			var handle = new DiligentRenderHandle(Device);
			handle.Alloc(info);
			return handle;
		}

		public void Initialize(GraphicsBackend backend)
		{
			DiligentEnumMapping.Backend = backend;
			switch (backend)
			{
				case GraphicsBackend.D3D11:
					SetupD3D11();
					break;
				case GraphicsBackend.D3D12:
					SetupD3D12();
					break;
				case GraphicsBackend.Vulkan:
					SetupVulkan();
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(backend), backend, null);
			}

			// Имя файла кэша - пер-бэкендное: D3D12 pipeline library и Vulkan pipeline cache
			// бинарно несовместимы, а редактор (D3D12) и CLI-пробы (Vulkan) стартуют из одной
			// рабочей директории (см. DiligentPsoManager).
			PsoManager = new DiligentPsoManager(Device,
				Path.Combine(Environment.CurrentDirectory, $"cache.{backend.ToString().ToLowerInvariant()}.pso"));
			// Директория тоже пер-бэкендная: DXIL, DXBC и SPIR-V взаимно нечитаемы, а ключ записи
			// на разных бэкендах совпал бы с точностью до макроса DECA_VULKAN (см. тот же довод у
			// файла PSO-кеша выше).
			ShaderBytecodeCache = DiligentShaderBytecodeCache.Create(
				Path.Combine(Environment.CurrentDirectory, $"cache.{backend.ToString().ToLowerInvariant()}.shaders"),
				backend.ToString());
			WindowHandle.OnWindowResize += OnWindowHandleResize;

			void SetupD3D11()
			{
				var engineFactory = Native.GetEngineFactoryD3D11();
				if (engineFactory is null)
				{
					throw new NullReferenceException($"Failed to get {nameof(IEngineFactoryD3D11)}");
				}
				engineFactory.SetMessageCallback(OnMessageCallback);

				var adapter = FindBestAdapter(engineFactory);
				var createInfo = new EngineD3D11CreateInfo
				{
					EnableValidation = ResolveValidationEnabled(),
					GraphicsAPIVersion = new Version(11, 0),
					AdapterId = (uint)adapter
				};

				var setupInfo = new GraphicsPipelineSetupInfo
				{
					backend = backend
				};
				OnCreateSetupInfo?.Invoke(setupInfo);
				if (setupInfo.contextCount > 0)
				{
					createInfo.NumDeferredContexts = setupInfo.contextCount;
				}

				engineFactory.CreateDeviceAndContextsD3D11(createInfo, out var renderDevice, out var deviceContexts);

				_engineFactory = engineFactory;
				_immediateContext = deviceContexts[0];
				_deferredDevices = deviceContexts.ToArray();
				_renderDevice = renderDevice;

				var wndSize = WindowHandle.Size;
				var swapChainDesc = new SwapChainDesc
				{
					Width = (uint)wndSize.X,
					Height = (uint)wndSize.Y,
					BufferCount = 2,
					Usage = SwapChainUsageFlags.RenderTarget,
					IsPrimary = true,
					ColorBufferFormat = global::Diligent.TextureFormat.RGBA8_UNorm,
					DepthBufferFormat = global::Diligent.TextureFormat.D32_Float
				};

				OnSwapChainInfo?.Invoke();

				var swapChain = engineFactory.CreateSwapChainD3D11(renderDevice, deviceContexts[0], swapChainDesc,
					new FullScreenModeDesc(), new Win32NativeWindow { Wnd = WindowHandle.Handle });

				_swapChain = swapChain;
			}

			void SetupD3D12()
			{
				var engineFactory = Native.GetEngineFactoryD3D12();
				if (engineFactory is null)
				{
					throw new NullReferenceException($"Failed to get {nameof(IEngineFactoryD3D12)}");
				}
				engineFactory.SetMessageCallback(OnMessageCallback);
				engineFactory.LoadD3D12("d3d12.dll");

				var adapter = FindBestAdapter(engineFactory);

				var createInfo = new EngineD3D12CreateInfo
				{
					EnableValidation = ResolveValidationEnabled(),
					AdapterId = (uint)adapter,
				};

				var setupInfo = new GraphicsPipelineSetupInfo
				{
					backend = backend
				};
				OnCreateSetupInfo?.Invoke(setupInfo);
				if (setupInfo.contextCount > 0)
				{
					createInfo.NumDeferredContexts = setupInfo.contextCount;
				}

				if (setupInfo.dynamicHeapPageSize > 0)
				{
					createInfo.DynamicHeapPageSize = setupInfo.dynamicHeapPageSize;
				}

				createInfo.Features = new DeviceFeatures()
				{
					MultiViewport = DeviceFeatureState.Enabled,
					// Optional, а не Enabled: на железе/драйвере без трассировки Enabled уронил бы
					// создание устройства, а так фича просто останется выключенной, и вызывающий
					// уйдёт на compute-обход своего BVH (см. IGraphicsApi.RayTracing).
					RayTracing = DeviceFeatureState.Optional,
					// Динамическая индексация массивов шейдерных ресурсов (Texture2D arr[N] с
					// NonUniformResourceIndex) - «bindless»-текстуры RT-хитов SSR. Тоже Optional:
					// на D3D12 бесплатно всегда, на Vulkan включает descriptor indexing, если
					// драйвер умеет; наличие проверяется через SupportsShaderResourceArrays.
					ShaderResourceRuntimeArrays = DeviceFeatureState.Optional,
				};

				engineFactory.CreateDeviceAndContextsD3D12(createInfo, out var renderDevice, out var deviceContexts);
				_engineFactory = engineFactory;
				_immediateContext = deviceContexts[0];
				_deferredDevices = deviceContexts.ToArray();
				_renderDevice = renderDevice;

				var wndSize = WindowHandle.Size;
				var swapChainDesc = new SwapChainDesc
				{
					Width = (uint)wndSize.X,
					Height = (uint)wndSize.Y,
					BufferCount = 2,
					Usage = SwapChainUsageFlags.RenderTarget,
					IsPrimary = true,
					ColorBufferFormat = global::Diligent.TextureFormat.RGBA8_UNorm,
					DepthBufferFormat = global::Diligent.TextureFormat.D32_Float
				};

				OnSwapChainInfo?.Invoke();
				var swapChain = engineFactory.CreateSwapChainD3D12(renderDevice, deviceContexts[0], swapChainDesc,
					new FullScreenModeDesc(), new Win32NativeWindow { Wnd = WindowHandle.Handle });

				_swapChain = swapChain;
			}

			void SetupVulkan()
			{
				var engineFactory = Native.GetEngineFactoryVk();
				if (engineFactory is null)
				{
					throw new NullReferenceException($"Failed to get {nameof(IEngineFactoryVk)}");
				}
				engineFactory.SetMessageCallback(OnMessageCallback);

				var adapter = FindBestAdapter(engineFactory);
				var createInfo = new EngineVkCreateInfo
				{
					EnableValidation = ResolveValidationEnabled(),
					AdapterId = (uint)adapter
				};

				var setupInfo = new GraphicsPipelineSetupInfo
				{
					backend = backend
				};
				OnCreateSetupInfo?.Invoke(setupInfo);
				if (setupInfo.contextCount > 0)
				{
					createInfo.NumDeferredContexts = setupInfo.contextCount;
				}
				if (setupInfo.dynamicHeapSize > 0)
				{
					createInfo.DynamicHeapSize = setupInfo.dynamicHeapSize;
				}
				if (setupInfo.dynamicHeapPageSize > 0)
				{
					createInfo.DynamicHeapPageSize = setupInfo.dynamicHeapPageSize;
				}

				createInfo.Features = new DeviceFeatures()
				{
					MultiViewport = DeviceFeatureState.Enabled,
					// Optional, а не Enabled: на железе/драйвере без трассировки Enabled уронил бы
					// создание устройства, а так фича просто останется выключенной, и вызывающий
					// уйдёт на compute-обход своего BVH (см. IGraphicsApi.RayTracing).
					RayTracing = DeviceFeatureState.Optional,
					// Динамическая индексация массивов шейдерных ресурсов (Texture2D arr[N] с
					// NonUniformResourceIndex) - «bindless»-текстуры RT-хитов SSR. Тоже Optional:
					// на D3D12 бесплатно всегда, на Vulkan включает descriptor indexing, если
					// драйвер умеет; наличие проверяется через SupportsShaderResourceArrays.
					ShaderResourceRuntimeArrays = DeviceFeatureState.Optional,
				};

				engineFactory.CreateDeviceAndContextsVk(createInfo, out var renderDevice, out var deviceContexts);
				_engineFactory = engineFactory;
				_immediateContext = deviceContexts[0];
				_deferredDevices = deviceContexts.ToArray();
				_renderDevice = renderDevice;

				var wndSize = WindowHandle.Size;
				var swapChainDesc = new SwapChainDesc
				{
					Width = (uint)wndSize.X,
					Height = (uint)wndSize.Y,
					BufferCount = 3, // Use triple buffering for Vulkan
					Usage = SwapChainUsageFlags.RenderTarget,
					IsPrimary = true,
					ColorBufferFormat = global::Diligent.TextureFormat.RGBA8_UNorm,
					DepthBufferFormat = global::Diligent.TextureFormat.D32_Float
				};

				OnSwapChainInfo?.Invoke();

				var swapChain = engineFactory.CreateSwapChainVk(renderDevice, deviceContexts[0], swapChainDesc, new Win32NativeWindow { Wnd = WindowHandle.Handle });
				_swapChain = swapChain;
			}

			_frameFence = Device.CreateFence(new FenceDesc { Name = "Frame Fence" });
		}

		/// <summary>См. <see cref="IGraphicsApi.WaitForGpuIdle"/>.</summary>
		public void WaitForGpuIdle()
		{
			ImmediateContext.Flush();
			ImmediateContext.WaitForIdle();
		}

		private void OnWindowHandleResize()
		{
			// GPU must be idle before ResizeBuffers-style calls: the OS resize event can land mid-frame,
			// while commands referencing the current back buffer are still in flight. Resizing out from
			// under them leaves the device in a broken state, and every texture creation afterwards
			// starts failing (see the offscreen-target resize sites, which follow the same rule).
			ImmediateContext.Flush();
			ImmediateContext.WaitForIdle();

			SwapChain.Resize((uint)WindowHandle.Size.X, (uint)WindowHandle.Size.Y, SurfaceTransform.Identity);
		}

		private int FindBestAdapter(IEngineFactory factory)
		{
			var adapters = factory.EnumerateAdapters(new Version(11, 1)).ToList();

			GraphicsAdapterInfo suitableAdapter = default;
			int adapterIdx = 0;

			for (int i = 0; i < adapters.Count; i++)
			{
				if (adapters[i].Type == AdapterType.Discrete)
				{
					adapterIdx = i;
					suitableAdapter = adapters[i];
					break;
				}
			}

			if (suitableAdapter.DeviceId != 0)
			{
				return adapterIdx;
			}

			for (int i = 0; i < adapters.Count; i++)
			{
				if (adapters[i].Type == AdapterType.Integrated)
				{
					adapterIdx = i;
					suitableAdapter = adapters[i];
					break;
				}
			}

			if (suitableAdapter.DeviceId != 0)
			{
				return adapterIdx;
			}

			suitableAdapter = adapters.FirstOrDefault();

			return suitableAdapter.DeviceId != 0 ? 0 : throw new NullReferenceException("There's no graphics adapter available.");
		}

		/// <summary>См. <see cref="IGraphicsApi.SavePipelineCache"/>. Ошибки глотаются внутри
		/// DiligentPsoManager.SaveCache - протухший кэш там же удаляется.</summary>
		public void SavePipelineCache()
		{
			PsoManager?.SaveCache();
		}

		public void Present()
		{
			// SyncInterval 1 (vsync/FIFO) по умолчанию, не 0: с MAILBOX на бескапном фреймрейте
			// презентованные кадры не успевают реаквайриться, и свопчейн Diligent переиспользует их
			// семафоры (валидация: VUID-vkQueueSubmit-pSignalSemaphores-00067, затем каскад по фенсам
			// и командным буферам). Ставящий PresentInterval = 0 берёт этот риск на себя - лимит
			// кадров в полёте ниже держит очередь короткой.
			SwapChain.Present((uint)Math.Max(PresentInterval, 0));

			// Ограничение кадров в полёте (FramesInFlight). Без него (vsync = 0) CPU сабмитит кадры
			// без ограничений и убегает от GPU на десятки кадров: кольцевой DynamicHeap Diligent
			// обязан держать данные ВСЕХ кадров в полёте и исчерпывается ("Space in dynamic
			// heap is almost exhausted. Allocation forced idling the GPU"), пул командных
			// буферов переиспользует ещё исполняющиеся буферы (валидация Vulkan:
			// VUID-vkResetCommandBuffer-commandBuffer-00045 и компания), а на D3D12 latency
			// waitable свопчейна отваливается по таймауту.
			var fenceValue = _nextFrameValue++;
			ImmediateContext.EnqueueSignal(_frameFence, fenceValue);

			var framesInFlight = (ulong)Math.Max(FramesInFlight, 1);
			if (fenceValue > framesInFlight)
			{
				_frameFence!.Wait(fenceValue - framesInFlight);
			}
		}

		public void Release()
		{
			// Шарёные шейдеры живут до конца api - освобождаются только здесь.
			ReleaseShaderCache();

			_frameFence?.Dispose();
			PsoManager?.Dispose();
			_swapChain?.Dispose();
			foreach (var deviceContext in _deferredDevices)
			{
				deviceContext.Dispose();
			}

			_immediateContext?.Dispose();
			_renderDevice?.Dispose();

			WindowHandle.OnWindowResize -= OnWindowHandleResize;
		}
	}
}