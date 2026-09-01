using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Engine.ImGui.Core;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;
using DecaEngine.Scene;
using DecaEngine.Animation;
using DecaEngine.Graphics;

namespace DecaEngine.Editor;

/// <summary>
/// Докуемое окно "Graphics" со ВСЕМИ настройками превью-графики в одном месте - для теххудожника:
/// свет и тени, эффекты кадра (AO, SSGI, туман, объёмный свет, блум, грейд, экспозиция), материалы
/// и детальные ручки probe-GI/неба (см. ProbeGi.cs). В отличие от модалки Settings (см.
/// <see cref="SettingsWindow"/>) применяет всё живьём: каждое изменение пишется в
/// <see cref="EditorSettings"/> и поднимает <see cref="SettingsWindow.PreviewGraphicsApplied"/> -
/// вьюпорт сам решает, что это (пуш кбуфера, перестройка конвейера или ребейк проб, см.
/// ModelPreviewViewport.OnGraphicsSettingsChanged/ApplyGraphicsSettings). Настройки сохраняются
/// на диск после отпускания контрола, не каждый тик драга слайдера.
///
/// Разбивка на секции - ПО СМЫСЛУ, с одним исключением: ручки, которые нельзя переставить на живом
/// окружении, вынесены в отдельную секцию "Перезагрузка" и применяются кнопкой (см. DrawApplyBar).
/// Держать их вперемешку с живыми нельзя - снаружи они неотличимы, а стоят на порядки дороже.
/// </summary>
public partial class GraphicsSettingsWindow : ImGuiDockingWindow
{
	private readonly EditorSettings _settings;
	private readonly ModelPreviewViewport _viewport;
	private readonly PrefabSceneViewport _sceneViewport;

	private bool _changed;
	private bool _savePending;

	// Однократная синхронизация VSync при первом кадре секции Display (см. DrawDisplaySection).
	private bool _vsyncSynced;

	// --- Буфер отложенных настроек (см. DrawApplyBar) --------------------------------------------
	//
	// Всё остальное окно применяется живьём, и это правильно: художник крутит ползунок и видит кадр.
	// Но четыре ручки запечены НЕ в конвейер, а в вещи, которые нельзя переставить на живом
	// окружении: HDRI требует пересчёта IBL, анизотропия и потолок
	// текстур - в сэмплерах и декодере уже залитых текстур. Любая из них пересоздаёт окружение и
	// перечитывает модель с диска - секунды на ассете уровня Sponza. Применяться на каждый клик по
	// комбо они не должны: пройтись по трём ручкам стоило трёх полных перезагрузок подряд, причём
	// две первых - впустую. Поэтому правятся они здесь, в буфере, и уезжают в EditorSettings разом.
	private bool _pendingAniso;
	private string _pendingHdr = "";
	private int _pendingMaxTextureSize;

	// Снимок настроек, из которого буфер набран. Нужен не для дифа (диф считается прямо против
	// EditorSettings), а чтобы заметить правку ТЕХ ЖЕ полей из модалки Settings: она пишет в
	// EditorSettings напрямую, и без пересинхронизации буфер молча вернул бы старые значения.
	private (bool Aniso, string Hdr, int MaxTextureSize) _pendingSource;

	// --- Состояние отладочного вида каскадов теней (см. DrawShadowCascadesDebug) ---
	private const int ShadowDebugSize = 512;
	private int _shadowDebugSource;
	private bool _shadowDebugRaw;
	private string _shadowDebugInfo = "";
	private float[][] _shadowDebugSlices;
	private (float Min, float Max, float Coverage)[] _shadowDebugStats;
	private (float WorldSize, float WorldDepthRange)[] _shadowDebugWorld;
	private IGpuTexture[] _shadowDebugTextures;
	private ImTextureRef[] _shadowDebugTexRefs;

	public GraphicsSettingsWindow(string name, EditorSettings settings, ModelPreviewViewport viewport,
		PrefabSceneViewport sceneViewport, ImGuiRender imGuiRender) : base(name, imGuiRender)
	{
		_settings = settings;
		_viewport = viewport;
		_sceneViewport = sceneViewport;
	}

