using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using Diligent;
using UnsafeCollections.Collections.Native;

namespace DecaEngine.Core;

public enum TextureAddress
{
	Wrap,
	Mirror,
	Clamp,
	Border,
	MirrorOnce,
}

public enum TextureFilter : byte
{
	Unknown,
	Point,
	Linear,
	Anisotropic,
	ComparisonPoint,
	ComparisonLinear,
	ComparisonAnisotropic,
	MinimumPoint,
	MinimumLinear,
	MinimumAnisotropic,
	MaximumPoint,
	MaximumLinear,
	MaximumAnisotropic,
	NumFilters,
}

public enum CompFunction : byte
{
	Unknown,
	Never,
	Less,
	Equal,
	LessEqual,
	Greater,
	NotEqual,
	GreaterEqual,
	Always,
	NumFunctions,
}

/// <summary>Что устройство умеет по части трассировки лучей (см.
/// <see cref="IGraphicsApi.RayTracing"/>). Порядок значений - по возрастанию возможностей, так что
/// проверки вида "&gt;= Inline" осмысленны.</summary>
public enum RayTracingSupport
{
	/// <summary>Аппаратной трассировки нет: старое железо, программный бэкенд или драйвер без
	/// расширения. Остаётся compute-обход собственного BVH.</summary>
	None,

	/// <summary>Только полноценный конвейер (DXR 1.0): ray-gen/hit/miss шейдеры и таблица привязки.
	/// Лучи из обычного compute так не пустишь - нужен отдельный RT-конвейер.</summary>
	Pipeline,

	/// <summary>Есть inline-трассировка (RayQuery, DXR 1.1): луч пускается прямо из compute- или
	/// пиксельного шейдера, без отдельного конвейера и таблиц привязки. Для обновления проб это
	/// заметно проще - трассировка становится обычным вызовом внутри существующего шейдера.</summary>
	Inline,
}

public interface IGraphicsPipeline
{
	public void Initialize();
	public void Execute();

	public void SignalGraph(DirectionalLightCascadeData renderScene, RenderCamerasData renderViews);

	/// <summary>Debug-only per-frame render graph statistics. Always null in Release builds.</summary>
	public RenderGraphDebugSnapshot DebugSnapshot { get; }

	/// <summary>Debug-only recent frame history. Always null in Release builds.</summary>
	public RenderGraphDebugHistory DebugHistory { get; }
}

public interface IGraphicsApi : IReleaseObject
{
	public void Initialize(GraphicsBackend backend);

	public event Action<GraphicsPipelineSetupInfo> OnCreateSetupInfo;
	public event Action OnSwapChainInfo;

	public IWindowHandle WindowHandle { set; get; }

	public void Present();

	/// <summary>Интервал синхронизации <see cref="Present"/>: 1 (дефолт) - vsync, 0 - без ожидания
	/// (бескапный фреймрейт), больше - кратные интервалы. Переключается на живом api - значение
	/// читается на каждом презенте. Дефолт перекрывается переменной окружения DECA_VSYNC.</summary>
	public int PresentInterval { get; set; }

	/// <summary>Сбрасывает накопленный кэш скомпилированных PSO на диск, чтобы следующий запуск не
	/// компилировал их заново.
	///
	/// Зовётся ПОСЛЕ ЗАВЕРШЕНИЯ ЗАГРУЗКИ модели - именно там кэш пополнился и именно там пауза
	/// незаметна. Без этого вызова кэш существовал, но никогда не доживал до следующего запуска:
	/// сохранение было привязано к Dispose графического API, а он на практике не происходит, и файла
	/// на диске просто не было. Замерено на Sponza: создание PSO - около 5.2 с из 6.8 с всей
	/// загрузки (26 материалов, ~200 мс на каждый), при том что заливка текстур стоит 73 мс, а
	/// компиляция шейдеров - 0.
	///
	/// Реализация обязана быть безопасной к повторным вызовам и не бросать: неудачное сохранение -
	/// не повод ронять загрузку.</summary>
	public void SavePipelineCache();

	public void SetBackBufferTarget(Vector4 color);


