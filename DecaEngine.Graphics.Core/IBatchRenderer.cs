using System.Collections.Generic;
using DecaEngine.Graphics.Diligent;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics;

/// <summary>
/// Opaque handle to the result of a compute-culling dispatch (<see cref="IBatchRenderer.ExecuteComputeCulling"/>).
/// Backend implementations return a concrete type implementing this marker interface; callers should
/// only ever pass it back into <see cref="IBatchRenderer.ExecuteDrawShadows"/> / <see cref="IBatchRenderer.ExecuteDrawBatching"/>.
/// </summary>
public interface ICullResult
{
}

/// <summary>Which materials <see cref="IBatchRenderer.ExecuteDrawBatching(ICommandBuffer, ICullResult, BatchDrawFilter)"/>
/// draws - the split lets a pass render opaque geometry, snapshot the color target, then render
/// transmissive materials that sample the snapshot for refraction (see <see cref="ForwardPass"/>).</summary>
public enum BatchDrawFilter
{
	All,
	OpaqueOnly,
	TransparentOnly,
}

/// <summary>
/// Backend-agnostic surface over the GPU-driven instanced batch renderer. This is what
/// <see cref="IGraphicsPipeline"/> uses to actually cull and draw the scene for every camera
/// and shadow cascade view it is given.
/// </summary>
public interface IBatchRenderer
{
	/// <summary>True when cached indirect draw commands / GPU buffers need to be rebuilt.</summary>
	bool IsDirty { get; }

	/// <summary>Инстанс-данные изменились по содержимому (мировые матрицы, DrawData) без смены
	/// состава - буферы перезальются, команды не перестраиваются.</summary>
	void MarkInstancesContentDirty();

	/// <summary>Пиновка живого подмножества инстансов (см. <see cref="BatchSubset"/>): рендерер
	/// читает массивы напрямую из него при каждой заливке.</summary>
	void PinInstances(BatchSubset subset);

	/// <summary>Number of shadow-map cascades supported by the shadow renderer.</summary>
	int ShadowCascadeCount { get; }

	/// <summary>(Re)allocates GPU buffers if the number of instances/batches changed since last frame.</summary>
	void CheckAndReallocateBuffers();

	/// <summary>Resets the indirect draw argument buffers/counters before a new culling pass.</summary>
	void ClearIndirectDrawBuffers(ICommandBuffer cmd);

	/// <summary>Привязывает общий View-кбуфер (обновляемый SetupViewData) к материалу, который НЕ
	/// регистрируется как батч-материал - например, фуллскрин-скай/SSAO материалы (см. ForwardPass,
	/// SsaoPass): им нужна камера, но Register() тянет за собой лишние ресурсы (инстанс-буферы, тени).</summary>
	void BindViewConstants(IMaterialObject material);

	/// <summary>Привязывает кбуфер Light (матрицы каскадов, направление и цвет солнца) и сам массив
	/// shadow map к материалу, который НЕ регистрируется как батч-материал, - фуллскрин-пассу,
	/// которому нужны тени сцены (см. <see cref="VolumetricLightPass"/>: марш объёмного света
	/// выбирает shadow map в каждой точке луча). Register() для такого материала избыточен - он
	/// тянет за собой инстанс-буферы и прочую машинерию рисования геометрии.</summary>
	void BindShadowResources(IMaterialObject material);

	/// <summary>Переводит массив shadow map в состояние чтения из шейдера. Нужен пассам, которые
	/// читают тени ВНЕ рисования геометрии (см. <see cref="BindShadowResources"/>): переход, который
	/// делает <see cref="ExecuteDrawBatching(ICommandBuffer, ICullResult)"/>, полагаться на себя не
	/// позволяет - пассы включаются и выключаются по имени, а выключенный пасс не делает и своих
	/// переходов.</summary>
	void TransitionShadowMapsForRead(ICommandBuffer cmd);

	void SetupViewData(ICommandBuffer cmd, ref ViewData viewData);
	void SetupCullData(ICommandBuffer cmd, ref CullData cullData);
	void SetupLightData(ICommandBuffer cmd, ref LightData lightData);

	/// <summary>Загружает пул punctual-светов кадра (PunctualLight, см. ViewSubset.punctualLights) в
	/// GPU-буфер. Команда замороженная: запоминается сырой указатель и память перечитывается на каждом
	/// реплее - пул обязан жить в стабильной unmanaged-памяти. Зовётся один раз на кадр перед пер-камерными
	/// диспатчами <see cref="ExecuteLightClustering"/>; какая камера каким отрезком пула владеет,
	/// сообщает её LightData.ClusterParams.</summary>
	unsafe void SetupPunctualLights(ICommandBuffer cmd, UnsafeArray* lights);

