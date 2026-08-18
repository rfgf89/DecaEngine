using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using Engine.ImGui.Core;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;

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
public class GraphicsSettingsWindow : ImGuiDockingWindow
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
	// окружении: MSAA сидит в PSO всей геометрии, HDRI требует пересчёта IBL, анизотропия и потолок
	// текстур - в сэмплерах и декодере уже залитых текстур. Любая из них пересоздаёт окружение и
	// перечитывает модель с диска - секунды на ассете уровня Sponza. Применяться на каждый клик по
	// комбо они не должны: пройтись по трём ручкам стоило трёх полных перезагрузок подряд, причём
	// две первых - впустую. Поэтому правятся они здесь, в буфере, и уезжают в EditorSettings разом.
	private int _pendingMsaa;
	private bool _pendingAniso;
	private string _pendingHdr = "";
	private int _pendingMaxTextureSize;

	// Снимок настроек, из которого буфер набран. Нужен не для дифа (диф считается прямо против
	// EditorSettings), а чтобы заметить правку ТЕХ ЖЕ полей из модалки Settings: она пишет в
	// EditorSettings напрямую, и без пересинхронизации буфер молча вернул бы старые значения.
	private (int Msaa, bool Aniso, string Hdr, int MaxTextureSize) _pendingSource;

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
			// перезагрузки (MSAA/HDRI/анизотропия/потолок текстур) пересоздают окружение и
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

	private void DrawLightSection()
	{
		ImGui.Spacing();

		var shadows = _settings.PreviewShadows;
		if (ImGui.Checkbox("Shadows (world sun)", ref shadows))
		{
			_settings.PreviewShadows = shadows;
			_changed = true;
		}
		Tooltip("Тени мирового ключа (shadow map каскад). Выключение откатывает свет на камерный риг\nи прячет probe-GI (пробам нужно направление солнца).");

		// Верх диапазона = ровно тот кламп, с которым значение уходит в кбуфер и в бейк
		// (ModelPreviewViewport: Clamp(..., 0.1f, 16f)); прежние 1000 были фикцией - всё выше 16
		// движок молча срезал.
		var sun = _settings.ProbeGiSunIntensity;
		if (Slider("Sun intensity", ref sun, 0.1f, 16f, "%.2f"))
		{
			_settings.ProbeGiSunIntensity = sun;
		}
		Tooltip("Интенсивность солнца - и аналитического ключа, и баунса в пробах (перепекает их).\nВыше колена тонемапа (~0.76 на светлом альбедо) контраст съедается - крутить вместе с Ambient boost.");

		ImGui.Spacing();
		DrawShadowCascadesDebug();
	}

	/// <summary>Отладочный вид shadow map каскадов: по кнопке вычитывает D32-слайсы выбранного
	/// вьюпорта на CPU (синхронно, поэтому не live) и показывает их нормализованными ПО-КАСКАДНО -
	/// сырая глубина крупного каскада «вся белая» не потому, что буфер пуст, а потому что сцена
	/// занимает узкую полосу его Z-диапазона (far = 2 диаметра сферы каскада) и малую долю площади.</summary>
	private unsafe void DrawShadowCascadesDebug()
	{
		if (!ImGui.TreeNode("Shadow cascades (debug)"))
		{
			return;
		}

		var sourceLabels = new[] { "Scene View", "Model Preview" };
		ImGui.SetNextItemWidth(140 * _scale);
		ImGui.Combo("Source", ref _shadowDebugSource, sourceLabels, sourceLabels.Length);

		if (ImGui.Button("Capture", new Vector2(100 * _scale, 0)))
		{
			CaptureShadowCascades();
		}
		Tooltip("Синхронный ридбек всех слайсов shadow map выбранного вьюпорта (кадр встанет на\nмгновение). Слепок, не live - после смены камеры/света жми снова.");

		ImGui.SameLine();
		if (ImGui.Checkbox("Raw depth", ref _shadowDebugRaw))
		{
			RefreshShadowDebugTextures();
		}
		Tooltip("Глубина как в буфере (0..1 от near до far каскада) вместо растяжки по факт.\nдиапазону геометрии. У крупных каскадов сцена лежит в узкой полосе - картинка\nзакономерно почти белая, это не баг записи.");

		if (_shadowDebugInfo.Length > 0)
		{
			ImGui.TextDisabled(_shadowDebugInfo);
		}

		if (_shadowDebugSlices != null)
		{
			float imageSize = 220 * _scale;
			for (int i = 0; i < _shadowDebugSlices.Length; i++)
			{
				ImGui.Image(_shadowDebugTexRefs[i], new Vector2(imageSize, imageSize));
				ImGui.SameLine();
				ImGui.BeginGroup();
				ImGui.Text($"Cascade {i}");
				var stats = _shadowDebugStats[i];
				var world = _shadowDebugWorld[i];
				if (stats.Coverage <= 0f)
				{
					ImGui.TextDisabled("пусто (нет геометрии или каскад не рендерится)");
				}
				else
				{
					ImGui.Text($"геометрия: {stats.Coverage * 100f:F1}% текселей");
					ImGui.Text($"глубина: {stats.Min:F4} .. {stats.Max:F4}");
					if (world.WorldDepthRange > 0f)
					{
						ImGui.Text($"мир: {stats.Min * world.WorldDepthRange:F1} .. {stats.Max * world.WorldDepthRange:F1} ед. (диапазон {world.WorldDepthRange:F1})");
					}
				}
				if (world.WorldSize > 0f)
				{
					ImGui.Text($"область: {world.WorldSize:F1} x {world.WorldSize:F1} ед. " +
						$"(тексель {world.WorldSize / ShadowRenderer.ShadowMapSize:F3} ед.)");
				}
				ImGui.EndGroup();
				ImGui.Spacing();
			}
		}

		ImGui.TreePop();
	}

	private unsafe void CaptureShadowCascades()
	{
		var env = _shadowDebugSource == 0 ? _sceneViewport?.Environment : _viewport?.Environment;
		if (env?.BatchRenderer == null)
		{
			_shadowDebugInfo = "окружение ещё не создано";
			return;
		}

		var shadowTarget = env.BatchRenderer.WorldShadowRenderer?.ShadowMapsTarget as DiligentRenderTarget;
		if (shadowTarget == null)
		{
			_shadowDebugInfo = "shadow map недоступна";
			return;
		}

		var fullSlices = DiligentTextureReadback.ReadFloatSlices(env.DilApi, shadowTarget,
			out int width, out int height);
		int step = Math.Max(1, width / ShadowDebugSize);

		_shadowDebugSlices = new float[fullSlices.Length][];
		_shadowDebugStats = new (float, float, float)[fullSlices.Length];
		_shadowDebugWorld = new (float, float)[fullSlices.Length];

		for (int slice = 0; slice < fullSlices.Length; slice++)
		{
			var data = new float[ShadowDebugSize * ShadowDebugSize];
			float min = float.MaxValue, max = float.MinValue;
			long geomCount = 0;
			for (int y = 0; y < ShadowDebugSize; y++)
			{
				for (int x = 0; x < ShadowDebugSize; x++)
				{
					float v = fullSlices[slice][(y * step) * width + x * step];
					data[y * ShadowDebugSize + x] = v;
					if (v < 1.0f)
					{
						geomCount++;
						min = Math.Min(min, v);
						max = Math.Max(max, v);
					}
				}
			}

			_shadowDebugSlices[slice] = data;
			_shadowDebugStats[slice] = geomCount > 0
				? (min, max, (float)geomCount / data.Length)
				: (0f, 0f, 0f);
		}

		// Логические размеры каскадов: у Scene View (mainCascades) они лежат в CameraData
		// каскадных камер солнца - ортоширина в viewport.Z (см. CameraData ортоконструктор),
		// диапазон глубины far-near. У превью (Simple-путь) камер-каскадов нет - размеры
		// остаются нулями и строка "мир"/"область" не показывается.
		var sun = env.SunEntity;
		if (!sun.IsNull && sun.HasComponent<CascadedShadowComponent>())
		{
			ref var cascaded = ref sun.GetComponent<CascadedShadowComponent>();
			fixed (CameraComponent* ptr = &cascaded.Cascade0)
			{
				for (int i = 0; i < Math.Min(_shadowDebugWorld.Length, ShadowRenderer.MaxCascades); i++)
				{
					var camData = (ptr + i)->data;
					_shadowDebugWorld[i] = (camData.viewport.Z, Math.Abs(camData.far - camData.near));
				}
			}
		}

		_shadowDebugInfo = $"{sourceName(_shadowDebugSource)}: {width}x{height} x{fullSlices.Length}, даунсемпл {step}x";
		RefreshShadowDebugTextures();

		static string sourceName(int source) => source == 0 ? "Scene View" : "Model Preview";
	}

	/// <summary>Перезаливает RGBA8-текстуры вида из сохранённых float-слайсов (капчер или смена
	/// Raw depth). Текстуры создаются один раз - API переживает пересоздание окружений.</summary>
	private void RefreshShadowDebugTextures()
	{
		if (_shadowDebugSlices == null)
		{
			return;
		}

		var env = _shadowDebugSource == 0 ? _sceneViewport?.Environment : _viewport?.Environment;
		if (env == null)
		{
			return;
		}

		_shadowDebugTextures ??= new IGpuTexture[_shadowDebugSlices.Length];
		_shadowDebugTexRefs ??= new ImTextureRef[_shadowDebugSlices.Length];

		var pixels = new byte[ShadowDebugSize * ShadowDebugSize * 4];
		for (int slice = 0; slice < _shadowDebugSlices.Length; slice++)
		{
			var data = _shadowDebugSlices[slice];
			var stats = _shadowDebugStats[slice];
			float range = MathF.Max(stats.Max - stats.Min, 1e-6f);

			for (int i = 0; i < data.Length; i++)
			{
				float v = data[i];
				byte b;
				if (_shadowDebugRaw)
				{
					b = (byte)Math.Clamp((int)(v * 255f), 0, 255);
				}
				else
				{
					// Пустота (clear 1.0) остаётся белой, геометрия растягивается на 0..230 -
					// граница «есть геометрия / нет» читается при любом диапазоне глубин.
					b = v >= 1.0f ? (byte)255 : (byte)Math.Clamp((int)((v - stats.Min) / range * 230f), 0, 230);
				}

				int o = i * 4;
				pixels[o] = pixels[o + 1] = pixels[o + 2] = b;
				pixels[o + 3] = 255;
			}

			if (_shadowDebugTextures[slice] == null)
			{
				_shadowDebugTextures[slice] = env.DilApi.CreateTexture2DMutable(
					$"Shadow Cascade Debug {slice}", ShadowDebugSize, ShadowDebugSize);
				_shadowDebugTexRefs[slice] = _imGuiRender.GetNewTexture();
				_imGuiRender.BindRenderTarget(_shadowDebugTexRefs[slice].GetTexID(), _shadowDebugTextures[slice]);
			}

			env.DilApi.UpdateTexture2D(_shadowDebugTextures[slice], pixels);
		}
	}

	/// <summary>Экранное затенение (см. SsaoPass). Сам тумблер - фича конвейера: ресурсы пасса
	/// заводятся на живом окружении по SetFeatures, модель не перечитывается (раньше секция обещала
	/// обратное - обещание протухло вместе с переездом фич на живой конвейер).</summary>
	private void DrawAoSection()
	{
		ImGui.Spacing();

		var ssao = _settings.PreviewSsao;
		if (ImGui.Checkbox("Ambient occlusion (screen-space)", ref ssao))
		{
			_settings.PreviewSsao = ssao;
			_changed = true;
		}
		Tooltip("Затенение в стыках и нишах по глубине кадра. Применяется живьём.");

		if (ssao)
		{
			var aoModeLabels = new[] { "SSAO", "GTAO" };
			var aoModeIndex = _settings.PreviewAoMode == AmbientOcclusionMode.Gtao ? 1 : 0;
			ImGui.SetNextItemWidth(120 * _scale);
			if (ImGui.Combo("AO technique", ref aoModeIndex, aoModeLabels, aoModeLabels.Length))
			{
				_settings.PreviewAoMode = aoModeIndex == 1 ? AmbientOcclusionMode.Gtao : AmbientOcclusionMode.Ssao;
				_changed = true;
			}
			Tooltip("SSAO - классическое спиральное затемнение.\nGTAO - горизонты + интеграл видимости: чище на плоскостях, чуть дороже.");

			// Ниже - кбуфер AoConstants: пуш на кадре, без перестройки конвейера, поэтому и не
			// вызывают _changed = true (см. Slider).
			var aoStrength = _settings.AoStrength;
			if (Slider("AO strength", ref aoStrength, 0.25f, 4f, "%.2f"))
			{
				_settings.AoStrength = aoStrength;
			}
			Tooltip("Контраст затемнения (степень видимости у GTAO, множитель интенсивности у SSAO).");

			var aoFloor = _settings.AoFloor;
			if (Slider("AO floor", ref aoFloor, 0f, 0.5f, "%.2f"))
			{
				_settings.AoFloor = aoFloor;
			}
			Tooltip("Нижний предел видимости: экранный AO - косвенная оценка и не вправе гасить свет в ноль.\n0 = разрешить полное затемнение.");

			var aoRadiusWorld = _settings.AoRadiusWorld;
			if (Slider("AO radius (world)", ref aoRadiusWorld, 0f, 5f, "%.2f"))
			{
				_settings.AoRadiusWorld = aoRadiusWorld;
			}
			Tooltip("Радиус поиска в МИРОВЫХ единицах. 0 - считать от габаритов модели ручкой ниже.\nНа сцене-уровне (Sponza: радиус баундов ~50) доля от баундов даёт метры, и тонкая\nгеометрия - шторы, флаги, листва - вместо контактной тени кладёт широкое пятно.\nДля таких сцен ставь 0.2-0.5.");

			var aoRadius = _settings.AoRadiusFraction;
			if (Slider("AO radius (bounds)", ref aoRadius, 0.02f, 0.6f, "%.3f"))
			{
				_settings.AoRadiusFraction = aoRadius;
			}
			Tooltip("Радиус поиска в долях габаритного радиуса модели - для превью ОДНОГО объекта\n(масштаб-инвариантно). Игнорируется, когда задана ручка выше.\nБольше - тень тянется дальше от стыков (крупные ниши), мельче - только контактная.");

			var aoDebug = _settings.AoDebugView;
			if (ImGui.Checkbox("AO debug view", ref aoDebug))
			{
				_settings.AoDebugView = aoDebug;
				_changed = true;
			}
			Tooltip("Отладочный вид AO: композит выводит саму видимость в grayscale вместо затенения кадра\n(белое - открыто, чёрное - заслонено). Видно ровно то, чем AO-пасс глушит эмбиент,\nтак что ручки strength/floor/radius и разницу SSAO против GTAO можно сравнивать напрямую.\nПрозрачная геометрия рисуется ПОСЛЕ композита и поверх отладки остаётся обычной.");
		}
	}

	/// <summary>Экранный отскок света (см. SsgiPass). Как и AO - фича живого конвейера.</summary>
	private void DrawSsgiSection()
	{
		ImGui.Spacing();

		var ssgi = _settings.PreviewSsgi;
		if (ImGui.Checkbox("SSGI (screen-space bounce)", ref ssgi))
		{
			_settings.PreviewSsgi = ssgi;
			_changed = true;
		}
		Tooltip("Экранный отскок света из кадра (color bleeding). Дополняет probe-GI\nконтактным переносом цвета там, где сетка проб слишком редкая.\nПрименяется живьём.");

		if (ssgi)
		{
			var giIntensity = _settings.SsgiIntensity;
			if (Slider("GI intensity", ref giIntensity, 0f, 4f, "%.2f"))
			{
				_settings.SsgiIntensity = giIntensity;
			}
			Tooltip("Множитель собранного отскока. 0 - пасс считается, но ничего не подмешивает.");

			var giSamples = _settings.SsgiSamples;
			if (SliderInt("GI samples", ref giSamples, 4, SsgiPassResources.MaxSampleCount))
			{
				_settings.SsgiSamples = giSamples;
			}
			Tooltip("Тапов на пиксель - главный рычаг шум/цена. 8 и ниже дают тот самый цветной снег,\n16-24 с размытием ниже уже читаются как мягкий отскок.");

			var giMaxLum = _settings.SsgiMaxLuminance;
			if (Slider("GI firefly clamp", ref giMaxLum, 0f, 32f, "%.2f",
				ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
			{
				_settings.SsgiMaxLuminance = giMaxLum;
			}
			Tooltip("Потолок яркости ОДНОГО тапа. В HDR-кадре солнечное пятно рядом с тенью светит\nв десятки единиц, и один такой тап из выборки - это белая/цветная точка-искра.\nНиже - чище и тусклее в контрастных местах; 0 - снять ограничение.");

			var giSaturation = _settings.SsgiSaturation;
			if (Slider("GI saturation", ref giSaturation, 0f, 1f, "%.2f"))
			{
				_settings.SsgiSaturation = giSaturation;
			}
			Tooltip("Насыщенность отскока: 1 - цвет отправителя как есть, 0 - серый bounce.\nАналог Bounce saturation у probe-GI: цветные ткани иначе светят как неон.");

			var giBlur = _settings.SsgiBlurRadius;
			if (SliderInt("GI blur radius", ref giBlur, 0, SsgiPassResources.MaxBlurRadius))
			{
				_settings.SsgiBlurRadius = giBlur;
			}
			Tooltip("Радиус билатерального (по глубине) размытия отскока в композите, пикселей.\nШире - глаже и дороже; силуэты не размазывает - вес режется по разрыву глубины.");

			var giRadiusWorld = _settings.SsgiRadiusWorld;
			if (Slider("GI radius (world)", ref giRadiusWorld, 0f, 20f, "%.2f"))
			{
				_settings.SsgiRadiusWorld = giRadiusWorld;
			}
			Tooltip("Радиус сбора в МИРОВЫХ единицах. 0 - считать от габаритов модели ручкой ниже.\nНа сцене-уровне (Sponza) доля от баундов даёт метры: отскок собирается с половины\nэкрана и вырождается в цветную дымку - ставь 1-3.");

			var giRadiusFraction = _settings.SsgiRadiusFraction;
			if (Slider("GI radius (bounds)", ref giRadiusFraction, 0.02f, 2f, "%.3f"))
			{
				_settings.SsgiRadiusFraction = giRadiusFraction;
			}
			Tooltip("Радиус сбора в долях габаритного радиуса модели - для превью ОДНОГО объекта\n(масштаб-инвариантно). Игнорируется, когда задана ручка выше.");

			var giDebug = _settings.SsgiDebugView;
			if (ImGui.Checkbox("GI debug view", ref giDebug))
			{
				_settings.SsgiDebugView = giDebug;
				_changed = true;
			}
			Tooltip("Отладочный вид SSGI: композит выводит ОДИН отскок вместо кадра с ним -\nвидно ровно то, что пасс подмешивает, и как на это влияют ручки выше.");
		}
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

		// Значение вне списка (правка json руками, старый файл настроек) - НЕ просто показать первый
		// пункт: комбо тогда рисует "Off", а движок продолжает работать с прежним сэмплингом, и окно
		// расходится с кадром. Индекс чиним вместе с самой настройкой.
		int[] msaaOptions = [1, 2, 4, 8];
		var msaaLabels = new[] { "Off", "2x", "4x", "8x" };
		var msaaIndex = Array.IndexOf(msaaOptions, _pendingMsaa);
		if (msaaIndex < 0)
		{
			msaaIndex = Array.IndexOf(msaaOptions, 4);
			_pendingMsaa = msaaOptions[msaaIndex];
		}

		ImGui.SetNextItemWidth(120 * _scale);
		if (ImGui.Combo("MSAA", ref msaaIndex, msaaLabels, msaaLabels.Length))
		{
			_pendingMsaa = msaaOptions[msaaIndex];
		}
		PendingMark(_pendingMsaa != _settings.PreviewMsaaSamples);

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

	/// <summary>Экранные векторы движения (см. MotionVectorPass) - подготовка к апскейлерам DLSS/FSR,
	/// а не эффект: буфер заполняется, но кадр не меняет, пока его никто не читает.
	///
	/// Ступень 1 восстанавливает векторы ИЗ ГЛУБИНЫ, поэтому в них движение одной лишь камеры: всё,
	/// что двигалось само, для временнЫх техник выглядит неподвижным. Секция об этом честно
	/// предупреждает - иначе отладочный вид читался бы как поломка.</summary>
	private void DrawMotionVectorSection()
	{
		ImGui.Spacing();

		// Метка НЕ совпадает с заголовком секции ("Motion vectors"): одинаковые метки в одном
		// ID-стеке ImGui дают коллизию ID, и клики по чекбоксу дерутся с заголовком - галка
		// "не работает". Тот же приём, что у SSGI: заголовок короткий, чекбокс - с уточнением.
		var motion = _settings.PreviewMotionVectors;
		if (ImGui.Checkbox("Motion vectors (вход апскейлеров)", ref motion))
		{
			_settings.PreviewMotionVectors = motion;
			_changed = true;
		}
		Tooltip("Экранные векторы движения в отдельный RG16F-буфер - вход апскейлеров (DLSS/FSR) и TAA.\n" +
			"Сам кадр не меняет: буфер пока никто не читает, галка стоит одного фуллскрин-дроу.\n" +
			"Применяется живьём.");

		// MSAA гасит фичу молча - на уровне конвейера ресурсы просто не создаются (см.
		// PipelineFeatures.MotionVectors). Молчание тут недопустимо: галка стояла бы включённой, а
		// отладочный вид не показывал бы ничего, и искать причину пришлось бы в математике.
		if (motion && _settings.PreviewMsaaSamples > 1)
		{
			ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f),
				"MSAA " + _settings.PreviewMsaaSamples + "x: векторы отключены.");
			Tooltip("Векторы читают одно-сэмпловый depth, а при MSAA геометрия пишет в мультисемпловый,\n" +
				"и одиночный остаётся неразрешённым. Ставь MSAA в 1 - апскейлеры с мультисемплингом\n" +
				"и так взаимоисключающи, они сами себе антиалиасинг.");
		}

		// Масштаб и джиттер - ДО раннего выхода: они не зависят от галки векторов и обязаны
		// оставаться доступными, когда векторы выключены.
		var renderScale = _settings.RenderScale;
		if (Slider("Render scale", ref renderScale, 0.25f, 1f, "%.2f"))
		{
			_settings.RenderScale = renderScale;
			_changed = true;
		}
		Tooltip("Сцена и её пост-обработка рисуются в этой доле разрешения окна, до полного кадр\n" +
			"поднимает тонемап (пока билинейно - сюда встанет апскейлер). 1 = выключено.\n" +
			"Применяется живьём; на пол-разрешении сцена стоит ~четверть полной цены.");

		var jitter = _settings.TemporalJitter;
		if (ImGui.Checkbox("Temporal jitter", ref jitter))
		{
			_settings.TemporalJitter = jitter;
			_changed = true;
		}
		Tooltip("Суб-пиксельный джиттер проекции (Halton 2/3, 16 фаз) - вторая половина входа\n" +
			"темпорального апскейлера. Применяется живьём, с MSAA не конфликтует.\n" +
			"БЕЗ потребителя (TAA/апскейлера) картинка дрожит - это сырой вход техники, которой\n" +
			"ещё нет, а не баг. Отладочный вид векторов при этом ОБЯЗАН оставаться ровно серым:\n" +
			"векторы считаются по матрицам без джиттера, дрожание вычитает сам апскейлер.");

		if (!motion)
		{
			return;
		}

		var taau = _settings.TemporalUpscale;
		if (ImGui.Checkbox("Temporal upscale", ref taau))
		{
			_settings.TemporalUpscale = taau;
			_changed = true;
		}
		Tooltip("Темпоральный апскейл: сцена в рендер-разрешении (см. Render scale) + джиттер +\n" +
			"векторы собираются в полный кадр аккумуляцией истории. Джиттер включает сам.\n" +
			"На Render scale 1 работает как обычное TAA (сглаживание без потери разрешения).");

		if (taau)
		{
			var backend = _settings.UpscalerBackend;
			ImGui.SetNextItemWidth(220);
			if (ImGui.Combo("Upscaler backend", ref backend, "TAAU (встроенный)\0FSR (нативный)\0DLSS (нативный)\0"))
			{
				_settings.UpscalerBackend = backend;
				_changed = true;
			}
			Tooltip("TAAU - управляемый референс-бэкенд (шейдер движка). FSR - нативный ffx-api\n" +
				"(нужны DecaFfxShim.dll + amd_fidelityfx_upscaler_dx12.dll). DLSS - NVIDIA NGX\n" +
				"(нужны DecaFfxShim.dll + nvngx_dlss.dll и видеокарта RTX). Оба - только D3D12.\n" +
				"Применяется живьём; при недоступности молча остаётся TAAU.");

			// Честность: выбран нативный бэкенд, а работает TAAU - молчание тут заставило бы искать
			// разницу в картинке, которой нет (тот же принцип, что у предупреждения про MSAA выше).
			var activeName = _viewport?.Environment?.ActiveUpscalerName;
			if (backend != 0 && activeName is null)
			{
				ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f),
					(backend == 2 ? "DLSS" : "FSR") + " не активен - работает TAAU.");
				Tooltip("Нет DecaFfxShim.dll/нативной DLL рядом с экзешником, бэкенд не D3D12,\n" +
					"не то железо (DLSS - только NVIDIA RTX), или буфер векторов не создан (MSAA).\n" +
					"Подробности - в консоли ([fsr]/[dlss] ...).");
			}
			else if (activeName is not null)
			{
				// Версия самой библиотеки: у FSR - активный провайдер ffx-рантайма (запрошен у
				// живого контекста), у DLSS - файловая версия nvngx_dlss.dll (= версия SDK-релиза).
				ImGui.TextDisabled($"Активен: {activeName}");
			}

			// Настройки САМОГО бэкенда - под тем, к кому относятся: чужие ручки в чужом режиме
			// только сбивали бы с толку.
			switch (backend)
			{
				case 0:
					var alpha = _settings.TaauBlendAlpha;
					if (Slider("TAAU: вес кадра", ref alpha, 0.02f, 0.5f, "%.2f"))
					{
						_settings.TaauBlendAlpha = alpha;
					}
					Tooltip("Вес текущего кадра в аккумуляторе истории. Меньше - стабильнее и резче\n" +
						"на статике, но дольше сходится и заметнее шлейфы; больше - отзывчивее к\n" +
						"движению, но дрожание джиттера глушится слабее. Классика TAA - 0.10.");
					break;

				case 1:
					var provider = _settings.FsrProvider;
					ImGui.SetNextItemWidth(220);
					if (ImGui.Combo("FSR: провайдер", ref provider, "Авто (новейший рабочий)\0FSR 2\0FSR 3.1\0"))
					{
						_settings.FsrProvider = provider;
						_changed = true;
					}
					Tooltip("Ветка провайдера ffx-рантайма. Авто берёт новейшее РАБОЧЕЕ поколение под\n" +
						"текущее железо (FSR 4 на RDNA4, иначе FSR 2). FSR 3.1 оставлен для проверки\n" +
						"глазами. Смена пересоздаёт контекст - мгновенно, история рвётся на кадр.");

					if (provider == 2)
					{
						ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f),
							"FSR 3.1 на текущем SDK деградирует кадр (известная проблема).");
						Tooltip("Провайдер 3.1.5 из SDK 2.3.0 на этой интеграции сводит кадр в размытую\n" +
							"кашу; все документированные параметры перепробованы, в официальном сэмпле\n" +
							"AMD он работает - подозрение на чтение протухшего дескриптора из кучи\n" +
							"приложения. Подробности - в логе консоли.");
					}

					var sharp = _settings.FsrSharpness;
					if (Slider("FSR: sharpness", ref sharp, 0f, 1f, "%.2f"))
					{
						_settings.FsrSharpness = sharp;
					}
					Tooltip("Встроенный шарпен FSR (RCAS) поверх апскейла: 0 - выключен, 1 - максимум.\n" +
						"Применяется живьём, бесплатной резкости не бывает - на шумной сцене\n" +
						"перешарп подчёркивает и шум.");
					break;

				case 2:
					var quality = _settings.DlssQuality;
					ImGui.SetNextItemWidth(220);
					if (ImGui.Combo("DLSS: качество", ref quality, "Performance\0Balanced\0Quality\0DLAA\0"))
					{
						_settings.DlssQuality = quality;
						_changed = true;
					}
					Tooltip("Пресет внутренней обработки DLSS (модель/веса). Разрешение рендера им НЕ\n" +
						"управляется - его задаёт Render scale выше; DLAA осмыслен при Render scale 1\n" +
						"(чистое сглаживание без апскейла). Смена пересоздаёт фичу - мгновенно.");
					break;
			}
		}

		var debug = _settings.MotionVectorDebugView;
		if (ImGui.Checkbox("Motion vector debug view", ref debug))
		{
			_settings.MotionVectorDebugView = debug;
			_changed = true;
		}
		Tooltip("Замещает кадр раскраской векторов: R - смещение по X, G - по Y, РОВНЫЙ СЕРЫЙ - ноль.\n" +
			"Главная проверка: на неподвижной камере кадр обязан быть равномерно серым при любом\n" +
			"диапазоне. Ядовито-жёлтые пятна - вектор ушёл за диапазон, подними ползунок ниже.\n" +
			"Движущиеся объекты в ступени 1 остаются серыми - векторов у них ещё нет, это не баг.");

		if (debug)
		{
			var range = _settings.MotionVectorDebugRange;
			if (Slider("Debug range (px)", ref range, 1f, 64f, "%.1f"))
			{
				_settings.MotionVectorDebugRange = range;
			}
			Tooltip("Смещение в ПИКСЕЛЯХ, на котором шкала упирается в край. Мельче - видно дрожание\n" +
				"на медленном движении камеры, крупнее - не забивается на быстрых разворотах.");
		}
	}

	/// <summary>Тумблеры отдельных пассов рендер-графа - инструмент отладки и профилирования.
	///
	/// Выключение НЕ пересобирает граф: команды пасса остаются записанными, просто не проигрываются
	/// (см. IRenderGraphPass.Enabled), поэтому переключать можно хоть каждый кадр и мерить разницу.
	/// Список берётся из живого графа, а не хардкодится - он зависит от включённых фич конвейера.</summary>
	private void DrawRenderGraphSection()
	{
		ImGui.Spacing();
		ImGui.TextDisabled("Отладочный список: вырубить ЛЮБОЙ пасс на живом графе и посмотреть, что он даёт.\n" +
			"Фичи конвейера переключайте выше - там снимается ещё и подготовка их ресурсов.");
		ImGui.Spacing();

		var pipeline = _viewport.Environment?.Pipeline;
		if (pipeline == null)
		{
			ImGui.TextDisabled("Конвейер превью ещё не поднят.");
			return;
		}

		var names = pipeline.PassNames;
		if (names.Count == 0)
		{
			ImGui.TextDisabled("Граф пуст (кадр ещё не собирался).");
			return;
		}

		for (int i = 0; i < names.Count; i++)
		{
			var name = names[i];
			var enabled = pipeline.IsPassEnabled(name);

			// ID по ИНДЕКСУ: имена пассов не обязаны быть уникальными (два оверлея, каскады),
			// а одинаковый ImGui-id склеил бы их чекбоксы в один.
			if (ImGui.Checkbox($"{name}##pass{i}", ref enabled))
			{
				pipeline.SetPassEnabled(name, enabled);
				_sceneViewport?.Environment?.Pipeline?.SetPassEnabled(name, enabled);
			}
		}

		ImGui.Spacing();
		ImGui.TextDisabled("Выключенный пасс исключается из графа целиком, вместе со своими переходами\n" +
			"состояний ресурсов, поэтому выключать можно любой - включая структурные\n" +
			"(Forward, Tonemap): кадр просто останется неотрисованным, но не сломается.\n" +
			"Галка ПЕРЕВЕШИВАЕТ настройки выше и переживает пересборку графа: пасс,\n" +
			"снятый здесь, не вернётся от включения своей фичи - верните его тут же.");
	}

	/// <summary>Цветокоррекция и виньетка (см. ColorGradePass). Единственное место, где художник
	/// вообще может править палитру кадра: до этого пасса в движке не было ни насыщенности, ни
	/// баланса белого, ни тонировки - только то, что зашито в текстуры.</summary>
	private void DrawColorGradeSection()
	{
		ImGui.Spacing();

		var grade = _settings.PreviewColorGrade;
		if (ImGui.Checkbox("Цветокоррекция", ref grade))
		{
			_settings.PreviewColorGrade = grade;
			_changed = true;
		}
		Tooltip("Финальный пасс по готовому кадру: насыщенность, контраст, баланс белого,\nтонировка теней и светов, виньетка.\nС дефолтными значениями кадр НЕ меняется - коррекцию набираешь сам.\nРаботает и в HDR, и в LDR.");

		if (!grade)
		{
			return;
		}

		ImGui.Spacing();
		ImGui.TextDisabled("Тон (live):");

		var saturation = _settings.GradeSaturation;
		if (Slider("Насыщенность", ref saturation, 0f, 2f, "%.2f"))
		{
			_settings.GradeSaturation = saturation;
		}
		Tooltip("1 - как есть, 0 - серый кадр, выше 1 - ярче цвета.\nГлавная ручка против перенасыщенных материалов: единый приглушённый\nконверт с одним-двумя акцентами читается дороже, чем десяток чистых цветов.");

		var contrast = _settings.GradeContrast;
		if (Slider("Контраст", ref contrast, 0f, 2f, "%.2f"))
		{
			_settings.GradeContrast = contrast;
		}
		Tooltip("Разведение тёмного и светлого вокруг среднего тона. 1 - без изменений.");

		var gamma = _settings.GradeGamma;
		if (Slider("Гамма", ref gamma, 0.2f, 3f, "%.2f"))
		{
			_settings.GradeGamma = gamma;
		}
		Tooltip("Средние тона: больше - светлее середина при тех же чёрном и белом.");

		var temperature = _settings.GradeTemperature;
		if (Slider("Температура", ref temperature, -1f, 1f, "%.2f"))
		{
			_settings.GradeTemperature = temperature;
		}
		Tooltip("Минус - холоднее (в синеву), плюс - теплее (в янтарь).\nНормирована по яркости: экспозицию не трогает, компенсировать не придётся.");

		var tint = _settings.GradeTint;
		if (Slider("Оттенок", ref tint, -1f, 1f, "%.2f"))
		{
			_settings.GradeTint = tint;
		}
		Tooltip("Минус - в зелёный, плюс - в пурпурный. Вторая ось баланса белого.");

		ImGui.Spacing();
		ImGui.TextDisabled("Тонировка (live):");

		var shadows = new Vector3(_settings.GradeShadowR, _settings.GradeShadowG, _settings.GradeShadowB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Тени", ref shadows))
		{
			_settings.GradeShadowR = shadows.X;
			_settings.GradeShadowG = shadows.Y;
			_settings.GradeShadowB = shadows.Z;
			_changed = true;
		}
		Tooltip("АДДИТИВНАЯ тонировка: поднимает именно чёрное, светов не касается.\nНейтраль - чёрный. Классический приём: увести тени в холодное.");

		var highlights = new Vector3(_settings.GradeHighlightR, _settings.GradeHighlightG, _settings.GradeHighlightB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Света", ref highlights))
		{
			_settings.GradeHighlightR = highlights.X;
			_settings.GradeHighlightG = highlights.Y;
			_settings.GradeHighlightB = highlights.Z;
			_changed = true;
		}
		Tooltip("МУЛЬТИПЛИКАТИВНАЯ тонировка: красит светлое, чёрное остаётся чёрным.\nНейтраль - белый. В паре с холодными тенями даёт тёплый ключ.");

		ImGui.Spacing();
		ImGui.TextDisabled("Виньетка (live):");

		var vignette = _settings.VignetteIntensity;
		if (Slider("Сила", ref vignette, 0f, 1f, "%.2f"))
		{
			_settings.VignetteIntensity = vignette;
		}
		Tooltip("Притемнение к краям кадра. 0 - выключена.\nЧасть того, почему кадр читается как СКОМПОНОВАННЫЙ, а не как скриншот.");

		var vignetteRadius = _settings.VignetteRadius;
		if (Slider("Радиус", ref vignetteRadius, 0.1f, 1.5f, "%.2f"))
		{
			_settings.VignetteRadius = vignetteRadius;
		}
		Tooltip("Размер чистой зоны в центре. Больше - виньетка отступает к краям.");

		var vignetteSmooth = _settings.VignetteSmoothness;
		if (Slider("Мягкость", ref vignetteSmooth, 0.01f, 1f, "%.2f"))
		{
			_settings.VignetteSmoothness = vignetteSmooth;
		}
		Tooltip("Ширина перехода. Малые значения дают видимое кольцо - обычно нужно мягко.");

		var vignetteRound = _settings.VignetteRoundness;
		if (Slider("Круглость", ref vignetteRound, 0f, 1f, "%.2f"))
		{
			_settings.VignetteRoundness = vignetteRound;
		}
		Tooltip("1 - круг с поправкой на формат кадра, 0 - овал, растянутый по всему кадру.");
	}

	/// <summary>Блум - оптическое рассеяние (см. BloomPass). Сама галка уровня окружения (пасс
	/// владеет своей цепочкой таргетов), всё остальное живое.</summary>
	private void DrawBloomSection()
	{
		ImGui.Spacing();

		var bloom = _settings.PreviewBloom;
		if (ImGui.Checkbox("Блум", ref bloom))
		{
			_settings.PreviewBloom = bloom;
			_changed = true;
		}
		Tooltip("Свечение вокруг ярких мест. Не «делает ярче» - делает источник ЧИТАЕМЫМ КАК СВЕТ:\nдисплей не способен показать лампу ярче белой бумаги, и разницу между ними\nглазу передаёт именно рассеяние в оптике.");

		if (!bloom)
		{
			return;
		}

		ImGui.Spacing();

		var threshold = _settings.BloomThreshold;
		if (Slider("Порог", ref threshold, 0f, 4f, "%.2f"))
		{
			_settings.BloomThreshold = threshold;
		}
		Tooltip("Яркость, выше которой начинается свечение, в ОТОБРАЖАЕМЫХ единицах.\n1.0 - светятся только настоящие пересветы (то, что дисплей уже не покажет ярче).\nПривязан к авто-экспозиции, поэтому не зависит от абсолютной яркости сцены.\nНиже 1.0 - светиться начинает и то, что пересветом не является.");

		var knee = _settings.BloomKnee;
		if (Slider("Мягкость порога", ref knee, 0.0001f, 1f, "%.3f"))
		{
			_settings.BloomKnee = knee;
		}
		Tooltip("Ширина плавного перехода вокруг порога.\nБез него на градиенте видна ступенька: поверхность светлеет, и ровно на пороге\nу неё вдруг включается ореол.");

		var radius = _settings.BloomRadius;
		if (Slider("Радиус", ref radius, 0f, 4f, "%.2f"))
		{
			_settings.BloomRadius = radius;
		}
		Tooltip("Ширина тента при сборке цепочки вверх.\nБольше - мягче и дальше растекается ореол; 0 - только билинейная выборка,\nи между уровнями видны кольца.");

		var intensity = _settings.BloomIntensity;
		if (Slider("Интенсивность", ref intensity, 0f, 3f, "%.2f"))
		{
			_settings.BloomIntensity = intensity;
		}
		Tooltip("Сколько ореола подмешивать в кадр. Нормирована на число звеньев цепочки,\nпоэтому не скачет при смене разрешения вьюпорта.");
	}

	/// <summary>Атмосферный туман - воздушная перспектива (см. FogPass). Сама галка - уровня
	/// окружения (пассу нужны депт и scene-copy), всё остальное живое.</summary>
	private void DrawFogSection()
	{
		ImGui.Spacing();

		var fog = _settings.PreviewFog;
		if (ImGui.Checkbox("Атмосферный туман", ref fog))
		{
			_settings.PreviewFog = fog;
			_changed = true;
		}
		Tooltip("Воздушная перспектива: дальние планы теряют контраст и уходят в дымку.\nГлавный источник ощущения ГЛУБИНЫ в кадре - никакая GI его не заменяет.");

		if (!fog)
		{
			return;
		}

		ImGui.Spacing();

		// Логарифмический: рабочая зона плотности - тысячные доли, и на линейной шкале она
		// занимала бы пару пикселей у левого края (та же причина, что у Ambient boost).
		var density = _settings.FogDensity;
		if (Slider("Плотность", ref density, 0.0002f, 0.5f, "%.4f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.FogDensity = density;
		}
		Tooltip("Главная ручка. Плотность на опорной высоте, 1/единица мира.\nМасштабно-зависима: сцене в десятки единиц нужны сотые, отдельной модели - десятые.");

		var heightFalloff = _settings.FogHeightFalloff;
		if (Slider("Спад по высоте", ref heightFalloff, 0f, 1f, "%.3f"))
		{
			_settings.FogHeightFalloff = heightFalloff;
		}
		Tooltip("Как быстро дымка редеет вверх.\n0 - однородный туман без высотного профиля;\nбольше - низкая стелющаяся пелена, из которой торчат верхушки геометрии.");

		var heightRef = _settings.FogHeightRef;
		if (Slider("Опорная высота", ref heightRef, -50f, 50f, "%.1f"))
		{
			_settings.FogHeightRef = heightRef;
		}
		Tooltip("Высота (Y мира), на которой плотность равна заданной выше.\nОбычно - уровень пола сцены.");

		var start = _settings.FogStartDistance;
		if (Slider("Ближняя отсечка", ref start, 0f, 50f, "%.1f"))
		{
			_settings.FogStartDistance = start;
		}
		Tooltip("Дистанция, до которой тумана нет вовсе.\nБез неё дымка садится на самые ближние предметы и мылит их.");

		var maxDistance = _settings.FogMaxDistance;
		if (Slider("Предельная дальность", ref maxDistance, 10f, 5000f, "%.0f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.FogMaxDistance = maxDistance;
		}
		Tooltip("Потолок дальности. Её же получает НЕБО - у фона глубины нет,\nи без этого горизонт остался бы единственным местом без дымки.");

		var maxOpacity = _settings.FogMaxOpacity;
		if (Slider("Потолок плотности", ref maxOpacity, 0f, 1f, "%.2f"))
		{
			_settings.FogMaxOpacity = maxOpacity;
		}
		Tooltip("Сколько дымка вправе закрыть дальний план.\n1 - полностью, меньше - сквозь туман всегда что-то видно.");

		ImGui.Spacing();
		ImGui.TextDisabled("Цвет:");

		var color = new Vector3(_settings.FogColorR, _settings.FogColorG, _settings.FogColorB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Цвет среды", ref color))
		{
			_settings.FogColorR = color.X;
			_settings.FogColorG = color.Y;
			_settings.FogColorB = color.Z;
			_changed = true;
		}
		Tooltip("Тень дымки - то, во что уходит дальний план ВНЕ стороны солнца.\nСизый/голубоватый читается как даль, тёплый - как пыль или смог.");

		var sunColor = new Vector3(_settings.FogSunColorR, _settings.FogSunColorG, _settings.FogSunColorB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Цвет подсветки", ref sunColor))
		{
			_settings.FogSunColorR = sunColor.X;
			_settings.FogSunColorG = sunColor.Y;
			_settings.FogSunColorB = sunColor.Z;
			_changed = true;
		}
		Tooltip("Цвет дымки в сторону солнца. Обычно теплее и ярче цвета среды.");

		var sunStrength = _settings.FogSunStrength;
		if (Slider("Сила подсветки", ref sunStrength, 0f, 1f, "%.2f"))
		{
			_settings.FogSunStrength = sunStrength;
		}
		Tooltip("Ради этого туман и ставят: дымка перестаёт быть серой пеленой\nи начинает светиться со стороны источника. 0 - одноцветный туман.");

		var sunSharpness = _settings.FogSunSharpness;
		if (Slider("Резкость пятна", ref sunSharpness, 1f, 64f, "%.1f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.FogSunSharpness = sunSharpness;
		}
		Tooltip("Малые значения - широкое мягкое свечение на полнеба,\nбольшие - компактный ореол вокруг диска.");
	}

	/// <summary>Объёмный свет - god rays и объёмный туман (см. VolumetricLightPass). Сама галка -
	/// уровня окружения (пассу нужны депт, scene-copy и shadow map), всё остальное живое.</summary>
	private void DrawVolumetricSection()
	{
		ImGui.Spacing();

		var volumetric = _settings.PreviewVolumetric;
		if (ImGui.Checkbox("Объёмный свет", ref volumetric))
		{
			_settings.PreviewVolumetric = volumetric;
			_changed = true;
		}
		Tooltip("Световые столбы (god rays) и светящийся объёмный туман.\n" +
			"Рейкмарш вдоль луча с выборкой каскадных теней в каждой точке -\n" +
			"поэтому столбы точно повторяют геометрию, отбрасывающую тень.\n" +
			"Не заменяет атмосферный туман и не конфликтует с ним: тот отвечает\n" +
			"за дальнюю дымку, этот - за рассеянный свет.");

		if (!volumetric)
		{
			return;
		}

		// Тени - не опция этой секции, а условие существования эффекта: без них выборка идёт по
		// неинициализированному shadow map, и сила тени принудительно занулена на CPU (см.
		// VolumetricLightPassResources.SetParams). Сказать об этом прямо дешевле, чем оставить
		// человека крутить ползунок, который ничего не делает.
		if (!_viewport.VolumetricShadowsAvailable && _sceneViewport?.VolumetricShadowsAvailable != true)
		{
			ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "Теней нет - столбов не будет");
			Tooltip("Марш берёт тени из каскадного shadow map. Без теневого пасса остаётся\n" +
				"только ровный объёмный туман - включите тени в секции Sun & Shadows.");
		}

		ImGui.Spacing();

		// Логарифмический по той же причине, что у плотности тумана: рабочая зона - сотые доли.
		var density = _settings.VolumetricDensity;
		if (Slider("Плотность среды", ref density, 0.0005f, 1f, "%.4f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.VolumetricDensity = density;
		}
		Tooltip("Главная ручка. Сколько вещества в воздухе, 1/единица мира.\n" +
			"Больше - плотнее столбы и мутнее кадр.");

		var sunIntensity = _settings.VolumetricSunIntensity;
		if (Slider("Сила лучей", ref sunIntensity, 0f, 8f, "%.2f"))
		{
			_settings.VolumetricSunIntensity = sunIntensity;
		}
		Tooltip("Яркость СОЛНЕЧНОГО рассеяния - это и есть god rays.\n" +
			"Именно её режет тень: где тени нет, столб светится.");

		var anisotropy = _settings.VolumetricAnisotropy;
		if (Slider("Анизотропия", ref anisotropy, -0.95f, 0.95f, "%.2f"))
		{
			_settings.VolumetricAnisotropy = anisotropy;
		}
		Tooltip("Направленность рассеяния (фазовая функция Хеньи-Гринштейна).\n" +
			"0.6..0.85 - как реальная дымка: столбы вспыхивают при взгляде ПРОТИВ солнца.\n" +
			"0 - ровное свечение со всех сторон. Отрицательные - рассеяние назад (редко нужно).\n" +
			"Общую яркость не меняет, только перераспределяет её по направлениям.");

		var shadowStrength = _settings.VolumetricShadowStrength;
		if (Slider("Сила тени", ref shadowStrength, 0f, 1f, "%.2f"))
		{
			_settings.VolumetricShadowStrength = shadowStrength;
		}
		Tooltip("Насколько тень режет лучи. 1 - настоящие столбы с чёткими краями,\n" +
			"0 - тень игнорируется и остаётся однородный объёмный туман.");

		ImGui.Spacing();
		ImGui.TextDisabled("Качество марша:");

		var steps = _settings.VolumetricSteps;
		if (SliderInt("Шагов", ref steps, 8, 192))
		{
			_settings.VolumetricSteps = steps;
		}
		Tooltip("Главная ручка ЦЕНЫ пасса - шаги считаются на каждый пиксель.\n" +
			"На яркость НЕ влияет (интеграл берётся аналитически по отрезку),\n" +
			"только на гладкость границ: мало шагов - зернистые края столбов.\n" +
			"32-64 обычно достаточно, выше 96 разница почти не видна.");

		var maxDistance = _settings.VolumetricMaxDistance;
		if (Slider("Дальность марша", ref maxDistance, 10f, 2000f, "%.0f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.VolumetricMaxDistance = maxDistance;
		}
		Tooltip("Докуда идёт луч. Шагов фиксированное число, поэтому вдвое большая\n" +
			"дальность = вдвое более крупный шаг и более рваные столбы.\n" +
			"Держите в пределах последнего каскада теней: дальше него столбы\n" +
			"выключаются разом (за каскадами всё считается освещённым).");

		var start = _settings.VolumetricStartDistance;
		if (Slider("Ближняя отсечка", ref start, 0f, 20f, "%.2f"))
		{
			_settings.VolumetricStartDistance = start;
		}
		Tooltip("С какой дистанции начинать марш.\nУ самой камеры среда даёт только шум и съедает шаги.");

		ImGui.Spacing();
		ImGui.TextDisabled("Оптика среды:");

		var scattering = _settings.VolumetricScattering;
		if (Slider("Рассеяние", ref scattering, 0f, 4f, "%.2f"))
		{
			_settings.VolumetricScattering = scattering;
		}
		Tooltip("Во сколько раз плотность превращается в СВЕТ.\nОбщий множитель яркости всего эффекта.");

		var extinction = _settings.VolumetricExtinction;
		if (Slider("Экстинкция", ref extinction, 0.01f, 4f, "%.2f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.VolumetricExtinction = extinction;
		}
		Tooltip("Насколько среда ГАСИТ проходящий сквозь неё свет.\n" +
			"Разведена с рассеянием намеренно: низкая экстинкция при высоком рассеянии\n" +
			"даёт светящиеся столбы без замутнения кадра - физически такого вещества\n" +
			"не бывает, но запрос «лучи есть, а даль не в молоке» самый частый.");

		var maxOpacity = _settings.VolumetricMaxOpacity;
		if (Slider("Потолок плотности", ref maxOpacity, 0f, 1f, "%.2f"))
		{
			_settings.VolumetricMaxOpacity = maxOpacity;
		}
		Tooltip("Сколько среда вправе съесть от исходного кадра.\n1 - до полного молока, меньше - сквозь неё всегда что-то видно.");

		var heightFalloff = _settings.VolumetricHeightFalloff;
		if (Slider("Спад по высоте", ref heightFalloff, 0f, 1f, "%.3f"))
		{
			_settings.VolumetricHeightFalloff = heightFalloff;
		}
		Tooltip("Как быстро среда редеет вверх.\n0 - однородный объём;\nбольше - низкая стелющаяся пелена, в которой лучи видны только у пола.");

		var heightRef = _settings.VolumetricHeightRef;
		if (Slider("Опорная высота", ref heightRef, -50f, 50f, "%.1f"))
		{
			_settings.VolumetricHeightRef = heightRef;
		}
		Tooltip("Высота (Y мира), на которой плотность равна заданной выше.\nОбычно - уровень пола сцены.");

		ImGui.Spacing();
		ImGui.TextDisabled("Цвет:");

		var sunColor = new Vector3(_settings.VolumetricSunColorR, _settings.VolumetricSunColorG,
			_settings.VolumetricSunColorB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Цвет лучей", ref sunColor))
		{
			_settings.VolumetricSunColorR = sunColor.X;
			_settings.VolumetricSunColorG = sunColor.Y;
			_settings.VolumetricSunColorB = sunColor.Z;
			_changed = true;
		}
		Tooltip("Цвет самих столбов. Обычно берётся от солнца - тёплый на закате.");

		var ambientColor = new Vector3(_settings.VolumetricAmbientColorR,
			_settings.VolumetricAmbientColorG, _settings.VolumetricAmbientColorB);
		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.ColorEdit3("Цвет в тени", ref ambientColor))
		{
			_settings.VolumetricAmbientColorR = ambientColor.X;
			_settings.VolumetricAmbientColorG = ambientColor.Y;
			_settings.VolumetricAmbientColorB = ambientColor.Z;
			_changed = true;
		}
		Tooltip("Цвет среды ТАМ, КУДА СОЛНЦЕ НЕ ДОШЛО - свет неба, тени его не режут.\nОбычно холоднее цвета лучей.");

		var ambientIntensity = _settings.VolumetricAmbientIntensity;
		if (Slider("Сила в тени", ref ambientIntensity, 0f, 3f, "%.2f"))
		{
			_settings.VolumetricAmbientIntensity = ambientIntensity;
		}
		Tooltip("Без неё среда в тени абсолютно чёрная,\nи столбы читаются как вырезанные ножницами, а не как свет в дымке.");

		var ambientFloor = _settings.VolumetricAmbientShadowFloor;
		if (Slider("Небо в тени", ref ambientFloor, 0f, 1f, "%.2f"))
		{
			_settings.VolumetricAmbientShadowFloor = ambientFloor;
		}
		Tooltip("Во сколько раз свечение слабее там, куда солнце не дошло.\n" +
			"ГЛАВНАЯ ручка против «молока»: на 1 крытый интерьер светится наравне\n" +
			"с залитым солнцем двором, кадр теряет контраст и насыщенность целиком.\n" +
			"0.1..0.2 - свечение живёт только у проёмов, интерьер остаётся плотным.");
	}

	private void DrawExposureSection()
	{
		// Кривая - ПЕРЕД галкой авто-экспозиции и вне её ветки: она действует в обоих режимах
		// (в LDR её применяет сам UnlitInstancedPS, в HDR - TonemapPass), а авто-экспозиция по
		// умолчанию выключена. Спрячь её внутрь - и главная ручка «почему плоско» осталась бы
		// недоступной в дефолтной конфигурации.
		var curveLabels = new[] { "PBR Neutral", "ACES (filmic)", "AgX (filmic)" };
		var curve = Math.Clamp(_settings.ToneCurve, 0, curveLabels.Length - 1);
		if (curve != _settings.ToneCurve)
		{
			// Кламп только для показа оставлял окно врущим: комбо на "PBR Neutral", в настройках -
			// прежний мусорный индекс, и шейдер берёт свою ветку по нему.
			_settings.ToneCurve = curve;
			_changed = true;
		}

		ImGui.SetNextItemWidth(180 * _scale);
		if (ImGui.Combo("Кривая тонмапа", ref curve, curveLabels, curveLabels.Length))
		{
			_settings.ToneCurve = curve;
			_changed = true;
		}
		Tooltip("PBR Neutral - эталон glTF: ниже ~0.76 тождественна, то есть НАРОЧНО не добавляет\n" +
			"ни контраста, ни глубины теней. Правильно для оценки материала и ровно поэтому\n" +
			"кадр с ней читается плоским.\n\n" +
			"ACES - классическая киношная кривая: контраст в средних тонах, глубокий носок,\n" +
			"укатанные света. Уводит оттенок насыщенных ярких цветов (оранжевый в жёлтый).\n\n" +
			"AgX - тот же фильмический контраст, но БЕЗ сдвига оттенка: пересвет уходит в белый\n" +
			"через десатурацию, а не через смену тона. Обычно лучший выбор для «покрасивее».");

		ImGui.Spacing();

		var eyeAdaptation = _settings.PreviewEyeAdaptation;
		if (ImGui.Checkbox("Auto exposure (eye adaptation)", ref eyeAdaptation))
		{
			_settings.PreviewEyeAdaptation = eyeAdaptation;
			_changed = true;
		}
		Tooltip("Замер средней яркости готового кадра + временное сглаживание: экспозиция приводит сцену\nк Key value, как глаз привыкает к свету. Переводит превью на HDR-конвейер (линейный кадр,\nтонемап отдельным пассом) - конвейер перестраивается на месте, модель не перезагружается.");

		if (!eyeAdaptation)
		{
			return;
		}

		ImGui.Spacing();

		var key = _settings.EyeAdaptationKey;
		if (Slider("Key value", ref key, 0.02f, 1f, "%.3f"))
		{
			_settings.EyeAdaptationKey = key;
		}
		Tooltip("Средняя яркость, к которой экспонируется кадр. 0.18 - фотографический средне-серый;\nвыше - светлее вся картинка целиком.");

		var ev = _settings.EyeAdaptationExposureCompensation;
		if (Slider("Exposure compensation (EV)", ref ev, -4f, 4f, "%.2f"))
		{
			_settings.EyeAdaptationExposureCompensation = ev;
		}
		Tooltip("Художественная поправка в стопах поверх авто-экспозиции: +1 EV - вдвое светлее.");

		var minLum = _settings.EyeAdaptationMinLuminance;
		if (Slider("Min luminance", ref minLum, 0.001f, 1f, "%.3f", ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.EyeAdaptationMinLuminance = minLum;
		}
		Tooltip("Нижняя граница ИЗМЕРЕННОЙ яркости: без неё почти чёрный кадр\n(камера уткнулась в стену) вытягивается в шум.");

		var maxLum = _settings.EyeAdaptationMaxLuminance;
		if (Slider("Max luminance", ref maxLum, 0.1f, 64f, "%.2f", ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.EyeAdaptationMaxLuminance = maxLum;
		}
		Tooltip("Верхняя граница измеренной яркости - от провала сцены в чёрный,\nкогда в кадр попадает солнце.");

		var speedUp = _settings.EyeAdaptationSpeedUp;
		if (Slider("Adapt speed (to light)", ref speedUp, 0.1f, 10f, "%.2f"))
		{
			_settings.EyeAdaptationSpeedUp = speedUp;
		}
		Tooltip("Скорость привыкания к более СВЕТЛОМУ кадру, 1/сек («зажмуриться»).");

		var speedDown = _settings.EyeAdaptationSpeedDown;
		if (Slider("Adapt speed (to dark)", ref speedDown, 0.1f, 10f, "%.2f"))
		{
			_settings.EyeAdaptationSpeedDown = speedDown;
		}
		Tooltip("Скорость привыкания к более ТЁМНОМУ кадру, 1/сек.\nПривычно медленнее подъёма - так же ведёт себя глаз.");
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

	private void DrawProbeGiSection()
	{
		ImGui.Spacing();

		// Небо стоит ДО раннего выхода по !enabled: оно рисуется независимо от probe GI, и прятать
		// его вместе с настройками бейка - значит терять галку ровно тогда, когда GI выключен.
		var sky = _settings.PreviewSkyBackground;
		if (ImGui.Checkbox("Sky background", ref sky))
		{
			_settings.PreviewSkyBackground = sky;
			_changed = true;
		}
		Tooltip("Рисовать окружение фоном вместо чистой заливки.\nПереключается на живом конвейере.");

		ImGui.Spacing();

		var enabled = _settings.PreviewProbeGi;
		if (ImGui.Checkbox("Probe GI (DDGI-lite)", ref enabled))
		{
			_settings.PreviewProbeGi = enabled;
			_changed = true;
		}
		Tooltip("CPU-бейк сетки irradiance-проб (SH L1) + sky visibility по геометрии модели:\nнебо и отскоки света вместо константного эмбиента. Требует включённых теней.");

		ImGui.SameLine();
		ImGui.TextDisabled($"[превью: {_viewport.ProbeGiStatus}]");
		if (_sceneViewport != null)
		{
			ImGui.SameLine();
			ImGui.TextDisabled($"[сцена: {_sceneViewport.ProbeGiStatus}]");
		}

		if (!enabled)
		{
			return;
		}

		var realtime = _settings.ProbeGiRealtime;
		if (ImGui.Checkbox("Реальное время (без сходимости)", ref realtime))
		{
			_settings.ProbeGiRealtime = realtime;
			_changed = true;
		}
		Tooltip("Запечка против динамики. Обычный режим копит поле бегущим средним и\n" +
			"останавливается по сходимости: чем дольше, тем меньше шума, но и тем слабее\n" +
			"реакция на изменения - для статичной сцены это то, что нужно.\n" +
			"В реальном времени раунды не останавливаются, а среднее становится\n" +
			"экспоненциальным с постоянной альфой: поле отслеживает свет бесконечно,\n" +
			"ценой остаточного шума. Переключается на живой сессии, без ребейка.");

		if (realtime)
		{
			ImGui.Indent();
			var realtimeRays = _settings.ProbeGiRealtimeRays;
			if (SliderInt("Лучей за раунд", ref realtimeRays, 8, 1024))
			{
				_settings.ProbeGiRealtimeRays = realtimeRays;
			}
			Tooltip("Против ДЫХАНИЯ ЯРКОСТИ всей сцены. Веер лучей у всех проб раунда общий,\n" +
				"поэтому ошибка оценки скоррелирована по сетке и видна не как шум отдельных\n" +
				"проб, а как общая пульсация; гасится она числом лучей (1/sqrt(N)).\n" +
				"Замерено на Sponza (размах средней яркости за раунд, alpha 0.15):\n" +
				"  16 лучей - 5.1% (видно отчётливо), 32 - 1.5%, 64 - 1.1%, 128 - 0.5%.\n" +
				"Стоимость раунда линейна по числу лучей. Live, ребейка не требует.");

			var blend = _settings.ProbeGiRealtimeBlend;
			if (Slider("Вес раунда", ref blend, 0.01f, 0.5f, "%.3f"))
			{
				_settings.ProbeGiRealtimeBlend = blend;
				_changed = true;
			}
			Tooltip("Против мигания ОТДЕЛЬНЫХ проб - именно этой ручкой, а не лучами.\n" +
				"Альфа экспоненциального среднего: возмущение затухает как (1-alpha)^n,\n" +
				"а дрожание пробы идёт как sqrt(alpha/(2-alpha)). Редактор выпускает не\n" +
				"больше раунда за кадр, поэтому 0.04 при 60 к/с - установление за ~1.2 с.\n" +
				"Замерено на Sponza при 64 лучах (p99 и максимум смены пробы за раунд):\n" +
				"  0.15 - p99 6.3%, max 79%   0.08 - p99 3.4%, max 48%\n" +
				"  0.04 - p99 1.8%, max 24%   0.02 - p99 1.0%, max 12% (отклик 2.5 с)\n" +
				"Меньше - спокойнее картинка, дольше догоняет смену света. Live.");

			var maxStep = _settings.ProbeGiRealtimeMaxStep;
			if (Slider("Предел шага", ref maxStep, 0f, 0.2f, "%.3f"))
			{
				_settings.ProbeGiRealtimeMaxStep = maxStep;
				_changed = true;
			}
			Tooltip("Главная ручка против «диско»: сколько проба вправе изменить за раунд.\n" +
				"Вес раунда - фильтр, ОДИНАКОВЫЙ для всех проб, и ставить его приходится\n" +
				"по худшей: спокойным это лишняя вязкость, буйным всё равно мало. Предел\n" +
				"шага спокойных не трогает вовсе и включается только там, где оценка\n" +
				"скачет - проба перестаёт вспыхивать и начинает переползать. То есть при\n" +
				"нехватке лучей качество деградирует в ЗАДЕРЖКУ, а не во вспышки.\n" +
				"Яркость от него не теряется: режется производная, а не величина.\n" +
				"Замерено на Sponza при 8 лучах, весе 0.5, сетке 64, небе 12\n" +
				"(доля проб, дёрнувшихся за раунд больше чем на 10%):\n" +
				"  выключен - 61.2%   0.10 - 22.0%   0.03 - 0.6%   0.01 - 0.1%\n" +
				"Средняя яркость поля при этом 5.61 / 5.56 / 5.60 / 5.70 - не уезжает.\n" +
				"0 = выключить. Меньше - спокойнее и медленнее отклик. Live.");

			var relocation = _settings.ProbeGiRealtimeRelocation;
			if (Slider("Релокация проб", ref relocation, 0f, 0.45f, "%.2f"))
			{
				_settings.ProbeGiRealtimeRelocation = relocation;
				_changed = true;
			}
			Tooltip("Лечит ПРИЧИНУ мигания густой сетки, а не следствие. Чем мельче ячейка,\n" +
				"тем больше проб оказывается ВНУТРИ стен и колонн - а такая проба и мигает\n" +
				"(её лучи мечутся между задними гранями и небом за краем), и течёт светом\n" +
				"сквозь стену. Здесь такая проба каждый раунд отодвигается наружу через\n" +
				"ближайшую заднюю грань - её же лучи и показывают, где выход.\n" +
				"Значение - предел отхода в долях шага сетки. Выше 0.45 нельзя:\n" +
				"проба покинет свою ячейку, и трилинейная интерполяция соврёт сильнее,\n" +
				"чем выигрыш. 0 = выключить. Live.");

			var gamma = _settings.ProbeGiRealtimeGamma;
			if (Slider("Гамма накопления", ref gamma, 1f, 8f, "%.1f"))
			{
				_settings.ProbeGiRealtimeGamma = gamma;
				_changed = true;
			}
			Tooltip("Перцептивное накопление (Majercik 2021, §4.2, адаптация к SH): яркость\n" +
				"пробы копится не линейно, а по кривой глаза - редкая вспышка-светлячок\n" +
				"давится примерно в (вес раунда)^(гамма-1) раз, а переход свет→тень\n" +
				"ускоряется и читается как ровное потемнение, без бесконечного хвоста.\n" +
				"Замерено на Sponza (сетка 64, небо 12, 64 луча, вес 0.04): худший рывок\n" +
				"пробы за раунд 84% - 38% (гамма 3) - 29% (гамма 5); вместе с пределом\n" +
				"шага 0.03: 7% - 3%. Средняя яркость поля при этом теряет ~0.1%.\n" +
				"1 = линейно (выключено). Авторская величина - 5. Live.");

			var variability = _settings.ProbeGiVariabilityThreshold;
			if (Slider("Порог сходимости", ref variability, 0f, 0.3f, "%.3f"))
			{
				_settings.ProbeGiVariabilityThreshold = variability;
				_changed = true;
			}
			Tooltip("Probe Variability из RTXGI-DDGI: изменчивость пробы - это коэффициент\n" +
				"вариации её яркости (разброс, делённый на среднее), усреднённый по объёму.\n" +
				"Величина безразмерная, поэтому тёмный интерьер и залитый солнцем двор\n" +
				"сравнимы между собой. Опустилась ниже порога - объём сошёлся, и раунды\n" +
				"перестают пускаться ВОВСЕ, пока свет или геометрия не тронутся; раз в 32\n" +
				"раунда полный проход идёт всё равно, страховкой от незамеченного изменения.\n" +
				"Это следующая ступень после сна проб: сон экономит три четверти лучей,\n" +
				"а здесь диспатча нет совсем.\n" +
				"Замерено на Sponza (128 лучей): установившаяся изменчивость 0.058, при\n" +
				"пороге 0.08 пропускается 94% раундов, средняя яркость поля не меняется.\n" +
				"Величина зависит от сцены и числа лучей (шум оценки идёт как 1/sqrt(N)):\n" +
				"поставишь ниже установившейся - остановка не включится никогда, слишком\n" +
				"высоко - объём замрёт не досчитавшись. 0 = выключить. Live.");
			ImGui.Unindent();
		}

		if (_settings.ProbeGiRealtime)
		{
			var cascades = _settings.ProbeGiCascades;
			if (SliderInt("Каскады сетки", ref cascades, 1, 3))
			{
				_settings.ProbeGiCascades = cascades;
			}
			Tooltip("Объёмы проб для БОЛЬШИХ сцен: 1 = одна сетка на всю сцену (как раньше).\n" +
				"Выше - дополнительные каскады вокруг центра сцены тем же бюджетом проб:\n" +
				"каждый покрывает бокс вдвое меньше предыдущего, то есть ячейка вдвое мельче.\n" +
				"Выборка идёт от мелкого к крупному, базовая сетка остаётся гарантией покрытия.\n" +
				"На маленькой сцене (один объект) смысла нет - там и базовая сетка мелкая;\n" +
				"на сцене-уровне даёт детализацию GI в центре, не раздувая бюджет кубически.\n" +
				"Требует реального времени. Ребейк.");
		}

		// Тумблера CPU/GPU больше нет: раунды крутит только GPU (compute), CPU-раунд остался
		// эталоном сверки в CLI. Аппаратная трассировка - единственная ручка пути.
		{
			var hardwareSupported = _viewport.RayTracingSupported;
			if (!hardwareSupported)
			{
				ImGui.BeginDisabled();
			}

			var hardware = _settings.ProbeGiHardwareRayTracing;
			if (ImGui.Checkbox("Аппаратное ускорение трассировки", ref hardware))
			{
				_settings.ProbeGiHardwareRayTracing = hardware;
				_changed = true;
			}

			if (!hardwareSupported)
			{
				ImGui.EndDisabled();
				ImGui.SameLine();
				ImGui.TextDisabled("(устройство не умеет)");
			}

			Tooltip("Пускать лучи через RayQuery по аппаратным структурам ускорения (BLAS/TLAS)\n" +
				"вместо программного обхода собственного BVH. Ускоряет САМ обход - то есть\n" +
				"содержимое диспатча; накладные на границах диспатчей это не трогает.\n" +
				"Требует поддержки inline-трассировки (DXR 1.1 / VK_KHR_ray_query).\n" +
				"Недоступно или не собралось - молча откатывается на программный путь.");
		}

		ImGui.Spacing();
		ImGui.TextDisabled("Live (без ребейка):");

		// Логарифмический: на линейной шкале 0.25-128 рабочая зона (1-4) занимает пару пикселей у
		// левого края, и ручка мгновенно уезжает в сотни - эмбиент пересвечивается.
		var boost = _settings.ProbeGiAmbientBoost;
		if (Slider("Ambient boost", ref boost, 0.25f, 128f, "%.2f",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.ProbeGiAmbientBoost = boost;
		}
		Tooltip("Множитель готовой probe-irradiance - экспозиция эмбиента.\nРабочая зона 1-4; десятки и выше пересвечивают сцену в белое (кламп при пуше - 128).");

		var shadowFloor = _settings.ProbeGiShadowFloor;
		if (Slider("Sun bounce in shadow", ref shadowFloor, 0f, 1f, "%.2f"))
		{
			_settings.ProbeGiShadowFloor = shadowFloor;
		}
		Tooltip("Сколько СОЛНЕЧНОЙ доли эмбиента остаётся в экранной тени ключа\n(0 - контактные тени жёстче, 1 - мягче). Действует там, где поле проб\nсолнечное - см. красный канал Probe debug view; в небесных сценах\n(двор в тени) эффекта почти нет - там крути Sky ambient in shadow.");

		var skyShadowFloor = _settings.ProbeGiSkyShadowFloor;
		if (Slider("Sky ambient in shadow", ref skyShadowFloor, 0.05f, 1f, "%.2f"))
		{
			_settings.ProbeGiSkyShadowFloor = skyShadowFloor;
		}
		Tooltip("Сколько НЕБЕСНОЙ доли эмбиента остаётся в экранной тени ключа.\n1 (дефолт) - физически честно: двор в тени залит небом (Intel Sponza).\nНиже - тени темнеют целиком, под более контрастный муд.");

		var specFloor = _settings.ProbeGiSpecularFloor;
		if (Slider("Env specular occlusion", ref specFloor, 0f, 1f, "%.2f"))
		{
			_settings.ProbeGiSpecularFloor = specFloor;
		}
		Tooltip("Флор глушения отражений окружения запечённой видимостью неба\n(0 - в интерьерах отражения гаснут полностью).");

		var bias = _settings.ProbeGiNormalBias;
		if (Slider("Normal bias", ref bias, 0f, 1f, "%.2f"))
		{
			_settings.ProbeGiNormalBias = bias;
		}
		Tooltip("Сдвиг точки сэмпла вдоль нормали в долях ячейки сетки -\nот утечек света/тьмы сквозь тонкие стены.");

		var viewBias = _settings.ProbeGiViewBias;
		if (Slider("View bias", ref viewBias, 0f, 1f, "%.2f"))
		{
			_settings.ProbeGiViewBias = viewBias;
		}
		Tooltip("Куда именно сдвигать точку сэмпла: 1 - к камере, 0 - по нормали.\n" +
			"1 (дефолт): фасеток нет, но при движении камеры пробы будто\n" +
			"съезжают и меняют яркость - освещение видозависимо.\n" +
			"0: от камеры не зависит вовсе, но на жёстких рёбрах в эмбиенте\n" +
			"проступают грани треугольников. У статьи DDGI - 0.8.");

		ImGui.Spacing();
		ImGui.TextDisabled("Бейк (изменение перепекает пробы):");

		var skyIntensity = _settings.ProbeGiSkyIntensity;
		if (Slider("Sky intensity", ref skyIntensity, 0.25f, 12f, "%.2f"))
		{
			_settings.ProbeGiSkyIntensity = skyIntensity;
		}
		Tooltip("Яркость неба в бейке - небесная часть эмбиента (двор в тени, ниши, sky visibility).");

		// Границы = ровно тот кламп, с которым значение уходит в бейк (ProbeGi.cs: Clamp(..., 16, 512)):
		// разметка 32..256 срезала оба конца, и заявленный в доке диапазон был недостижим из окна.
		var rays = _settings.ProbeGiRaysPerProbe;
		if (SliderInt("Rays per probe", ref rays, 16, 512))
		{
			_settings.ProbeGiRaysPerProbe = rays;
		}
		Tooltip("Лучей на пробу: больше - глаже поле и точнее sky visibility, линейно дольше бейк.");

		var visRes = _settings.ProbeGiVisRes;
		if (SliderInt("Visibility res", ref visRes, ProbeGiBakeResult.MinVisRes, ProbeGiBakeResult.MaxVisRes))
		{
			_settings.ProbeGiVisRes = visRes;
		}
		Tooltip("Сторона окто-карты ГЛУБИН на пробу (DDGI visibility): по ней тест Чебышёва решает,\n" +
			"не заслонена ли проба стеной от затеняемой точки - это главная защита от протечек\n" +
			"света сквозь тонкую геометрию (шторы, кромки колонн).\n" +
			"8 = ~25° на тексель, 16 = ~12° (значение эталонной статьи Majercik 2021).\n" +
			"ВНИМАНИЕ: атлас растёт квадратично, а лучей на тексель становится во столько же раз\n" +
			"МЕНЬШЕ - при неизменном Rays per probe качество падает (замерено: на 16 паразитная\n" +
			"подсветка интерьера Sponza выросла с 14.1 до 15.9). Поднимать вместе с Rays per probe.\n" +
			"Ребейк: раскладка атласов задаётся при создании сессии.");

		// Верх = ProbeGi.cs: Clamp(options.Bounces, 1, 6). Разметка до 4 отрезала два последних
		// переотскока, которые движок принимает и которые обещает дока ручки.
		var bounces = _settings.ProbeGiBounces;
		if (SliderInt("Bounces", ref bounces, 1, 6))
		{
			_settings.ProbeGiBounces = bounces;
		}
		Tooltip("Итераций сбора: 1 - только небо + прямой отскок солнца,\nкаждая следующая добавляет переотскок (глубоким дворам нужно 2-3).");

		var bounceSat = _settings.ProbeGiBounceSaturation;
		if (Slider("Bounce saturation", ref bounceSat, 0f, 1f, "%.2f"))
		{
			_settings.ProbeGiBounceSaturation = bounceSat;
		}
		Tooltip("Насыщенность цветного отскока: 0 - серый баунс, 1 - полный цвет альбедо.\nЯркость баунса не меняется (солнце рассеивается так же сильно) -\nниже значение, тем меньше яркие цветные ткани светят как лампочки.");

		// Низ = ProbeGi.cs: Clamp(options.GridDensity, 4f, 64f) - самая грубая сетка была недоступна.
		var density = _settings.ProbeGiGridDensity;
		if (Slider("Grid density", ref density, 4f, 64f, "%.0f"))
		{
			_settings.ProbeGiGridDensity = density;
		}
		Tooltip("Ячеек сетки на максимальный габарит сцены: плотнее - меньше утечек и точнее\nлокализация света, дороже бейк (число проб растёт кубически).");

		// Верх списка = ProbeGiBaker.MaxProbeBudget: раньше комбо обрывалось на 32k, и поднятый
		// потолок бейкера был недостижим из окна.
		int[] probeCaps = [2048, 4096, 8192, 16384, 32768, 131072, 524288, ProbeGiBaker.MaxProbeBudget];
		var capLabels = new[] { "2k", "4k", "8k", "16k", "32k", "128k", "512k", "2M" };
		var capIndex = Array.IndexOf(probeCaps, _settings.ProbeGiMaxProbes);
		if (capIndex < 0)
		{
			capIndex = Array.IndexOf(probeCaps, 8192);
			_settings.ProbeGiMaxProbes = probeCaps[capIndex];
			_changed = true;
		}

		ImGui.SetNextItemWidth(120 * _scale);
		if (ImGui.Combo("Max probes", ref capIndex, capLabels, capLabels.Length))
		{
			_settings.ProbeGiMaxProbes = probeCaps[capIndex];
			_changed = true;
		}
		Tooltip("Потолок числа проб: ячейка укрупняется, пока сетка не влезет.\nБейк растёт линейно по числу проб (32k ~ 1.3 с на Sponza), плюс сетку сверху\nрежут потолок по оси (128) и предел размера атласа видимости.");

		ImGui.Spacing();
		if (ImGui.Button("Rebake now", new Vector2(120 * _scale, 0)))
		{
			_viewport.RequestProbeRebake();
		}
		Tooltip("Принудительный ребейк (например, после правки HDR-файла на диске).");

		ImGui.SameLine();
		var debugView = _settings.ProbeGiDebugView;
		if (ImGui.Checkbox("Probe debug view", ref debugView))
		{
			_settings.ProbeGiDebugView = debugView;
			_changed = true;
		}
		Tooltip("Отладочный вид поля проб: R = солнечная доля (зона действия Sun bounce in shadow),\nG = видимость неба (зона Env specular occlusion), B = экранная тень ключа.");

		var debugProbes = _settings.ProbeGiDebugProbes;
		if (ImGui.Checkbox("Probe placement", ref debugProbes))
		{
			_settings.ProbeGiDebugProbes = debugProbes;
			_changed = true;
		}
		Tooltip("Где стоит каждая проба и что с ней сделала релокация:\n" +
			"  зелёное пятно - проба на своём узле сетки, всё в порядке;\n" +
			"  жёлтое-красное - отодвинута из геометрии (краснее = дальше);\n" +
			"  синее - признана невалидной (замурована, в интерполяцию не идёт);\n" +
			"  фон - валидность поля вокруг.\n" +
			"Отмечаются пробы, рядом с которыми есть поверхность - то есть ровно те,\n" +
			"из-за которых густая сетка и мигает. Пробы в открытом воздухе не видны:\n" +
			"своего прохода геометрии у этого вида нет, метки рисуются на поверхностях.\n" +
			"Старше галочки Probe debug view, если включены обе.");

		var showProbes = _settings.ProbeGiShowProbes;
		if (ImGui.Checkbox("Probe spheres", ref showProbes))
		{
			_settings.ProbeGiShowProbes = showProbes;
			_changed = true;
		}
		Tooltip("Шарик на каждую пробу В ЕЁ ФАКТИЧЕСКОЙ позиции (с релокацией) - в отличие от\n" +
			"Probe placement, это настоящая геометрия с депт-тестом, видны и пробы в воздухе:\n" +
			"  цвет - накопленный пробой свет (SH L0): тёмный в тени, яркий у света;\n" +
			"  красный - проба считает себя в стене (после релокации таких почти нет);\n" +
			"  голубая кромка - проба переехала релокацией (видно, кого вытащило).\n" +
			"Рисуется поверх сцены с её глубиной, MSAA общий. Live, ребейка не требует.");
		// --- Отладка BVH (структура ускорения трассировки проб) ---
		ImGui.Separator();

		var bvhDebug = _settings.ProbeGiBvhDebug;
		if (ImGui.Checkbox("BVH boxes", ref bvhDebug))
		{
			_settings.ProbeGiBvhDebug = bvhDebug;
			_changed = true;
		}
		Tooltip("Каркасные боксы узлов BVH - дерева, по которому идёт трассировка проб.\n" +
			"Видно, во что реально свернулась геометрия сцены: раздутый узел (один бокс\n" +
			"на пол-сцены) - это лучи, перебирающие лишние треугольники.\n" +
			"Само дерево кешируется рядом с моделью файлом .bhv.bin: его сборка на тяжёлом\n" +
			"ассете стоит десятки секунд и делается один раз на версию файла модели.\n" +
			"Статистика дерева печатается в консоль при включении.");

		if (bvhDebug)
		{
			var bvhLeaves = _settings.ProbeGiBvhDebugLeaves;
			if (ImGui.Checkbox("BVH leaves only", ref bvhLeaves))
			{
				_settings.ProbeGiBvhDebugLeaves = bvhLeaves;
				_changed = true;
			}
			Tooltip("Только листья дерева - фактическая гранулярность разбиения\n" +
				"(в листе <= 4 треугольника). Список режется на 20000 боксах.");

			if (!bvhLeaves)
			{
				var bvhDepth = _settings.ProbeGiBvhDebugDepth;
				if (SliderInt("BVH depth", ref bvhDepth, 0, 16))
				{
					_settings.ProbeGiBvhDebugDepth = bvhDepth;
				}
				Tooltip("До какой глубины показывать узлы (0 = только корневой бокс всей сцены).\n" +
					"Боксов на уровне вдвое больше, чем на предыдущем.");
			}
		}
	}

	// --- Отложенное применение -------------------------------------------------------------------

	/// <summary>Набирает буфер отложенных ручек из настроек. force - после применения/отмены;
	/// без него пересинхронизация происходит только когда настройки изменил КТО-ТО ДРУГОЙ (модалка
	/// Settings пишет в те же поля): иначе каждый кадр затирал бы то, что человек сейчас правит.</summary>
	private void SyncPendingFromSettings(bool force)
	{
		var current = (_settings.PreviewMsaaSamples, _settings.PreviewAnisotropicFiltering,
			_settings.PreviewEnvironmentHdr ?? string.Empty, _settings.PreviewMaxTextureSize);

		// Первый кадр окна тоже попадает сюда: _pendingSource пустой и с настройками не совпадает.
		if (!force && current == _pendingSource)
		{
			return;
		}

		_pendingSource = current;
		_pendingMsaa = current.Item1;
		_pendingAniso = current.Item2;
		_pendingHdr = current.Item3;
		_pendingMaxTextureSize = current.Item4;
	}

	/// <summary>Что в буфере разошлось с настройками, человеческим текстом «было -> стало». Пустой
	/// список = применять нечего.</summary>
	private List<string> CollectPendingChanges()
	{
		var changes = new List<string>();

		if (_pendingMsaa != _settings.PreviewMsaaSamples)
		{
			changes.Add($"MSAA: {MsaaLabel(_settings.PreviewMsaaSamples)} -> {MsaaLabel(_pendingMsaa)}");
		}

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

		static string MsaaLabel(int samples) => samples <= 1 ? "Off" : $"{samples}x";
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
		_settings.PreviewMsaaSamples = _pendingMsaa;
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
