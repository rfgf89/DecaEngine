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

/// <summary>Секции света и теней: мировой свет, каскады, отладочный захват слайсов shadow map. Часть <see cref="GraphicsSettingsWindow"/> - файл на тему,
/// поля и применение изменений живут в основном файле.</summary>
public partial class GraphicsSettingsWindow
{
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

		// Порядок - по возрастанию накладных расходов; хранится ШЕЙДЕРНОЕ значение (см.
		// EditorSettings.ShadowFilterMode: 0 обязан оставаться PCSS), поэтому индексы комбо
		// мапятся через таблицу. Верхний пункт (Ray-traced) показывается только на устройстве с
		// inline-трассировкой - без неё вариант шейдера не соберётся вовсе.
		bool rtAvailable = _viewport?.RayTracingSupported ?? false;
		int[] shadowModeValues = rtAvailable ? [1, 2, 0, 3, 4] : [1, 2, 0, 3];
		var shadowModeLabels = rtAvailable
			? new[]
			{
				"Hard (1 тап)",
				"PCF 3x3",
				"PCSS (полутень)",
				"PCSS HQ (32 тапа)",
				"Ray-traced (перезагрузка)",
			}
			: new[]
			{
				"Hard (1 тап)",
				"PCF 3x3",
				"PCSS (полутень)",
				"PCSS HQ (32 тапа)",
			};
		var shadowModeIndex = Array.IndexOf(shadowModeValues, _settings.ShadowFilterMode);
		if (shadowModeIndex < 0)
		{
			shadowModeIndex = 2;
		}

		ImGui.SetNextItemWidth(200 * _scale);
		if (ImGui.Combo("Shadow filtering", ref shadowModeIndex, shadowModeLabels, shadowModeLabels.Length))
		{
			_settings.ShadowFilterMode = shadowModeValues[shadowModeIndex];
			_changed = true;
		}
		Tooltip("Фильтр теней солнца И punctual-светов, по возрастанию цены:\n" +
			"  Hard - один аппаратный тап, край в тексель. Самый дешёвый.\n" +
			"  PCF 3x3 - постоянная мягкость в тексель, 9 тапов.\n" +
			"  PCSS - полутень от углового размера источника (контакт резкий, дальше мягче),\n" +
			"    16+16 тапов по диску Фогеля, зерно усредняет TAAU.\n" +
			"  PCSS HQ - тот же PCSS с удвоенным веером (32+32) и более широкой полутенью -\n" +
			"    для стоп-кадров и работы без TAAU.\n" +
			"  Ray-traced - тень солнца ТЕНЕВЫМИ ЛУЧАМИ по TLAS (8 лучей в конусе диска):\n" +
			"    физическая полутень без каскадов и байасов. Переключение ПЕРЕЗАГРУЖАЕТ модель\n" +
			"    (вариант шейдера собирается DXC). Листва с альфа-тестом затеняет монолитом;\n" +
			"    punctual-света остаются на PCSS.\n" +
			"Ширину полутени задают Sun angular size (солнце) и SourceRadius света (лампы).");

		var sunSize = _settings.SunAngularSize;
		if (Slider("Sun angular size", ref sunSize, 0.25f, 8f, "%.2f°",
			ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic))
		{
			_settings.SunAngularSize = sunSize;
		}
		Tooltip("Видимый ДИАМЕТР диска солнца, градусы - ширина полутени PCSS: чем крупнее диск,\n" +
			"тем мягче тень с расстоянием от объекта (контактная остаётся резкой).\n" +
			"Реальное солнце ~0.53°; дефолт 1° - мягкость видна и на коротких тенях.");

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

}