	/// <summary>Диспатчит компьют-кластеризацию светов (LightClusterCS.hlsl): раскладывает отрезок
	/// пула текущей камеры (границы - из последнего <see cref="SetupLightData"/>) по фроксел-кластерам,
	/// с по-типовым тестом свет-против-кластера (сфера для точечных, конус для спотов). Результат
	/// (ClusterCounts/ClusterIndices) читают пиксельные шейдеры батч-материалов.</summary>
	void ExecuteLightClustering(ICommandBuffer cmd);

	/// <summary>Заливает viewProj-матрицы теневых слайсов punctual-светов кадра (сэмплинг теней в
	/// пиксельном шейдере). Команда замороженная - память обязана быть стабильной
	/// (см. RenderCamerasData.punctualShadowMatrices).</summary>
	unsafe void SetupPunctualShadowMatrices(ICommandBuffer cmd, UnsafeArray* matrices);

	/// <summary>Пишет один слайс texture array теней punctual-светов по результату куллинга слайса:
	/// вызывающий сперва заливает SetupCullData фрустумом света и SetupLightData с CascadeMatrix0 =
	/// viewProj слайса (см. PunctualShadowScheduler), затем ExecuteComputeCulling - и этот дроу.</summary>
	void ExecuteDrawPunctualShadow(ICommandBuffer cmd, ICullResult cullResult, int sliceIndex);

	/// <summary>Переводит массив теней punctual-светов в состояние чтения из шейдера - зовётся перед
	/// камерными дроу ВСЕГДА, даже если слайсы не рисовались (текстура объявлена в PS безусловно,
	/// лейаут обязан быть валиден).</summary>
	void TransitionPunctualShadowsForRead(ICommandBuffer cmd);

	/// <summary>Dispatches the GPU culling compute shader for the currently bound view/cull data.</summary>
	ICullResult ExecuteComputeCulling(ICommandBuffer cmd, int cascadeIndex = -1);

	/// <summary>Renders the depth-only shadow map for the given cascade using a previous culling result.</summary>
	void ExecuteDrawShadows(ICommandBuffer cmd, ICullResult cullResult, int cascadeIndex);

	/// <summary>Renders the main color pass using a previous culling result.</summary>
	void ExecuteDrawBatching(ICommandBuffer cmd, ICullResult cullResult);

	/// <summary>Renders only the materials selected by <paramref name="filter"/> - see <see cref="BatchDrawFilter"/>.</summary>
	void ExecuteDrawBatching(ICommandBuffer cmd, ICullResult cullResult, BatchDrawFilter filter);

	/// <summary>Включает АЛЬФА-ТЕСТ при записи shadow map для материала - для «дырявой» геометрии
	/// (листва, ажурные решётки). Без этого теневой пасс пишет геометрию квада, а не то, что на нём
	/// нарисовано: крона отбрасывает монолитную кляксу, и сквозь неё не пробивается ни солнечное
	/// кружево на полу, ни лучи в объёмном свете (см. VolumetricLightPass).
	///
	/// Стоит дроу-колла на материал в каждом каскаде, поэтому звать ТОЛЬКО для реально дырявой
	/// геометрии: пометки glTF alphaMode=MASK недостаточно - экспортеры сплошь метят ею камень и
	/// ткань с альфой ~1 (см. критерий по средней альфе в ModelViewportGeometry и тот же самый в
	/// ProbeGi). Пока материал не помечен, он пишет тень по-старому, сплошным квадом.</summary>
	void SetMaterialAlphaTestedShadow(int materialId, DecaEngine.Graphics.ModelLoader.BaseColorBinding baseColor,
		float alphaCutoff);

	/// <summary>Убирает материал из кастеров тени СОВСЕМ (сырой id материала - <c>MaterialId.materialId</c>).
	/// По умолчанию тень отбрасывают все.
	///
	/// Нужно BLEND-накладкам: декали грязи и потёков (Intel Sponza) лежат в миллиметрах от стены,
	/// которую украшают, и любая их тень - хоть сплошным квадом, хоть вырезанная по альфе - падает на
	/// эту же стену и дублирует их собственный рисунок крупными тёмными кляксами. Альфа-тест здесь не
	/// лечит ничего: он лишь меняет форму кляксы на форму текстуры. Не путать с MASK (листва,
	/// решётки): у той тень настоящая и нужна, см. <see cref="SetMaterialAlphaTestedShadow"/>.</summary>
	void SetMaterialShadowCasting(int materialId, bool casts);

	/// <summary>Marks a registered material as transparent/transmissive for <see cref="BatchDrawFilter"/>
	/// purposes (raw material id - <c>MaterialId.materialId</c>). Materials default to opaque.</summary>
	void SetMaterialTransparent(int materialId, bool transparent);

	// Партиционное снятие ОДНОЙ модели (DiligentBatchRenderer.UnregisterModel) здесь НЕ объявлено:
	// оно оперирует BatchId/MaterialId/MeshId, а те живут в проекте Graphics.Diligent, на который
	// Graphics.Core не ссылается. Потребители (ModelStreamer) держат конкретный DiligentBatchRenderer
	// (см. ModelViewportEnvironment.BatchRenderer) и зовут метод напрямую.
}