	public IMeshObject CreateMesh(string name);
	public IMaterialObject CreateMaterial(string name);
	public IComputeMaterial CreateComputeMaterial(string name); // Added for Compute
	public IShaderObject CreateShader(string name, string factoryPath, string filePath, ShaderObjectType type);
	public IShaderObject CreateShader(string name, string factoryPath, string filePath, ShaderObjectType type, string entryPoint);

	/// <summary>Вариант шейдера с ключевыми словами (shader keywords): каждый ключ уходит в
	/// компиляцию макросом со значением 1, шейдер вырезает выключенные эффекты через #if - в
	/// отличие от рантайм-веток по cbuffer-флагам, выключенный эффект не стоит ни регистров, ни
	/// привязок. Кэширование вариантов - на совести вызывающего (см. ModelLoader).</summary>
	public IShaderObject CreateShader(string name, string factoryPath, string filePath, ShaderObjectType type, string entryPoint, IReadOnlyList<string> keywords);

	/// <summary>Шейдер из ОБЩЕГО кэша бэкенда (ключ - файл + точка входа + тип + набор кейвордов):
	/// один и тот же вариант не компилируется заново для каждой загруженной модели. Компиляция
	/// большого PS-варианта стоит секунды и идёт синхронно на потоке рендера, а варианты у моделей
	/// почти всегда одни и те же - без кэша каждое переключение модели в Asset Browser платило за
	/// них заново.
	///
	/// Экземпляр ШАРЕНЫЙ и живёт до конца api: его <see cref="IShaderObject.Release"/> - no-op.
	/// Поэтому звать можно только оттуда, где материалы помечены <see
	/// cref="IMaterialObject.OwnsShaders"/> = false (материал с OwnsShaders=true освобождает
	/// нативный шейдер напрямую и убил бы шарёный экземпляр под чужими живыми материалами).</summary>
	public IShaderObject CreateSharedShader(string name, string factoryPath, string filePath,
		ShaderObjectType type, string entryPoint = "Main", IReadOnlyList<string> keywords = null);

	/// <summary>
	/// Создает abstract Graphics Pipeline State (<see cref="PipelineStateType.Graphics"/>) из
	/// backend-независимого описания <see cref="GraphicsStateInfo"/>. Конкретный backend
	/// (например DiligentEngine) сам решает, во что превратить это описание внутри себя.
	/// </summary>
	public IStateObject CreateGraphicsState(GraphicsStateInfo info);

	/// <summary>
	/// Создает abstract Compute Pipeline State (<see cref="PipelineStateType.Compute"/>) из
	/// backend-независимого описания <see cref="ComputeStateInfo"/>.
	/// </summary>
	public IStateObject CreateComputeState(ComputeStateInfo info);

	/// <summary>Что устройство умеет по части аппаратной трассировки. Запрашивается опционально при
	/// создании устройства, так что на железе без поддержки просто вернётся
	/// <see cref="RayTracingSupport.None"/> - вызывающий выбирает между аппаратным путём и
	/// compute-обходом собственного BVH.</summary>
	public RayTracingSupport RayTracing { get; }

	/// <summary>Умеет ли девайс динамическую индексацию массивов шейдерных ресурсов
	/// (Texture2D arr[N] с NonUniformResourceIndex) - гейт bindless-режима текстур RT-хитов SSR.
	/// Дефолт false: включает только Diligent-бэкенд, запросивший фичу при создании девайса.</summary>
	public bool SupportsShaderResourceArrays => false;

	public IGpuTexture CreateTexture(CpuTextureData data);

	/// <summary>Immutable Texture2DArray из готовых CPU-слоёв одного размера (RGBA8 без sRGB, один
	/// мип на слой) - атлас текстур RT-хитов SSR. Дефолт null: реализует только Diligent-бэкенд;
	/// вызывающий обязан пережить отсутствие (режим атласа тогда остаётся на плейсхолдере).</summary>
	public IGpuTexture? CreateTextureArray(string name, int width, int height,
		IReadOnlyList<byte[]> layers) => null;

