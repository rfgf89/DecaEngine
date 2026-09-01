using System.Numerics;
using DecaEngine.Graphics;

namespace DecaEngine.Editor;

/// <summary>
/// Пуш ручек окна Graphics в окружение вьюпорта - ОДНА копия на оба вьюпорта.
///
/// Раньше каждый из этих методов существовал дважды: в <see cref="ModelPreviewViewport"/> и в
/// <see cref="PrefabSceneViewport"/>, слово в слово. Комментарии в них так и говорили - «зеркало
/// ModelPreviewViewport.ApplyBloomSettings». Зеркала расходятся: новая живая ручка, дописанная в
/// одну цепочку и забытая в другой, молча работает в одном окне и не работает в другом. Так уже
/// родились три бага - «Порог сходимости» терялся в обоих инлайнах realtime-пушей, радиусы AO/GI не
/// применялись в Scene View, «Rebake now» перепекал только превью.
///
/// Здесь лежит только то, что зависит ИСКЛЮЧИТЕЛЬНО от <see cref="EditorSettings"/> и окружения.
/// Всё, что смотрит на состояние конкретного вьюпорта - его таргеты, флаги отложенного применения,
/// поддержку RT у ЕГО устройства, - осталось в вьюпортах: сливать разное только потому, что оно
/// похоже, значит менять один сорт дублирования на другой.
/// </summary>
internal static class ViewportSettingsPush
{
	/// <summary>Живые ручки блума (no-op когда он выключен - см.
	/// ModelViewportEnvironment.SetBloomParams).</summary>
	public static void Bloom(ModelViewportEnvironment env, EditorSettings s)
	{
		env.SetBloomParams(
			Math.Max(s.BloomThreshold, 0f),
			Math.Max(s.BloomKnee, 0.0001f),
			Math.Max(s.BloomRadius, 0f),
			Math.Max(s.BloomIntensity, 0f));
	}

	/// <summary>Живые ручки цветокоррекции и виньетки (no-op когда грейдинг выключен - см.
	/// ModelViewportEnvironment.SetColorGrade).</summary>
	public static void ColorGrade(ModelViewportEnvironment env, EditorSettings s)
	{
		env.SetColorGrade(
			Math.Max(s.GradeSaturation, 0f),
			Math.Max(s.GradeContrast, 0f),
			Math.Max(s.GradeGamma, 0.001f),
			Math.Clamp(s.GradeTemperature, -1f, 1f),
			Math.Clamp(s.GradeTint, -1f, 1f),
			new Vector3(s.GradeShadowR, s.GradeShadowG, s.GradeShadowB),
			new Vector3(s.GradeHighlightR, s.GradeHighlightG, s.GradeHighlightB));

		env.SetVignette(
			Math.Clamp(s.VignetteIntensity, 0f, 1f),
			Math.Max(s.VignetteRadius, 0.001f),
			Math.Max(s.VignetteSmoothness, 0.001f),
			Math.Clamp(s.VignetteRoundness, 0f, 1f));
	}

	/// <summary>Живые ручки тумана (no-op когда он выключен - см.
	/// ModelViewportEnvironment.SetFogParams).
	///
	/// Направление солнца сюда НЕ входит: оно пушится покадрово вместе с базисом камеры (см.
	/// SetCameraTransform). В Scene View солнце вращают гизмо, а они не поднимают событие настроек -
	/// пушился бы туман отсюда, подсветка отставала бы от гизмо на кадр.</summary>
	public static void Fog(ModelViewportEnvironment env, EditorSettings s)
	{
		env.SetFogParams(
			Math.Max(s.FogDensity, 0f),
			Math.Max(s.FogHeightFalloff, 0f),
			s.FogHeightRef,
			Math.Max(s.FogStartDistance, 0f),
			Math.Max(s.FogMaxDistance, 1f),
			Math.Clamp(s.FogMaxOpacity, 0f, 1f));

		env.SetFogColors(
			new Vector3(s.FogColorR, s.FogColorG, s.FogColorB),
			new Vector3(s.FogSunColorR, s.FogSunColorG, s.FogSunColorB),
			Math.Clamp(s.FogSunStrength, 0f, 1f),
			Math.Max(s.FogSunSharpness, 0.001f));
	}