	protected override void OnRender(uint dockId)
	{
		_changed = false;
		SyncPendingFromSettings(force: false);

		// Каждая секция - СВОЙ раскрывающийся заголовок, а не сплошной свиток: ручек здесь
		// на несколько экранов, и без сворачивания до нужной приходится крутить колесо мимо всех
		// остальных. Состояние раскрытия держит сам ImGui (по ID заголовка, в своём ini) - хранить
		// его в EditorSettings незачем.
		if (ImGui.CollapsingHeader("Display", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawDisplaySection();
		}

		if (ImGui.CollapsingHeader("Sun & Shadows", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawLightSection();
		}

		// Секция на ПАСС, а не свалка "Passes" с вложенными заголовками: у тумана, объёмного света,
		// блума и грейда свои секции с самого начала, а AO и SSGI сидели внутри общей - при том что
		// ручек у них больше, чем у блума. Вложенный CollapsingHeader внутри CollapsingHeader к тому
		// же не даёт свернуть соседа, не свернув родителя.
		if (ImGui.CollapsingHeader("Ambient occlusion", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawAoSection();
		}

		if (ImGui.CollapsingHeader("SSGI", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawSsgiSection();
		}

		if (ImGui.CollapsingHeader("Reflections (SSR)", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawSsrSection();
		}

		if (ImGui.CollapsingHeader("Fog"))
		{
			DrawFogSection();
		}

		if (ImGui.CollapsingHeader("Volumetric light"))
		{
			DrawVolumetricSection();
		}

		if (ImGui.CollapsingHeader("Bloom"))
		{
			DrawBloomSection();
		}

		if (ImGui.CollapsingHeader("Color grading"))
		{
			DrawColorGradeSection();
		}

		if (ImGui.CollapsingHeader("Exposure"))
		{
			DrawExposureSection();
		}

		if (ImGui.CollapsingHeader("Materials"))
		{
			DrawMaterialSection();
		}

		if (ImGui.CollapsingHeader("Sky / Probe GI", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawProbeGiSection();
		}

		if (ImGui.CollapsingHeader("Motion vectors"))
		{
			DrawMotionVectorSection();
		}

		if (ImGui.CollapsingHeader("Render graph (debug)"))
		{
			DrawRenderGraphSection();
		}

		// Последней и рядом с кнопкой, которая её и применяет: секция собрана не по смыслу ручек, а
		// по их ЦЕНЕ - это единственное, что их объединяет, и единственное, что о них надо знать.
		if (ImGui.CollapsingHeader("Перезагрузка (окружение и загрузка)"))
		{
			DrawReloadSection();
		}

		// Панель применения - ВНЕ раскрывающегося заголовка: свою секцию художник почти всегда держит
		// свёрнутой, и кнопка вместе со списком «что ждёт применения» пропала бы из виду ровно тогда,
		// когда о ней и надо напомнить.
		DrawApplyBar();

		if (_changed)
		{
			// Вьюпорт диффует сам: кбуферные ручки пушатся сразу, фичи конвейера (AO/SSGI/скай/туман/
			// объёмник/блум/грейд/экспозиция) перестраивают его на живом окружении, а ручки
			// перезагрузки (HDRI/анизотропия/потолок текстур) пересоздают окружение и
			// перечитывают модель - но они сюда попадают только через ApplyPending.
			SettingsWindow.RaisePreviewGraphicsApplied();
			_savePending = true;
		}

		// Сохранение на диск - после отпускания контрола: json-запись каждый тик драга слайдера
		// молотила бы диск впустую.
		if (_savePending && !ImGui.IsAnyItemActive())
		{
			_savePending = false;
			_settings.Save();
		}
	}

	// --- Секции --------------------------------------------------------------------------------

	/// <summary>Презентация кадра САМОГО ОКНА редактора, а не превью-конвейера: ручка живёт на
	/// главном <see cref="IGraphicsApi"/> (том, что зовёт Present) - окно достаёт его через
	/// окружение вьюпорта, это тот же экземпляр (см. EditorManager). Пока окружение не поднято,
	/// применять не к чему - секция ждёт. Переменная окружения DECA_VSYNC при старте старше
	/// сохранённой настройки: галка тогда синхронизируется с фактом, а не наоборот.</summary>
	private void DrawDisplaySection()
	{
		ImGui.Spacing();

		var api = _viewport?.Environment?.GraphicsApi ?? _sceneViewport?.Environment?.GraphicsApi;
		if (api == null)
		{
			ImGui.TextDisabled("Окружение ещё не создано.");
			return;
		}

		if (!_vsyncSynced)
		{
			_vsyncSynced = true;
			if (System.Environment.GetEnvironmentVariable("DECA_VSYNC") != null)
			{
				_settings.VSync = api.PresentInterval > 0;
			}
			else
			{
				api.PresentInterval = _settings.VSync ? 1 : 0;
			}
		}

		var vsync = _settings.VSync;
		if (ImGui.Checkbox("VSync", ref vsync))
		{
			_settings.VSync = vsync;
			api.PresentInterval = vsync ? 1 : 0;
			_changed = true;
		}
		Tooltip("Вертикальная синхронизация презента (IGraphicsApi.PresentInterval).\n" +
			"Выключение снимает кап фреймрейта - для замеров производительности;\n" +
			"кадры в полёте при этом по-прежнему ограничены фенсом (см. Present).\n" +
			"Применяется живьём; при старте перекрывается переменной DECA_VSYNC (1/0).");
	}

	/// <summary>Ручки ПЕРЕЗАГРУЗКИ - собраны по цене, а не по смыслу: это единственное, что у них
	/// общего, и единственное, что о них надо знать. Каждая запечена не в конвейер, а в вещи,
	/// которые нельзя переставить на живом окружении (PSO геометрии, IBL, сэмплеры и декодер уже
	/// залитых текстур), поэтому все они правятся в буфер и уезжают разом по кнопке внизу окна
	/// (см. DrawApplyBar). Метка "*" ставится каждой изменённой: иначе буфер - ловушка, значение в
	/// контроле уже новое, а кадр остаётся старым.</summary>
	private void DrawReloadSection()
	{
		ImGui.Spacing();
		ImGui.TextDisabled("Применяются кнопкой внизу окна: пересоздают окружение\n" +
			"и перечитывают модель с диска.");
		ImGui.Spacing();

		var aniso = _pendingAniso;
		if (ImGui.Checkbox("Anisotropic filtering", ref aniso))
		{
			_pendingAniso = aniso;
		}
		PendingMark(_pendingAniso != _settings.PreviewAnisotropicFiltering);

		var hdrBuffer = _pendingHdr;
		ImGui.SetNextItemWidth(240 * _scale);
		if (ImGui.InputText("Environment HDR", ref hdrBuffer, 512))
		{
			_pendingHdr = hdrBuffer;
		}
		PendingMark(_pendingHdr != (_settings.PreviewEnvironmentHdr ?? string.Empty));
		Tooltip("Equirect .hdr: абсолютный путь или относительно EditorAssets/.\nПусто - процедурное небо.\nПрименяется кнопкой внизу окна (пересоздаёт окружение и перепекает пробы).");

		// Потолок текстур - к рендеру напрямую не относится, но живёт здесь: он запечён в декодер
		// загрузчика, то есть меняется ровно тем же перечитыванием модели, что и остальные три.
		var sizes = new[] { 512, 1024, 2048, 4096 };
		var labels = new[] { "512", "1024", "2048", "4096" };
		int index = Array.IndexOf(sizes, _pendingMaxTextureSize);
		if (index < 0)
		{
			index = 2;
			_pendingMaxTextureSize = sizes[index];
		}

		ImGui.SetNextItemWidth(120 * _scale);
		if (ImGui.Combo("Потолок текстур", ref index, labels, labels.Length))
		{
			_pendingMaxTextureSize = sizes[index];
		}
		PendingMark(_pendingMaxTextureSize != _settings.PreviewMaxTextureSize);
		Tooltip("Максимальная сторона текстуры при загрузке модели.\n\n" +
			"Прямо задаёт ПИКОВУЮ память загрузки: загрузчик декодирует ВСЕ текстуры модели\n" +
			"разом и только потом заливает их на GPU, поэтому в памяти одновременно лежит\n" +
			"вся их несжатая RGBA-копия. При 2048 одна текстура - 16 МБ; на ассете уровня\n" +
			"Intel Sponza с сотнями текстур это гигабайты.\n\n" +
			"Каждый шаг вниз режет пик ВЧЕТВЕРО. 1024 на превью почти неотличим,\n" +
			"512 заметно мылит крупные планы.");

		DrawStreamingSettings();
	}

	/// <summary>
	/// Стриминг моделей сцены. Ручки ЖИВЫЕ: радиус стример перечитывает на каждом Tick, поэтому
	/// ни перечитывания моделей, ни пересоздания окружения не требуется - в отличие от потолка
	/// текстур выше, который печётся при загрузке.
	/// </summary>
	private void DrawStreamingSettings()
	{
		ImGui.Separator();
		ImGui.TextDisabled("Сцена");

		bool skinning = _settings.SceneSkinning;
		if (ImGui.Checkbox("GPU-скиннинг", ref skinning))
		{
			_settings.SceneSkinning = skinning;
			_changed = true;
		}
		Tooltip("Деформация скиннед-моделей на GPU.\n\n" +
			"Выключение НЕ убирает модель - она рисуется в bind-позе обычным статическим\n" +
			"путём: не заводятся ни отдельные инстансы в мега-буфере вершин, ни батчи под\n" +
			"них, ни compute-проход. Компоненты анимации при этом остаются в инспекторе.\n\n" +
			"Ручка ИНСТАНЦИРОВАНИЯ: скиннед-инстансы регистрируются при появлении модели\n" +
			"в сцене, поэтому переключение действует на следующие инстанцирования -\n" +
			"уже показанную модель нужно переоткрыть.\n\n" +
			"Переменная окружения DECA_SKINNING=0 сильнее этой галки: она нужна как\n" +
			"аварийный путь, когда редактор не доживает до этого окна.");

		bool streaming = _settings.SceneStreaming;
		if (ImGui.Checkbox("Стриминг по расстоянию", ref streaming))
		{
			_settings.SceneStreaming = streaming;
			_changed = true;
		}
		Tooltip("Модели сцены берутся и отпускаются по расстоянию до камеры.\n\n" +
			"Выключение НЕ отключает загрузку - оно делает все модели сцены постоянно\n" +
			"резидентными: радиус уходит в бесконечность, и никто ничего не отпускает.\n" +
			"Память при этом растёт на всю сцену разом.\n\n" +
			"Практическая польза выключения: стриминг - единственный путь в редакторе,\n" +
			"где набор мешей и батчей меняется ПО ХОДУ кадров, а не на первом. Тумблер\n" +
			"позволяет отделить проблемы сцены от проблем стриминга без пересборки.");

		if (!streaming)
		{
			return;
		}

		float radius = _settings.SceneStreamingRadius;
		ImGui.SetNextItemWidth(160 * _scale);
		if (ImGui.SliderFloat("Радиус стриминга", ref radius, 10f, 5000f, "%.0f"))
		{
			_settings.SceneStreamingRadius = MathF.Max(1f, radius);
			_changed = true;
		}
		Tooltip("Мировые единицы. Дальше радиуса модель отпускается, ближе - берётся.\n\n" +
			"У выгрузки есть запас (гистерезис x1.15): без него модель на самой границе\n" +
			"грузилась и выгружалась бы на каждом шаге камеры.\n\n" +
			"Слишком малый радиус даёт видимое подгружение вокруг камеры, слишком\n" +
			"большой обесценивает стриминг - сцена всё равно окажется в памяти целиком.");
	}

	/// <summary>Метка «отредактировано, но ещё не применено» справа от контрола. Без неё буфер
	/// отложенных ручек - ловушка: значение в комбо стои́т новое, кадр остаётся старым, и понять,
	/// это ждёт кнопки или просто не работает, снаружи нельзя.</summary>
	private static void PendingMark(bool pending)
	{
		if (!pending)
		{
			return;
		}

		ImGui.SameLine();
		ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f), "*");
	}

	private void DrawMaterialSection()
	{
		ImGui.Spacing();

		var normalMaps = _settings.PreviewNormalMaps;
		if (ImGui.Checkbox("Normal maps", ref normalMaps))
		{
			_settings.PreviewNormalMaps = normalMaps;
			_changed = true;
		}

		var bakedAo = _settings.PreviewBakedOcclusion;
		if (ImGui.Checkbox("Baked occlusion (occlusionTexture)", ref bakedAo))
		{
			_settings.PreviewBakedOcclusion = bakedAo;
			_changed = true;
		}
	}

	/// <summary>Набирает буфер отложенных ручек из настроек. force - после применения/отмены;
	/// без него пересинхронизация происходит только когда настройки изменил КТО-ТО ДРУГОЙ (модалка
	/// Settings пишет в те же поля): иначе каждый кадр затирал бы то, что человек сейчас правит.</summary>
	private void SyncPendingFromSettings(bool force)
	{
		var current = (_settings.PreviewAnisotropicFiltering,
			_settings.PreviewEnvironmentHdr ?? string.Empty, _settings.PreviewMaxTextureSize);

		// Первый кадр окна тоже попадает сюда: _pendingSource пустой и с настройками не совпадает.
		if (!force && current == _pendingSource)
		{
			return;
		}

		_pendingSource = current;
		_pendingAniso = current.Item1;
		_pendingHdr = current.Item2;
		_pendingMaxTextureSize = current.Item3;
	}

	/// <summary>Что в буфере разошлось с настройками, человеческим текстом «было -> стало». Пустой
	/// список = применять нечего.</summary>
	private List<string> CollectPendingChanges()
	{
		var changes = new List<string>();


		if (_pendingAniso != _settings.PreviewAnisotropicFiltering)
		{
			changes.Add($"Anisotropic filtering: {OnOff(_settings.PreviewAnisotropicFiltering)} -> {OnOff(_pendingAniso)}");
		}

		if (_pendingHdr != (_settings.PreviewEnvironmentHdr ?? string.Empty))
		{
			changes.Add($"Environment HDR: {HdrLabel(_settings.PreviewEnvironmentHdr)} -> {HdrLabel(_pendingHdr)}");
		}

		if (_pendingMaxTextureSize != _settings.PreviewMaxTextureSize)
		{
			changes.Add($"Потолок текстур: {_settings.PreviewMaxTextureSize} -> {_pendingMaxTextureSize}");
		}

		return changes;

		static string OnOff(bool value) => value ? "вкл" : "выкл";
		static string HdrLabel(string path) =>
			string.IsNullOrWhiteSpace(path) ? "процедурное небо" : Path.GetFileName(path.Trim());
	}

	/// <summary>Панель применения ручек перезагрузки - единственное место окна, где изменение НЕ
	/// уходит в кадр немедленно. Показывает не голую кнопку, а список того, что именно уедет:
	/// операция стоит пересоздания окружения и перечитывания модели с диска, и знать заранее, за
	/// что платишь, важнее, чем сэкономить три строки.</summary>
	private void DrawApplyBar()
	{
		ImGui.Spacing();
		ImGui.Separator();

		var changes = CollectPendingChanges();
		if (changes.Count == 0)
		{
			ImGui.TextDisabled("Изменений, требующих перезагрузки, нет.");
			return;
		}

		ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f), $"Ждут применения ({changes.Count}):");
		foreach (var change in changes)
		{
			ImGui.BulletText(change);
		}

		ImGui.Spacing();

		if (ImGui.Button("Применить", new Vector2(140 * _scale, 0)))
		{
			ApplyPending();
		}
		Tooltip("Записывает все накопленные ручки перезагрузки разом и пересоздаёт окружение:\n" +
			"модель перечитывается с диска, пробы перепекаются. Секунды на тяжёлом ассете -\n" +
			"поэтому ручки и копятся, а не применяются по одной.");

		ImGui.SameLine();
		if (ImGui.Button("Отменить", new Vector2(140 * _scale, 0)))
		{
			SyncPendingFromSettings(force: true);
		}
		Tooltip("Вернуть контролы к тому, что сейчас стои́т в движке.");
	}

	/// <summary>Переносит буфер в <see cref="EditorSettings"/> одним куском. Дальше - обычный путь
	/// окна: _changed поднимает PreviewGraphicsApplied, вьюпорты сами видят диф этих четырёх ручек
	/// и ставят отложенное пересоздание (см. ModelPreviewViewport.OnGraphicsSettingsChanged).</summary>
	private void ApplyPending()
	{
		_settings.PreviewAnisotropicFiltering = _pendingAniso;
		_settings.PreviewEnvironmentHdr = _pendingHdr;
		_settings.PreviewMaxTextureSize = _pendingMaxTextureSize;

		SyncPendingFromSettings(force: true);
		_changed = true;
	}

	// --- Хелперы -------------------------------------------------------------------------------

	/// <summary>Слайдер стандартной ширины; true при изменении значения (и взводит _changed).
	/// AlwaysClamp - чтобы ctrl+click-ввод не заводил в json значение вне диапазона: ползунок
	/// упирался бы в максимум, а движок применял введённое (см. историю с Ambient boost 121 при
	/// разметке до 12).</summary>
	private bool Slider(string label, ref float value, float min, float max, string format,
		ImGuiSliderFlags flags = ImGuiSliderFlags.AlwaysClamp)
	{
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.SliderFloat(label, ref value, min, max, format, flags))
		{
			_changed = true;
			return true;
		}

		return false;
	}

	/// <summary>Целочисленный близнец <see cref="Slider"/> - с той же шириной и тем же AlwaysClamp:
	/// без него ctrl+click-ввод заводил в json значение вне разметки (ползунок упирался бы в
	/// максимум, а движок применял введённое).</summary>
	private bool SliderInt(string label, ref int value, int min, int max)
	{
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.SliderInt(label, ref value, min, max, "%d", ImGuiSliderFlags.AlwaysClamp))
		{
			_changed = true;
			return true;
		}

		return false;
	}

	private static void Tooltip(string text)
	{
		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip(text);
		}
	}
}