	/// <summary>Immutable 2D-текстура с ЯВНОЙ мип-цепочкой: mipPixels[0] - базовый уровень
	/// width x height, каждый следующий вдвое меньше (min 1). Нужна там, где мипы несут смысловую
	/// нагрузку, а не просто уменьшение - например, префильтрованный по roughness энвайронмент
	/// превью (см. PreviewEnvironmentMap): SampleLevel(mip = f(roughness)).
	/// floatFormat = true - RGBA16Float (байты - little-endian half4 на пиксель, stride w*8) для
	/// HDR-содержимого; false - RGBA8 (stride w*4).</summary>
	public IGpuTexture CreateTexture2DWithMips(string name, IReadOnlyList<byte[]> mipPixels, int width, int height, bool floatFormat = false);

	/// <summary>ИЗМЕНЯЕМАЯ 2D-текстура без мипов под многократные перезаливки CPU-содержимого
	/// (см. <see cref="UpdateTexture2D"/>). Нужна там, где данные обновляются часто, а Immutable-
	/// вариант заставлял бы пересоздавать текстуру: прогрессивный бейк probe-GI (см.
	/// ProbeGiTextures) выкладывает новый снимок атласов каждый раунд, а пересоздание тянет за
	/// собой Flush+WaitForIdle и пересборку привязок материалов.
	/// floatFormat - как у <see cref="CreateTexture2DWithMips"/>.</summary>
	/// unorderedAccess = true дополнительно разрешает запись из шейдера (UAV) - нужно там, где
	/// содержимое считает GPU, а не CPU (см. probe-GI на compute: раунд пишет атласы напрямую,
	/// и промежуточная упаковка на CPU исчезает вовсе).
	public IGpuTexture CreateTexture2DMutable(string name, int width, int height,
		bool floatFormat = false, bool unorderedAccess = false);

	/// <summary>Перезаливает всё содержимое текстуры, созданной <see cref="CreateTexture2DMutable"/>
	/// (раскладка байт та же, что у mipPixels[0]). Зовётся с потока, владеющего немедленным
	/// контекстом.</summary>
	public void UpdateTexture2D(IGpuTexture texture, byte[] pixels);

	public IRenderTarget CreateRenderTarget(TextureInfo info);

	/// <summary>Отдельный GPU-буфер (в первую очередь константный) для материалов, которым мало
	/// <see cref="IMaterialObject.SetConstant"/>: тот попутно переустанавливает переменную в SRB, а
	/// со СВОИМ буфером содержимое заливается командой UpdateBuffer прямо в кадре - в том числе из
	/// заморожённого командного буфера (см. <see cref="EyeAdaptationPassResources"/>).</summary>
	public IBufferHandle CreateBuffer(BufferInfo info);

	/// <summary>Creates a new, backend-specific <see cref="ICommandBuffer"/> ready for recording.</summary>
	public ICommandBuffer CreateCommandBuffer();

	public IRenderGraph CreateRenderGraph();

	/// <summary>Flush + ожидание, пока GPU не доработает все отправленные команды. Обязательно перед
	/// освобождением или пересозданием любого ресурса, на который могут ссылаться кадры в полёте -
	/// см. <see cref="GraphicsPipelineSimple.SetFeatures"/> и ресайз офскрин-таргетов.</summary>
	public void WaitForGpuIdle();

	/// <summary>Raw texture view of the current swap chain back buffer's color target.</summary>
	public ITextureView GetBackBufferColorView();

	/// <summary>Raw texture view of the current swap chain back buffer's depth target.</summary>
	public ITextureView GetBackBufferDepthView();

	/// <param name="mipLodBias">Смещение выбора мип-уровня. Отрицательное - мипы мельче, чем даёт
	/// экранная производная: обязательное условие темпорального апскейла (log2 масштаба рендера),
	/// иначе при уменьшенном рендер-разрешении мелкие мипы не сэмплируются вовсе и аккумулятору
	/// нечего восстанавливать.</param>
	public ISamplerObject CreateSampler(string name,
		TextureFilter filter,
		TextureAddress address,
		CompFunction comparisonFunction,
		Vector4 border,
		float mipLodBias = 0f);
}

// Legacy, transitioning to IRenderTarget
public interface IRenderHandle : IReleaseObject
{
	public Vector2 Size { get; }
	public void Alloc(TextureInfo info);
	public void Resize(Vector2 size);
}

// New: Represents a texture in VRAM that can be rendered to
public interface IRenderTarget : IGpuTexture
{
	public Vector2 Size { get; }
	public void Resize(Vector2 size);
}