	/// <summary>Живые ручки объёмного света (no-op когда он выключен - см.
	/// ModelViewportEnvironment.SetVolumetricParams). Направление солнца сюда не входит по той же
	/// причине, что и у тумана.</summary>
	public static void Volumetric(ModelViewportEnvironment env, EditorSettings s)
	{
		env.SetVolumetricParams(
			Math.Max(s.VolumetricDensity, 0f),
			Math.Max(s.VolumetricHeightFalloff, 0f),
			s.VolumetricHeightRef,
			Math.Max(s.VolumetricStartDistance, 0f),
			Math.Max(s.VolumetricMaxDistance, 1f),
			Math.Clamp(s.VolumetricSteps, 4, 256),
			Math.Clamp(s.VolumetricMaxOpacity, 0f, 1f),
			Math.Clamp(s.VolumetricShadowStrength, 0f, 1f));

		env.SetVolumetricScattering(
			Math.Max(s.VolumetricScattering, 0f),
			Math.Max(s.VolumetricExtinction, 1e-4f),
			Math.Clamp(s.VolumetricAnisotropy, -0.95f, 0.95f));

		env.SetVolumetricColors(
			new Vector3(s.VolumetricSunColorR, s.VolumetricSunColorG, s.VolumetricSunColorB),
			Math.Max(s.VolumetricSunIntensity, 0f),
			new Vector3(s.VolumetricAmbientColorR, s.VolumetricAmbientColorG, s.VolumetricAmbientColorB),
			Math.Max(s.VolumetricAmbientIntensity, 0f),
			Math.Clamp(s.VolumetricAmbientShadowFloor, 0f, 1f));

		env.SetVolumetricPunctualScatter(Math.Max(s.VolumetricPunctualScatter, 0f));
	}

	/// <summary>Бэкенд апскейлера и его тюнинг. Отложенное применение (флаг «настройки менялись»)
	/// остаётся у вьюпорта: это ЕГО состояние, а не настройка.</summary>
	public static void Upscaler(ModelViewportEnvironment env, EditorSettings s)
	{
		env.SetUpscalerBackend(s.TemporalUpscale && s.PreviewMotionVectors
			? Math.Clamp(s.UpscalerBackend, 0, 2)
			: 0);

		env.SetUpscalerTuning(
			Math.Clamp(s.TaauBlendAlpha, 0.02f, 0.5f),
			Math.Clamp(s.FsrSharpness, 0f, 1f),
			new[] { 0, 1, 2, 5 }[Math.Clamp(s.DlssQuality, 0, 3)],
			// Индекс комбо провайдера FSR -> мажор ветки: {Авто, FSR 2, FSR 3.1} = {0, 2, 3}.
			new[] { 0, 2, 3 }[Math.Clamp(s.FsrProvider, 0, 2)]);
	}

	/// <summary>Потолок стороны текстуры в том виде, в каком он уходит в загрузчик. Сравнивать сырую
	/// настройку с заклампленной значило бы вечно видеть расхождение на значениях вне [128, 8192] и
	/// перечитывать сцену каждым нажатием OK.</summary>
	public static int ClampedMaxTextureSize(EditorSettings s) =>
		Math.Clamp(s.PreviewMaxTextureSize, 128, 8192);

	/// <summary>Цвет солнца для бейка проб.</summary>
	public static Vector3 ProbeSunColor(EditorSettings s) =>
		new Vector3(1f, 0.98f, 0.92f) * Math.Clamp(s.ProbeGiSunIntensity, 0.1f, 16f);

	/// <summary>
	/// Опции загрузки модели. RT-тени приходят параметром, а не читаются из настроек: вьюпорты
	/// отвечают на вопрос «доступна ли инлайновая трассировка» по-разному - превью через своё
	/// свойство поддержки, сцена через RayTracing своего устройства.
	/// </summary>
	public static ModelLoadOptions BuildLoadOptions(EditorSettings s, bool rtShadows) => new()
	{
		VertexShader = s.DefaultVertexShader,
		PixelShader = s.DefaultPixelShader,
		OptimizeMesh = false,
		GenerateLods = false,
		AnisotropicFiltering = s.PreviewAnisotropicFiltering,

		// log2 масштаба рендера: при апскейле мипы обязаны выбираться под ПОЛНОЕ разрешение, иначе
		// аккумулятору нечего восстанавливать (см. ModelLoadOptions.MipLodBias). Уровня загрузки,
		// как анизотропия: смена масштаба подхватится следующей загрузкой модели.
		MipLodBias = MathF.Log2(Math.Clamp(s.RenderScale, 0.25f, 1f)),

		// Потолок текстуры - ПИК памяти запечки и заливки: сцена тянет на порядок больше текстур,
		// чем одиночная модель. Пока опции строились двумя копиями, в Scene View эта ручка какое-то
		// время не действовала вовсе - её просто забыли в одной из них.
		MaxTextureSize = ClampedMaxTextureSize(s),
		RtShadows = rtShadows,

		// Кейворд записи G-buffer-а отражений - у окружения он теперь безусловный
		// (см. ModelViewportEnvironment).
		ReflectionGbuffer = true,

		// Текстуры не декодируются в фоновой фазе загрузки вовсе - они приезжают из стола по
		// приоритету от камеры (см. ModelStore). В кадр модель попадает уже с ними: показ ждёт
		// ModelStore.ModelTexturesReady, иначе она появлялась бы на 1x1-филлерах и домигивала
		// текстуры десятки кадров.
		StreamTextures = true
	};
}
