using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Graphics.ProbeGi;

/// <summary>GPU-сторона probe-GI: четыре атласа + привязка к материалам модели и данные для
/// PreviewSettings-кбуфера (см. PreviewSettingsData.ProbeGrid*). Владеет текстурами.</summary>
public sealed class ProbeGiTextures : IReleaseObject
{
	public IGpuTexture Sh0 { get; }
	public IGpuTexture Sh1 { get; }
	public IGpuTexture Sh2 { get; }
	public IGpuTexture Sh3 { get; }

	/// <summary>Окто-атлас видимости (DDGI depth, см. ProbeGiBakeResult.Vis).</summary>
	public IGpuTexture Vis { get; }

	/// <summary>Атлас релокации: смещение пробы от узла сетки (см.
	/// ProbeGiBakeResult.Offset).</summary>
	public IGpuTexture Offset { get; }

	/// <summary>Угол объёма в мире. НЕ константа: прокручиваемый объём ездит за камерой, и материалы
	/// читают его отсюда каждый кадр (см. ProbeGiViewportShared.PushGrid).</summary>
	public Vector4 GridOrigin { get; private set; }

	public Vector4 GridCell { get; }
	public Vector4 GridCounts { get; }

	/// <summary>Минимальный из шагов сетки - база для normal-бейаса сэмпла (см. GridCell.w).</summary>
	public float MinCellSize { get; }

	private readonly IGraphicsApi _api;

	/// <summary>Атласы заведены с UAV - их пишет compute-раунд напрямую (см. ProbeRoundCS.hlsl), и
	/// <see cref="Update"/> в этом режиме не нужен.</summary>
	public bool GpuWritable { get; }

	public ProbeGiTextures(IGraphicsApi api, ProbeGiBakeResult result, string namePrefix,
		bool gpuWritable = false)
	{
		_api = api;
		GpuWritable = gpuWritable;

		// Размер атласов задаёт сама сетка: ширина - ось X, высота - плоскости Z столбиком (см.
		// ProbeGiBakeResult.ShWidth).
		int width = result.ShWidth;
		int height = result.ShHeight;

		// Изменяемые, а не Immutable: прогрессивный бейк перезаливает атласы каждый раунд (см.
		// Update) - пересоздавать текстуры и переприязывать их к материалам столько раз нельзя.
		Sh0 = api.CreateTexture2DMutable($"{namePrefix} ProbeSH0", width, height, true, gpuWritable);
		Sh1 = api.CreateTexture2DMutable($"{namePrefix} ProbeSH1", width, height, true, gpuWritable);
		Sh2 = api.CreateTexture2DMutable($"{namePrefix} ProbeSH2", width, height, true, gpuWritable);
		Sh3 = api.CreateTexture2DMutable($"{namePrefix} ProbeSH3", width, height, true, gpuWritable);
		Vis = api.CreateTexture2DMutable($"{namePrefix} ProbeVis",
			width * ProbeGiBakeResult.VisRes, height * ProbeGiBakeResult.VisRes, true, gpuWritable);
		Offset = api.CreateTexture2DMutable($"{namePrefix} ProbeOffset", width, height, true, gpuWritable);

		GridOrigin = new Vector4(result.Origin, 1f);

		// w = normal-бейас сэмпла в мировых единицах (доля ячейки, дефолт 0.3) - от утечек через
		// тонкие стены. Вьюпорт может переопределить его из настроек через MinCellSize.
		var cell = result.Cell;
		MinCellSize = MathF.Min(cell.X, MathF.Min(cell.Y, cell.Z));
		GridCell = new Vector4(cell, MinCellSize * 0.3f);
		GridCounts = new Vector4(result.CountX, result.CountY, result.CountZ, 0f);

		// В GPU-режиме атласы заполнит первый же раунд - заливать нечего.
		if (!gpuWritable)
		{
			// После GridCounts - Update сверяется с ними (см. Matches).
			Update(result);
		}
	}

	/// <summary>Заливает свежий снимок бейка в уже созданные атласы - привязки материалов и
	/// параметры сетки при этом не трогаются, так что зов дёшев и не требует Flush+WaitForIdle.
	/// Сетка обязана совпадать с той, под которую текстуры создавались (см. <see cref="Matches"/>);
	/// сменилась сетка - пересоздавайте объект.</summary>
	public void Update(ProbeGiBakeResult result)
	{
		if (!Matches(result))
		{
			throw new ArgumentException("Probe grid size does not match the allocated atlases", nameof(result));
		}

		_api.UpdateTexture2D(Sh0, result.Sh0);
		_api.UpdateTexture2D(Sh1, result.Sh1);
		_api.UpdateTexture2D(Sh2, result.Sh2);
		_api.UpdateTexture2D(Sh3, result.Sh3);
		_api.UpdateTexture2D(Vis, result.Vis);
		_api.UpdateTexture2D(Offset, result.Offset);
	}

	/// <summary>Та же сетка, что у выделенных атласов - можно обновлять на месте.</summary>
	public bool Matches(ProbeGiBakeResult result) =>
		result.CountX == (int)GridCounts.X
		&& result.CountY == (int)GridCounts.Y
		&& result.CountZ == (int)GridCounts.Z;

	/// <summary>Привязывает атласы ко всем материалам модели (шейдер читает их через Load -
	/// сэмплер не нужен). slotSuffix - "" для базового объёма, "_C1"/"_C2" для мелких каскадов
	/// (см. SampleProbeGi в UnlitInstancedPS.hlsl).</summary>
	public void Bind(ModelLoader model, string slotSuffix = "")
	{
		for (int i = 0; i < model.materialObjects.Count; i++)
		{
			var material = model.materialObjects.GetAt(i).Value;
			material.SetTexture($"_ProbeSh0{slotSuffix}", Sh0);
			material.SetTexture($"_ProbeSh1{slotSuffix}", Sh1);
			material.SetTexture($"_ProbeSh2{slotSuffix}", Sh2);
			material.SetTexture($"_ProbeSh3{slotSuffix}", Sh3);
			material.SetTexture($"_ProbeVis{slotSuffix}", Vis);
			material.SetTexture($"_ProbeOffset{slotSuffix}", Offset);
		}
	}

	public void Release()
	{
		Sh0.Release();
		Sh1.Release();
		Sh2.Release();
		Sh3.Release();
		Vis.Release();
		Offset.Release();
	}
}
