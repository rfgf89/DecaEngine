using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using SharpGLTF.Schema2;
using DecaEngine.Core;
using DecaEngine.Graphics.Assets;
using DecaEngine.Graphics.Core;
using System.Runtime.InteropServices;
using Diligent;
using MeshOptimizer;
using StbImageSharp;
using UnsafeCollections.Collections.Unsafe;
using DecaEngine.Animation;

namespace DecaEngine.Graphics;

public class ModelLoader
{
	public List<InstanceData> instances = new();

	/// <summary>Разбивка времени загрузки и объём декодированных текстур - диагностика.
	/// Фазы стоят очень по-разному на разных ассетах, и «очевидный» виновник обычно не тот:
	/// без этих чисел оптимизация загрузки - гадание.</summary>
	public readonly record struct LoadTimings(long ParseMs, long DecodeMs, long MaterialsMs, long MeshesMs,
		long FinalizeMs, int DecodedImages, long DecodedBytes, int ShaderVariants, long ShaderMs,
		int TextureUploads, long TextureMs, int MeshUploads, long MeshMs, int Samplers, long SamplerMs, int MaterialsBuilt, long MaterialBuildMs, long MatCreateMs, long MatShaderMs);

	public LoadTimings Timings { get; internal set; }

	// Компиляция вариантов пиксельного шейдера внутри финализации - накапливается там же
	// (см. BuildFromPreparedIncremental.GetPixelShaderVariant).
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

	/// <summary>Освобождает GPU-ресурсы модели: меши (вершинные/индексные буферы плюс их CPU-копии
	/// в неуправляемой памяти) и материалы (PSO, SRB, кбуферы, шейдеры, текстуры).
	///
	/// Раньше этого не было вовсе - <see cref="ModelLoader"/> не был освобождаемым, и каждая
	/// открытая модель оставляла на GPU весь свой footprint навсегда.
	///
	/// Про шейдеры отдельно, потому что рядом в коде есть предупреждение о двойном освобождении:
	/// один вершинный шейдер и горстка вариантов пиксельного ШАРЯТСЯ между материалами модели, и
	/// <see cref="IMaterialObject.Release"/> освобождает их у каждого. Здесь это безопасно по двум
	/// причинам: DiligentShader.Release нуллит нативный объект и повторный вызов на нём - no-op, а
	/// кэш вариантов локален для ОДНОЙ загрузки (см. BuildFromPreparedIncremental), так что чужой
	/// модели эти шейдеры не принадлежат. Опасен был другой сценарий - освобождение шейдера, пока им
	/// пользуется ЖИВОЙ материал; здесь же умирает весь набор разом.
	///
	/// Вызывающий обязан сперва снять все инстансы со сцены и дождаться GPU: на буферы модели
	/// ссылаются записанные команды рендер-графа (см. ModelPreviewViewport.PopulateFromScene).</summary>
	/// <summary>Шейдеры, созданные загрузкой этой модели: один вершинный, варианты пиксельного и
	/// (при не-треугольных топологиях) точечный вершинный. ШАРЯТСЯ между материалами модели, поэтому
	/// материалы их не освобождают (OwnsShaders=false) - освобождает их отсюда, по одному разу.</summary>
	internal readonly List<IShaderObject> _ownedShaders = new();

	public void Release()
	{
		foreach (var mesh in Meshes)
		{
			mesh?.Release();
		}

		Meshes.Clear();
		MeshHasUv.Clear();

		// По РАЗЛИЧНЫМ объектам, а не по значениям словаря: дефолтный материал раздаётся ВСЕМ
		// null-материалам модели, то есть лежит в materialObjects под несколькими ключами. Простой
		// проход по Values освободил бы его столько же раз, а повторный Dispose нативного SRB/PSO -
		// это обращение к освобождённой памяти, ровно как с шарёными шейдерами.
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

		// Текстуры - ПОСЛЕ материалов: их SRB держали вьюхи текстур; сами материалы текстур не
		// освобождают (см. DiligentMaterial.Release), владение здесь.
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

		// ПОСЛЕ материалов: они держат нативные шейдеры, и освобождать шейдер, пока жив
		// использующий его материал, - та же ошибка, только с другой стороны.
		foreach (var shader in _ownedShaders)
		{
			shader?.Release();
		}

		_ownedShaders.Clear();
		instances.Clear();
	}

	public List<IMeshObject> Meshes = new();

	/// <summary>
	/// Parallel to <see cref="Meshes"/>: whether the glTF primitive that became Meshes[i] had a real
	/// TEXCOORD_0 accessor, as opposed to synthesized all-zero UVs (see PrepareModel). Used to gate the
	/// Tangent channel option in <see cref="DecaEngine.Editor.ModelPreviewViewport"/>'s Channel debug
	/// view - a derivative-based tangent computed from degenerate (0,0) UVs is meaningless.
	/// </summary>
	public List<bool> MeshHasUv = new();

	/// <summary>Скелет модели, null у статической (см. <see cref="PreparedSkeleton"/>). Общий на всю
	/// модель, живёт на CPU: скиннинг-палитра считается по нему покадрово, а процедурный слой (IK,
	/// рэгдолл, spring bones) правит позу до того, как она уедет в GPU.</summary>
	public PreparedSkeleton Skeleton;

	/// <summary>Клипы модели, разложенные по джойнтам <see cref="Skeleton"/>.</summary>
	public List<PreparedAnimation> Animations = new();

	/// <summary>Параллельно <see cref="Meshes"/>: скин-стрим меша (null у статических). Держится на
	/// CPU до сборки GPU-буфера скиннинга - см. <see cref="SkinVertex"/>.</summary>
	public List<SkinVertex[]> MeshSkin = new();

	/// <summary>Линейное альбедо каждого треугольника меша (ключ - meshId), из base color текстур -
	/// см. <see cref="ComputeTriangleAlbedoFromTextures"/>. Пусто у мешей без текстуры/UV/пикселей.</summary>
	public Dictionary<int, Vector3[]> TriangleAlbedo { get; } = new();

	/// <summary>Металличность каждого треугольника меша (ключ - meshId): B-канал metallic-roughness
	/// текстуры в центроиде UV x MetallicFactor. Потребитель - «зеркало в зеркале» RT-отражений
	/// (детект металла у TLAS-хита, см. SceneTrace.hlsl): по одному лишь альбедо светлый хром
	/// (серебро/золото) неотличим от белой штукатурки. Строится тем же проходом, что
	/// <see cref="TriangleAlbedo"/>, только при живых CPU-пикселях MR-текстуры; без неё потребитель
	/// падает на MetallicFactor материала.</summary>
	public Dictionary<int, float[]> TriangleMetalness { get; } = new();

	/// <summary>Шероховатость каждого треугольника меша (ключ - meshId): G-канал
	/// metallic-roughness текстуры в центроиде UV x RoughnessFactor. Потребитель тот же, что у
	/// <see cref="TriangleMetalness"/>: RT-отражения (насколько резко металлический хит отражает
	/// дальше - без неё зеркальный хром и матовое железо шейдились одинаково размыто). Без
	/// CPU-пикселей MR-текстуры словарь пуст - фолбэк на RoughnessFactor материала.</summary>
	public Dictionary<int, float[]> TriangleRoughness { get; } = new();

	/// <summary>Сторона плитки <see cref="MaterialAlbedoTile"/>.</summary>
	public const int AlbedoTileSize = 128;

	/// <summary>Даунсемпленная до <see cref="AlbedoTileSize"/>² плитка base color текстуры
	/// материала (ключ - materialId): RGBA8 в sRGB, усреднение в ЛИНЕЙНОМ пространстве (среднее по
	/// sRGB темнит контрастные текстуры), БЕЗ BaseColorFactor - его умножает потребитель. Источник
	/// слоёв атласа текстур RT-хитов (дешёвый режим, см. SsrHitTextures в редакторе). Как и
	/// <see cref="TriangleAlbedo"/>, строится только пока живы CPU-пиксели: у стриминга и
	/// cooked-моделей словарь пуст, потребитель падает на плитку среднего цвета материала.</summary>
	public Dictionary<int, byte[]> MaterialAlbedoTile { get; } = new();

	public OrderedDictionary<int, IMaterialObject> materialObjects = new();

	/// <summary>Одна стримимая текстура модели (см. <see cref="ModelLoadOptions.StreamTextures"/>):
	/// текущая GPU-текстура (первая ступень - низкое качество), сжатый исходник для ре-декода и все
	/// слоты материалов, куда она привязана (один image часто шарится каналами/материалами - ORM).
	/// Апгрейды делает DecaEngine.Editor.ECS.ModelStreamer: фоновый декод следующей ступени ->
	/// CreateTexture -> SetTexture по всем привязкам (SRB живого материала обновляется на месте, см.
	/// DiligentMaterial.SetTexture - тот же приём, что у ProbeGiTextures.Bind) -> отложенный Release
	/// старой. По достижении целевого/нативного размера <see cref="EncodedPixels"/> обнуляется -
	/// CPU-данные освобождаются.</summary>
	public sealed class StreamedTexture
	{
		/// <summary>Путь к внешнему файлу картинки (ре-декод читает его с диска в фоне) - null для
		/// встроенных, у них исходник лежит в <see cref="EncodedPixels"/>.</summary>
		public string FilePath;

		/// <summary>Сжатый исходник встроенной картинки (.glb / data-URI).</summary>
		public byte[] EncodedPixels;

		/// <summary>Есть ли ещё откуда брать ступени (иначе стриминг этой текстуры окончен).</summary>
		public bool HasSource => !Completed && (FilePath != null || EncodedPixels != null || DtexPath != null);

		/// <summary>Читает сжатый исходник. Дисковый I/O - звать только из фонового потока.</summary>
		public byte[] ReadEncoded() => EncodedPixels ?? (FilePath != null ? File.ReadAllBytes(FilePath) : null);

		/// <summary>Освобождает CPU-данные исходника (стриминг завершён).</summary>
		public void ReleaseCpuData()
		{
			Completed = true;
			EncodedPixels = null;
			FilePath = null;
			DtexPath = null;
		}

		/// <summary>Бо́льшая сторона текущего GPU-декода (0 = ещё 1x1-филлер).</summary>
		public int CurrentSize;

		/// <summary>Целевая сторона (<see cref="ModelLoadOptions.MaxTextureSize"/>; 0 = нативное
		/// разрешение файла).</summary>
		public int TargetSize;

		public bool Completed;

		/// <summary>Авторские настройки сэмплера glTF - апгрейд создаёт текстуру, сэмплер уже
		/// привязан к слоту как immutable и не меняется.</summary>
		public TextureAddress AddressMode;
		public TextureFilter FilterMode;

		/// <summary>Текущая GPU-текстура (шарится всеми привязками); null = слот ещё на 1x1-филлере.
		/// Финальную освобождает <see cref="ModelLoader.Release"/>, промежуточные - стример
		/// отложенной очередью.</summary>
		public IGpuTexture Texture;

		public readonly List<(IMaterialObject Material, string Slot)> Bindings = new();

		/// <summary>Запечённая .dtex этой текстуры (см. DecaEngine.Graphics.Assets.DtexFile) - источник
		/// ступеней, когда модель пришла из кеша ассетов. Ступень здесь не декодируется вовсе: она
		/// ЧИТАЕТСЯ хвостом мип-цепочки прямо с диска и уезжает в VRAM теми же байтами.</summary>
		public string DtexPath;

		/// <summary>Размеры нулевого уровня <see cref="DtexPath"/> - из cooked-модели, чтобы перевести
		/// запрошенную сторону в номер мип-уровня без открытия файла.</summary>
		public int DtexWidth;
		public int DtexHeight;

		/// <summary>Ступени этой текстуры блочно-сжатые - она занимает вчетверо меньше VRAM, чем
		/// RGBA8 той же стороны. Отдельным полем от <see cref="DtexPath"/> именно потому, что путь
		/// обнуляется по завершении стриминга (<see cref="ReleaseCpuData"/>), а учёт занятой памяти
		/// живёт дальше - см. ModelStore.EstimateTextureBytes.</summary>
		public bool IsBlockCompressed;
	}

	/// <summary>
	/// Одна ступень качества стримимой текстуры - то, что фоновая задача подготовила, а заливка
	/// отдаёт графическому API.
	///
	/// Два источника с общим интерфейсом: несжатые RGBA8-пиксели (декод PNG/JPG - путь без кеша
	/// ассетов) и готовый хвост BC-цепочки из .dtex (путь с кешем). Различие не размазано по
	/// стримеру: он оперирует ступенями, а чем ступень является внутри - знает только она сама.
	/// </summary>
	public sealed class StreamedTextureLevel
	{
		/// <summary>Данные уровней: ровно один элемент для RGBA8 (мипы достроит GPU), хвост цепочки
		/// от этой ступени и ниже - для блочно-сжатых (их GPU достроить не может).</summary>
		public required byte[][] Mips { get; init; }

		/// <summary>Формат блочно-сжатых данных; <see cref="TextureObjectFormat.Unknown"/> - RGBA8.</summary>
		public required TextureObjectFormat Format { get; init; }

		public required int Width { get; init; }
		public required int Height { get; init; }

		/// <summary>Бо́льшая сторона - ею стример меряет качество ступени.</summary>
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

	/// <summary>Что привязано в слот _MainTex материала - текстура, её (immutable) сэмплер и запись
	/// стриминга, если текстура приезжает ступенями.
	///
	/// Существует ради ТЕНЕВОГО материала с альфа-тестом (см. ShadowRenderer.RegisterAlphaTestedMaterial):
	/// теневой пасс - отдельный PSO со своим SRB, и чтобы вырезать листву по альфе, ему нужна ровно
	/// та же текстура и тот же сэмплер, что и экранному материалу. Пересоздавать их для тени
	/// значило бы удвоить память на каждую крону.</summary>
	public sealed class BaseColorBinding
	{
		public IGpuTexture Texture;
		public ISamplerObject Sampler;

		/// <summary>Запись стриминга (null - текстура загружена целиком): подписавшись на неё,
		/// теневой материал получает те же ступени качества, что и экранный.</summary>
		public StreamedTexture Stream;
	}

	/// <summary>Привязки _MainTex по ключу материала (тому же, что у <see cref="materialObjects"/> и
	/// <see cref="MaterialPbr"/>). Материалы без базовой текстуры сюда не попадают.</summary>
	public readonly Dictionary<int, BaseColorBinding> MaterialBaseColor = new();

	/// <summary>Обобщение <see cref="MaterialBaseColor"/> на ВСЕ слоты (не только _MainTex): по ключу
	/// материала (как у <see cref="materialObjects"/>/<see cref="MaterialPbr"/>) - словарь "имя слота
	/// -&gt; привязка" для каждого слота, куда был привязан РЕАЛЬНЫЙ (не филлер) ресурс. Слоты, ушедшие
	/// на филлер (нет текстуры в glTF), сюда не попадают - их состояние детерминированно выводится из
	/// <see cref="MaterialPbr"/> (Has*Texture/TransmissionFactor).
	///
	/// Существует ради <see cref="BuildAdditionalMaterialSet"/>: второй (третий, ...) набор материалов
	/// для ДРУГОГО окружения строится из уже загруженных GPU-текстур этой модели без повторной
	/// декодировки/заливки - см. класс-комментарий у <see cref="DecaEngine.Editor.ECS.ModelStore"/> о
	/// том, почему материалы (в отличие от текстур/мешей) НЕ шарятся между окружениями напрямую.</summary>
	public readonly Dictionary<int, Dictionary<string, BaseColorBinding>> MaterialTextureBindings = new();

	/// <summary>Общие 1x1-филлеры модели (белый и плоская нормаль) плюс их сэмплер - см. локальные
	/// EnsureFallbackTextures/BindFallbackTexture/BindFlatNormalFallback в
	/// <see cref="BuildFromPreparedIncremental"/>, которые их лениво создают и заполняют эти поля.
	/// Освобождаются как обычные <see cref="_ownedTextures"/> в <see cref="Release"/>; хранятся здесь
	/// отдельно только чтобы <see cref="BuildAdditionalMaterialSet"/> могло переиспользовать те же
	/// объекты вместо создания собственных копий на каждое окружение.</summary>
	internal IGpuTexture FallbackWhiteTexture;
	internal ISamplerObject FallbackSampler;
	internal IGpuTexture FallbackFlatNormalTexture;

	/// <summary>Стримимые текстуры модели; пусто без <see cref="ModelLoadOptions.StreamTextures"/>.</summary>
	public readonly List<StreamedTexture> StreamedTextures = new();

	/// <summary>Не-стримимые GPU-текстуры загрузки (полноразмерные + 1x1-филлеры): материалы текстур
	/// не освобождают (см. DiligentMaterial.Release), владение и Release - здесь. Раньше они не
	/// хранились нигде и утекали навсегда.</summary>
	internal readonly List<IGpuTexture> _ownedTextures = new();

	// Коды топологии меша (MaterialPbrFactors.Topology / PreparedMesh.Topology): точечные и
	// линейные glTF-примитивы рисуются клоном материала с PSO соответствующей топологии.
	public const int MeshTopologyTriangles = 0;
	public const int MeshTopologyLineList = 1;
	public const int MeshTopologyLineStrip = 2;
	public const int MeshTopologyPoints = 3;

	/// <summary>Синтетический ключ материала-клона для не-треугольной топологии - не пересекается с
	/// логическими индексами glTF-материалов (-1..N).</summary>
	public static int MakeTopologyMaterialKey(int topology, int materialIndex) => 10000 * topology + materialIndex + 1;

	/// <summary>
	/// PBR metallic-roughness factors per material, keyed like <see cref="materialObjects"/> (glTF
	/// logical material index, plus -1 for the built-in default material). Consumed by the editor's
	/// Model Preview Lighting mode (see DecaEngine.Editor.ModelPreviewViewport / UnlitInstancedPS.hlsl
	/// PreviewMode == 3) - the engine has no PBR scene shading yet, so nothing else reads these.
	/// </summary>
	public Dictionary<int, MaterialPbrFactors> MaterialPbr = new();

	/// <summary>
	/// ????????? ????? (world-space) AABB ???? ?????, ????????? bounding-????? (<see
	/// cref="IMeshObject.Center"/>/<see cref="IMeshObject.Radius"/>, ??. <see
	/// cref="MeshUtility.RecalculateBounds"/>) ??????? <see cref="InstanceData"/>, ??????????????????
	/// ??? <see cref="Transform"/>. ?????? ????? ?????? ??????? ?? ?????????? ???????? (??????????
	/// glTF-????/?????) - ??????? ???????????? bound ??????? ???? ?????????? ????????????, ?????
	/// ?????????? bounds ???? ?????????/???????? ?????, ????? ????? ?????? ????? ???????? ??????
	/// ?????? ??? ???????? ?????? ??????. ?????????? (Vector3.Zero, Vector3.Zero), ???? ? ????? ???
	/// ?? ?????? ????????? ????????.
	/// </summary>
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

			// ??????? ?????? ????????????????? ???????: Scale -> Rotate -> Translate
			var matrix = Matrix4x4.CreateScale(t.scale.X, t.scale.Y, t.scale.Z) *
						 Matrix4x4.CreateFromQuaternion(t.rotation) *
						 Matrix4x4.CreateTranslation(t.position);

			// ?????????????? ????????? ????? ? world-space
			var worldCenter = Vector3.Transform(mesh.Center, matrix);

			// ??? ??????? ?????????? ???????????? ????????? scale (?????????????? ??????)
			var worldRadius = mesh.Radius * MathF.Max(MathF.Abs(t.scale.X),
				MathF.Max(MathF.Abs(t.scale.Y), MathF.Abs(t.scale.Z)));

			// ????????? - ?????????? ???????? ? NaN ??? Infinity ? bounds
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

	private static TextureAddress ToAddressMode(TextureWrapMode wrapMode)
	{
		return wrapMode switch
		{
			TextureWrapMode.CLAMP_TO_EDGE => TextureAddress.Clamp,
			TextureWrapMode.MIRRORED_REPEAT => TextureAddress.Mirror,
			TextureWrapMode.REPEAT => TextureAddress.Wrap,
			_ => TextureAddress.Wrap
		};
	}

	private static TextureFilter ToFilter(TextureMipMapFilter minFilter, TextureInterpolationFilter magFilter)
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

	/// <summary>
	/// Path to a lightweight placeholder model bundled at EditorAssets/models/result.gltf, used when
	/// no other model has been selected (the full Sponza.gltf scene this once stood in for isn't
	/// shipped in this repo - large external asset).
	/// </summary>
	public const string DefaultModelPath = "EditorAssets/models/result.gltf";

	private ModelLoader()
	{
	}

	/// <summary>
	/// Kicks off loading a .gltf/.glb file from <paramref name="modelPath"/> (absolute or relative to
	/// <see cref="Environment.CurrentDirectory"/>) in the background: file I/O, glTF parsing, texture
	/// decoding and mesh optimization/LOD generation (all pure-CPU work) run on a thread-pool thread via
	/// <see cref="Task.Run(Action)"/>. GPU resource creation (shaders/materials/textures/meshes) cannot
	/// safely happen off the main thread (Diligent's immediate device context isn't thread-safe - see
	/// DiligentGraphicsApi.CreateTexture), so it's deferred to <see cref="ModelLoadRequest.FinalizeOnMainThread"/>,
	/// which the caller must invoke from the same thread that owns <paramref name="graphicsApi"/> once
	/// the request is ready. <paramref name="progress"/>, if given, receives 0..1 completion updates from
	/// the background thread. Used both by the main editor scene (<see
	/// cref="DecaEngine.Editor.EditorManager"/>) and <see cref="DecaEngine.Editor.ModelPreviewViewport"/>'s
	/// lightweight Asset Browser preview.
	/// </summary>
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

	/// <summary>
	/// Synchronously loads and finalizes a model - equivalent to <see cref="BeginLoadAsync"/> followed by
	/// blocking until ready and calling <see cref="ModelLoadRequest.FinalizeOnMainThread"/>. Blocks the
	/// calling thread for the entire load; prefer <see cref="BeginLoadAsync"/> in the editor so the UI
	/// stays responsive and a progress indicator can be shown.
	/// </summary>
	public static ModelLoader Load(IGraphicsApi graphicsApi, string modelPath, ModelLoadOptions options)
	{
		var request = BeginLoadAsync(graphicsApi, modelPath, options);
		request.PrepareTask.GetAwaiter().GetResult();
		return request.FinalizeOnMainThread();
	}

	/// <summary>Подготовка МИМО кеша - вход для фоновой печки (см. <see cref="AssetBakeQueue"/>).
	/// Рекурсии не даёт сам вызывающий: он снимает CacheDirectory в переданных опциях.</summary>
	internal static PreparedModel PrepareForBake(string modelPath, ModelLoadOptions options,
		CancellationToken cancellationToken) => PrepareModel(modelPath, options, null, cancellationToken);

	private static PreparedModel PrepareModel(string modelPath, ModelLoadOptions options,
		IProgress<float> progress, CancellationToken cancellationToken)
	{
		// Ассет-пайплайн. При ПОПАДАНИИ всё, что ниже, не выполняется вовсе: ни разбора glTF, ни
		// декода картинок, ни meshopt, ни упрощения под LOD - только чтение линейного .dmdl. Именно
		// эти четыре фазы и составляют почти всё время загрузки, и все они - чистые функции от
		// исходника и опций, то есть считать их заново при каждом открытии сцены незачем.
		var cache = options.Cache;
		if (cache != null)
		{
			var modelKey = AssetCache.ModelKey(modelPath, options.CookSignature());
			var cooked = CookedModelFile.TryRead(cache.ModelPath(modelKey));

			if (cooked != null && ModelAssetBaker.AllTexturesPresent(cooked, cache))
			{
				progress?.Report(1f);
				return cooked;
			}

			// Промах. Загрузка НЕ ждёт печку и идёт дальше обычным путём - включение пайплайна не
			// имеет права сделать первое открытие модели медленнее, чем оно было без него.
			AssetBakeQueue.Enqueue(modelPath, options, modelKey);
		}

		// Строгая валидация SharpGLTF на больших сценах заметно небесплатна; TryFix заодно чинит
		// мелкие огрехи экспортёров вместо жёсткого отказа.
		var swPhase = System.Diagnostics.Stopwatch.StartNew();
		var model = LoadModelRoot(modelPath, options, out var externalImagePaths);
		cancellationToken.ThrowIfCancellationRequested();

		var prepared = new PreparedModel();
		prepared.MsParse = swPhase.ElapsedMilliseconds;
		swPhase.Restart();

		// Картинки, на которые реально ссылаются декодируемые ниже каналы материалов. Декод (PNG/JPG +
		// даунскейл) - самая дорогая CPU-фаза загрузки: параллелится по уникальным image, материалы
		// ниже берут готовые пиксели из кэша. Кэш заодно убирает повторный декод одного image,
		// разделяемого несколькими материалами/каналами (типовая ORM-текстура: у MetallicRoughness и
		// Occlusion один и тот же image - раньше он декодировался дважды).
		var usedImages = new List<SharpGLTF.Schema2.Image>();
		{
			var seenImages = new HashSet<SharpGLTF.Schema2.Image>();
			void AddImage(SharpGLTF.Schema2.Texture texture)
			{
				if (texture?.PrimaryImage != null && seenImages.Add(texture.PrimaryImage))
				{
					usedImages.Add(texture.PrimaryImage);
				}
			}

			foreach (var logicalMaterial in model.LogicalMaterials)
			{
				if (logicalMaterial == null)
				{
					continue;
				}

				AddImage(logicalMaterial.GetDiffuseTexture());
				AddImage(logicalMaterial.FindChannel("MetallicRoughness")?.Texture);
				AddImage(logicalMaterial.FindChannel("VolumeThickness")?.Texture);
				AddImage(logicalMaterial.FindChannel("Occlusion")?.Texture);
				AddImage(logicalMaterial.FindChannel("Normal")?.Texture);
			}
		}

		// Стриминг текстур: в фоновой фазе НЕ ДЕКОДИРУЕТСЯ НИ ОДНА картинка. Декод (PNG/JPG +
		// даунскейл) - самая дорогая CPU-фаза загрузки и главный вкладчик в пиковую память, и именно
		// он раньше держал сцену пустой всё время загрузки. Материалы строятся сразу с 1x1-филлерами
		// (кейворды шейдера при этом ТЕ ЖЕ - они ставятся по наличию текстуры в glTF, а не по
		// наличию пикселей, так что апгрейд не трогает PSO), геометрия появляется почти сразу, а
		// пиксели приезжают ступенями из ModelStreamer.
		//
		// Источник ре-декода - ПУТЬ к файлу картинки, если она внешняя (типовая .gltf-сцена вроде
		// Sponza: папка с PNG рядом), и только для встроенных (.glb / data-URI) копируются байты.
		// Иначе сотни 4K-исходников Sponza жили бы в managed-памяти всё время сессии.
		int decodeMaxSize = options.MaxTextureSize;
		Dictionary<SharpGLTF.Schema2.Image, TextureStreamSource> streamSources = null;
		if (options.StreamTextures)
		{
			decodeMaxSize = 0; // ничего не декодируем в этой фазе
			streamSources = new Dictionary<SharpGLTF.Schema2.Image, TextureStreamSource>();
			foreach (var image in usedImages)
			{
				streamSources[image] = CreateStreamSource(image, externalImagePaths);
			}
		}

		var decodedImages = new Dictionary<SharpGLTF.Schema2.Image, (byte[] Pixels, int Width, int Height)>();
		if (usedImages.Count > 0 && !options.StreamTextures)
		{
			var decodedResults = new (byte[] Pixels, int Width, int Height)[usedImages.Count];
			int imagesDone = 0;

			// Параллелизм ОГРАНИЧЕН, и это не про загрузку CPU, а про ПАМЯТЬ. Декод идёт в полном
			// разрешении файла и только потом ужимается до MaxTextureSize (stb иначе не умеет), то
			// есть каждый поток держит в пике полноразмерную RGBA-копию: для 4K это 64 МБ. Без
			// ограничения Parallel.For берёт по потоку на ядро, и на 16-32-поточной машине это
			// 1-2 ГБ ОДНИХ ТОЛЬКО промежуточных буферов - поверх того, что уже накоплено
			// декодированным (см. ниже: decodedResults держит ВСЕ картинки до конца фазы).
			//
			// Четыре потока сохраняют почти всю выгоду распараллеливания (декод упирается в память,
			// а не в ALU) и срезают этот пик до сотен мегабайт.
			var decodeOptions = new ParallelOptions
			{
				CancellationToken = cancellationToken,
				MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount),
			};

			Parallel.For(0, usedImages.Count, decodeOptions, i =>
			{
				decodedResults[i] = DecodeImagePixels(usedImages[i], decodeMaxSize);
				progress?.Report(0.05f + 0.30f * (Interlocked.Increment(ref imagesDone) / (float)usedImages.Count));
			});

			for (int i = 0; i < usedImages.Count; i++)
			{
				decodedImages[usedImages[i]] = decodedResults[i];
				prepared.DecodedBytes += decodedResults[i].Pixels?.LongLength ?? 0;
			}

			prepared.DecodedImages = usedImages.Count;
		}

		prepared.MsDecode = swPhase.ElapsedMilliseconds;
		swPhase.Restart();

		// Weight the big background phases (texture decode above, then materials and meshes) roughly
		// by count so the progress bar moves at a believable pace instead of jumping straight to 50%.
		int materialCount = Math.Max(1, model.LogicalMaterials.Count);

		for (var index = 0; index < model.LogicalMaterials.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var logicalMaterial = model.LogicalMaterials[index];

			if (logicalMaterial == null)
			{
				prepared.Materials.Add(new PreparedMaterial { LogicalIndex = index, IsNull = true });
				progress?.Report(0.35f + 0.05f * ((index + 1) / (float)materialCount));
				continue;
			}

			var preparedMaterial = new PreparedMaterial
			{
				LogicalIndex = index,
				Name = logicalMaterial.Name ?? $"Material_{index}"
			};

			var baseColorTexture = logicalMaterial.GetDiffuseTexture();
			if (baseColorTexture?.PrimaryImage != null)
			{
				preparedMaterial.BaseColorTexture = DecodeTexture(baseColorTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
			}

			// PBR metallic-roughness scalars for the editor's Lighting preview (see MaterialPbr).
			// PreparedMaterial's field initializers already hold the glTF spec defaults (white/1/1),
			// so only explicitly-authored channel parameters are read here.
			var baseColorChannel = logicalMaterial.FindChannel("BaseColor");
			if (baseColorChannel.HasValue)
			{
				foreach (var parameter in baseColorChannel.Value.Parameters)
				{
					if (parameter.Name == "RGBA" && parameter.Value is Vector4 rgba)
					{
						preparedMaterial.BaseColorFactor = rgba;
					}
				}
			}

			preparedMaterial.AlphaCutoff = logicalMaterial.Alpha switch
			{
				AlphaMode.MASK => logicalMaterial.AlphaCutoff,
				AlphaMode.BLEND => 0.5f,
				_ => 0f,
			};

			// Сам режим - ОТДЕЛЬНЫМ полем: порог выше его теряет (см. PreparedMaterial.AlphaMode).
			preparedMaterial.AlphaMode = logicalMaterial.Alpha switch
			{
				AlphaMode.MASK => MaterialAlphaMode.Mask,
				AlphaMode.BLEND => MaterialAlphaMode.Blend,
				_ => MaterialAlphaMode.Opaque,
			};

			bool metallicAuthored = false;
			bool roughnessAuthored = false;

			var metallicRoughnessChannel = logicalMaterial.FindChannel("MetallicRoughness");
			if (metallicRoughnessChannel.HasValue)
			{
				var channel = metallicRoughnessChannel.Value;

				foreach (var parameter in channel.Parameters)
				{
					if (parameter.Name == "MetallicFactor")
					{
						preparedMaterial.MetallicFactor = Convert.ToSingle(parameter.Value);
						metallicAuthored = !parameter.IsDefault;
					}
					else if (parameter.Name == "RoughnessFactor")
					{
						preparedMaterial.RoughnessFactor = Convert.ToSingle(parameter.Value);
						roughnessAuthored = !parameter.IsDefault;
					}
				}

				// Game-ready assets typically keep the factors at 1 and put the real per-texel values
				// into the metallic-roughness texture (G = roughness, B = metallic) - without sampling
				// it the preview would treat everything as polished-then-fully-rough metal.
				var mrTexture = channel.Texture;
				if (mrTexture?.PrimaryImage != null)
				{
					preparedMaterial.MetallicRoughnessTexture = DecodeTexture(mrTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
				}
			}

			// KHR_materials_ior / KHR_materials_dispersion - SharpGLTF мапит их прямо в свойства
			// материала (IndexOfRefraction по умолчанию 1.5, Dispersion 0 = выключена).
			preparedMaterial.Ior = logicalMaterial.IndexOfRefraction;
			preparedMaterial.Dispersion = logicalMaterial.Dispersion;

			// KHR_materials_transmission: только скалярный factor - текстуру трансмиссии превью не
			// сэмплирует, а полноценной рефракции у него нет (см. UnlitInstancedPS.hlsl, там
			// аппроксимация "фон сквозь тонированное стекло").
			var transmissionChannel = logicalMaterial.FindChannel("Transmission");
			if (transmissionChannel.HasValue)
			{
				foreach (var parameter in transmissionChannel.Value.Parameters)
				{
					if (parameter.Name == "TransmissionFactor")
					{
						preparedMaterial.TransmissionFactor = Convert.ToSingle(parameter.Value);
					}
				}
			}

			// KHR_materials_sheen: велюровый "световой ворс" (Charlie-лоб в шейдере). Цвет и своя
			// шероховатость - двумя каналами SharpGLTF. Параметры матчатся по ТИПУ значения (в каждом
			// канале ровно один нетекстурный параметр) - имена ключей у SharpGLTF внутренние.
			var sheenColorChannel = logicalMaterial.FindChannel("SheenColor");
			if (sheenColorChannel.HasValue)
			{
				foreach (var parameter in sheenColorChannel.Value.Parameters)
				{
					if (parameter.Value is Vector3 sheenRgb)
					{
						preparedMaterial.SheenColorFactor = sheenRgb;
					}
				}
			}

			var sheenRoughnessChannel = logicalMaterial.FindChannel("SheenRoughness");
			if (sheenRoughnessChannel.HasValue)
			{
				foreach (var parameter in sheenRoughnessChannel.Value.Parameters)
				{
					if (parameter.Value is float || parameter.Value is double)
					{
						preparedMaterial.SheenRoughnessFactor = Convert.ToSingle(parameter.Value);
					}
				}
			}

			// KHR_materials_specular: перекраска/ослабление диэлектрического F0 (сатин и прочие ткани
			// с цветным бликом). specularColorFactor может быть >1 (ChairDamaskPurplegold: [1,0.25,2]) -
			// кламп произойдёт в шейдере ПОСЛЕ умножения на F0 от IOR, как велит спека.
			var specularColorChannel = logicalMaterial.FindChannel("SpecularColor");
			if (specularColorChannel.HasValue)
			{
				foreach (var parameter in specularColorChannel.Value.Parameters)
				{
					if (parameter.Value is Vector3 specularRgb)
					{
						preparedMaterial.SpecularColorFactor = specularRgb;
					}
				}
			}

			var specularFactorChannel = logicalMaterial.FindChannel("SpecularFactor");
			if (specularFactorChannel.HasValue)
			{
				foreach (var parameter in specularFactorChannel.Value.Parameters)
				{
					if (parameter.Value is float || parameter.Value is double)
					{
						preparedMaterial.SpecularFactor = Convert.ToSingle(parameter.Value);
					}
				}
			}

			// KHR_materials_volume: Beer-Lambert затухание сквозь толщу стекла. Толщину берём только
			// фактором (thicknessTexture не сэмплируется), показатель степени thickness/attenuationDistance
			// предвычисляем здесь - шейдеру нужен один float4 (rgb цвет, w показатель).
			float volumeThickness = 0f;
			float attenuationDistance = 0f;
			var attenuationColor = Vector3.One;

			var thicknessChannel = logicalMaterial.FindChannel("VolumeThickness");
			if (thicknessChannel.HasValue)
			{
				foreach (var parameter in thicknessChannel.Value.Parameters)
				{
					if (parameter.Name == "ThicknessFactor")
					{
						volumeThickness = Convert.ToSingle(parameter.Value);
						preparedMaterial.ThicknessFactor = volumeThickness;
					}
				}

				// Толщина в текстуре (G-канал по спеке) - множитель поверх factor-а; без неё
				// плотное стекло глушит просвет равномерно, и тонкие детали (гребни, шипы)
				// теряют характерную "светящуюся" прозрачность.
				var thicknessTexture = thicknessChannel.Value.Texture;
				if (thicknessTexture?.PrimaryImage != null)
				{
					preparedMaterial.ThicknessTexture = DecodeTexture(thicknessTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
				}
			}

			var attenuationChannel = logicalMaterial.FindChannel("VolumeAttenuation");
			if (attenuationChannel.HasValue)
			{
				foreach (var parameter in attenuationChannel.Value.Parameters)
				{
					if (parameter.Name == "RGB" && parameter.Value is Vector3 rgb)
					{
						attenuationColor = rgb;
					}
					else if (parameter.Name == "AttenuationDistance")
					{
						attenuationDistance = Convert.ToSingle(parameter.Value);
					}
				}
			}

			preparedMaterial.VolumeAttenuation = volumeThickness > 0f && attenuationDistance > 0f
				? new Vector4(attenuationColor, volumeThickness / attenuationDistance)
				: new Vector4(1f, 1f, 1f, 0f);

			// Запечённый ambient occlusion (R-канал по спеке, часто общая ORM-текстура с MR) +
			// occlusionStrength. Глушит ambient/env-термы в порах и складках - без него фигуры
			// выглядят "пластиково чистыми". Прямой свет по спеке AO не трогает.
			var occlusionChannel = logicalMaterial.FindChannel("Occlusion");
			if (occlusionChannel.HasValue)
			{
				foreach (var parameter in occlusionChannel.Value.Parameters)
				{
					if (parameter.Name == "OcclusionStrength")
					{
						preparedMaterial.OcclusionStrength = Convert.ToSingle(parameter.Value);
					}
				}

				// AO часто запечён под уникальную развёртку ВТОРОГО UV-канала (texCoord 1, см.
				// ChairDamaskPurplegold) - сэмпл по UV0 кладёт затемнения в случайные места.
				// Каналы выше 1 в вершине не хранятся - клампятся в TEXCOORD_1.
				preparedMaterial.OcclusionUvSet = Math.Clamp(occlusionChannel.Value.TextureCoordinate, 0, 1);

				var occlusionTexture = occlusionChannel.Value.Texture;
				if (occlusionTexture?.PrimaryImage != null)
				{
					preparedMaterial.OcclusionTexture = DecodeTexture(occlusionTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
				}
			}

			// Нормал-мапа (tangent-space, линейная - без sRGB-декода) + normalScale. Без неё весь
			// авторский микрорельеф (кладка, резьба, прожилки) теряется - поверхность шейдится
			// только геометрической нормалью.
			var normalChannel = logicalMaterial.FindChannel("Normal");
			if (normalChannel.HasValue)
			{
				foreach (var parameter in normalChannel.Value.Parameters)
				{
					if (parameter.Name == "NormalScale")
					{
						preparedMaterial.NormalScale = Convert.ToSingle(parameter.Value);
					}
				}

				var normalTexture = normalChannel.Value.Texture;
				if (normalTexture?.PrimaryImage != null)
				{
					preparedMaterial.NormalTexture = DecodeTexture(normalTexture, decodeMaxSize, decodedImages, streamSources, externalImagePaths);
				}
			}

			// KHR_texture_transform: смещение/масштаб/поворот UV, заданные материалом (Khronos-семпл
			// ChairDamaskPurplegold: scale 3x3 + rotation 0.1 на дереве/ткани - без учёта текстуры
			// тайлятся втрое крупнее и без поворота волокон). Одна трансформация на материал: с
			// baseColor-канала, фоллбек normal/MR. Предвычисляется в 2x2-матрицу + offset по формуле
			// спеки M = Translation * Rotation * Scale.
			foreach (var channelName in new[] { "BaseColor", "Normal", "MetallicRoughness" })
			{
				var transform = logicalMaterial.FindChannel(channelName)?.TextureTransform;
				if (transform == null)
				{
					continue;
				}

				float sin = MathF.Sin(transform.Rotation);
				float cos = MathF.Cos(transform.Rotation);
				preparedMaterial.UvTransform = new Vector4(
					cos * transform.Scale.X, -sin * transform.Scale.Y,
					sin * transform.Scale.X, cos * transform.Scale.Y);
				preparedMaterial.UvOffset = transform.Offset;
				preparedMaterial.HasUvTransform = true;
				break;
			}

			// Preview-friendly fallback: a material with neither a metallic-roughness texture nor
			// authored factors lands on the glTF spec defaults (metallic 1, roughness 1), i.e. a metal
			// with no diffuse and a lobe-less specular - it renders as if unlit (ambient only). A
			// neutral dielectric reads far closer to what the author meant.
			//
			// ВАЖНО: только когда НЕ авторский НИ ОДИН фактор. IsDefault у SharpGLTF означает
			// "значение равно дефолту", а не "не записан в JSON" - материал с явным metallic=1 +
			// roughness=0 (зеркало, см. PrimitiveModeNormalsTest) выглядит как "metallic не авторский",
			// но авторский roughness выдаёт осознанный metal-workflow, и глушить его в диэлектрик нельзя.
			if (preparedMaterial.MetallicRoughnessTexture == null && !metallicAuthored && !roughnessAuthored)
			{
				preparedMaterial.MetallicFactor = 0f;
				preparedMaterial.RoughnessFactor = 0.6f;
			}

			prepared.Materials.Add(preparedMaterial);
			progress?.Report(0.35f + 0.05f * ((index + 1) / (float)materialCount));
		}

		prepared.MsMaterials = swPhase.ElapsedMilliseconds;
		swPhase.Restart();

		var primitiveToMeshIdMap = new Dictionary<MeshPrimitive, int>();
		var meshWork = new List<MeshWorkItem>();

		// Скелет и клипы - ДО обхода примитивов: скин-стрим вершин переводит локальные индексы скина
		// в индексы джойнтов скелета, значит скелет к этому моменту обязан существовать.
		prepared.Skeleton = SkinningImport.BuildSkeleton(model, out var nodeToJoint);
		prepared.Animations.AddRange(SkinningImport.BuildAnimations(model, prepared.Skeleton, nodeToJoint));

		// Скин висит на УЗЛЕ, а не на примитиве, но скин-стрим нужен именно примитиву - отсюда
		// предпроход. Один и тот же примитив под двумя узлами с разными скинами разрешается в пользу
		// первого: glTF такое допускает, живые ассеты - нет, а тащить в PreparedMesh вариант на скин
		// значило бы дублировать всю геометрию ради несуществующего случая.
		var primitiveToSkin = new Dictionary<MeshPrimitive, Skin>();
		foreach (var node in model.LogicalNodes)
		{
			if (node.Mesh == null || node.Skin == null)
			{
				continue;
			}

			foreach (var primitive in node.Mesh.Primitives)
			{
				primitiveToSkin.TryAdd(primitive, node.Skin);
			}
		}

		foreach (var logicalMesh in model.LogicalMeshes)
		{
			var baseMeshName = logicalMesh.Name ?? $"Mesh_{logicalMesh.LogicalIndex}";

			for (var primitiveIndex = 0; primitiveIndex < logicalMesh.Primitives.Count; primitiveIndex++)
			{
				var primitive = logicalMesh.Primitives[primitiveIndex];
				cancellationToken.ThrowIfCancellationRequested();

				var positionsAccessor = primitive.GetVertexAccessor("POSITION");
				var uvsAccessor = primitive.GetVertexAccessor("TEXCOORD_0");
				var uvs1Accessor = primitive.GetVertexAccessor("TEXCOORD_1");
				var normalsAccessor = primitive.GetVertexAccessor("NORMAL");
				var tangentsAccessor = primitive.GetVertexAccessor("TANGENT");
				var colorsAccessor = primitive.GetVertexAccessor("COLOR_0");
				var indexAccessor = primitive.GetIndexAccessor();

				if (positionsAccessor == null)
				{
					continue;
				}

				// Топология примитива (см. MeshTopology*-константы): точки/линии рисуются клонами
				// материала с PSO соответствующей топологии (см. BuildFromPrepared /
				// ModelViewportGeometry.RegisterModelResources) - батч-рендерер группирует дроу по
				// материалу, так что отдельный материал на топологию не требует его переделки.
				int topology = primitive.DrawPrimitiveType switch
				{
					PrimitiveType.TRIANGLES => MeshTopologyTriangles,
					PrimitiveType.LINES => MeshTopologyLineList,
					PrimitiveType.LINE_STRIP => MeshTopologyLineStrip,
					PrimitiveType.LINE_LOOP => MeshTopologyLineStrip,
					PrimitiveType.POINTS => MeshTopologyPoints,
					_ => -1,
				};
				if (topology < 0)
				{
					// TRIANGLE_STRIP/FAN не поддержаны - раньше такие примитивы рисовались как
					// triangle list (мусор), теперь честно пропускаются.
					continue;
				}

				var positions = positionsAccessor.AsVector3Array();
				if (positions.Count == 0)
				{
					continue;
				}

				var uvs = uvsAccessor?.AsVector2Array();
				var uvs1 = uvs1Accessor?.AsVector2Array();
				var normals = normalsAccessor?.AsVector3Array();
				var tangents = tangentsAccessor?.AsVector4Array();
				var colors = colorsAccessor?.AsColorArray();
				var indices = indexAccessor?.AsIndicesArray();

				// glTF - правосторонняя система (+Z на зрителя), движок - левосторонняя: без
				// зеркалирования Z вся геометрия рендерится отражённой (текст задом наперёд, см.
				// PrimitiveModeNormalsTest). Вместе с инверсией Z у треугольников меняется winding -
				// он разворачивается ниже, чтобы фронт-фейсы остались фронт-фейсами.
				var sourceVertices = new Vertex[positions.Count];
				for (int i = 0; i < positions.Count; i++)
				{
					var uv = uvs != null && i < uvs.Count ? uvs[i] : Vector2.Zero;
					var uv1 = uvs1 != null && i < uvs1.Count ? uvs1[i] : Vector2.Zero;
					var normal = normals != null && i < normals.Count ? normals[i] : Vector3.UnitY;
					var color = colors != null && i < colors.Count ? colors[i] : Vector4.One;

					// Авторский glTF TANGENT (vec4, w = знак битангента). Направление зеркалируется
					// по Z вместе с позициями/нормалями, а w ИНВЕРТИРУЕТСЯ: зеркало меняет
					// ориентацию базиса (det = -1), и cross(N, T) в пространстве движка смотрит
					// против зеркалированного битангента. Без авторских тангентов w временно 1 -
					// GenerateTangents ниже перезапишет и направление, и знак.
					var tangent = tangents != null && i < tangents.Count
						? new Vector4(tangents[i].X, tangents[i].Y, -tangents[i].Z, -tangents[i].W)
						: new Vector4(1f, 0f, 0f, 1f);

					sourceVertices[i] = new Vertex
					{
						Position = new Vector3(positions[i].X, positions[i].Y, -positions[i].Z),
						TexCoord = new Vector2(uv.X, uv.Y),
						TexCoord1 = new Vector2(uv1.X, uv1.Y),
						Normal = new Vector3(normal.X, normal.Y, -normal.Z),
						Tangent = tangent,
						Color = color
					};
				}

				// Точки/линии в glTF почти всегда неиндексированные (см. PrimitiveModeNormalsTest) -
				// батч-рендерер рисует только DrawIndexedIndirect, поэтому синтезируем 0..N-1.
				uint[] sourceIndices;
				if (indices != null)
				{
					sourceIndices = indices.ToArray();
				}
				else
				{
					sourceIndices = new uint[positions.Count];
					for (uint i = 0; i < sourceIndices.Length; i++)
					{
						sourceIndices[i] = i;
					}
				}

				// A glTF logical mesh with multiple primitives (e.g. one node using several materials)
				// becomes multiple sub-meshes here, one per primitive - without a per-primitive suffix
				// they'd all inherit the same logicalMesh.Name and be indistinguishable in the sub-mesh
				// list (same label for every entry, even though each is a distinct piece of geometry).
				var meshName = logicalMesh.Primitives.Count > 1 ? $"{baseMeshName}.{primitiveIndex}" : baseMeshName;

				// Тяжёлая чистая CPU-обработка (winding/нормали/тангенты/meshopt/LOD) вынесена в
				// параллельную фазу ниже - здесь только чтение SharpGLTF (не потокобезопасно) и
				// сбор сырья по примитивам. meshId примитива = индекс work-item-а.
				primitiveToMeshIdMap[primitive] = meshWork.Count;
				meshWork.Add(new MeshWorkItem
				{
					Name = meshName,
					SourceVertices = sourceVertices,
					SourceIndices = sourceIndices,
					Topology = topology,
					HasUv = uvsAccessor != null,
					HasNormals = normalsAccessor != null,
					HasTangents = tangents != null,
					// Читается ЗДЕСЬ, а не в параллельной фазе ниже: SharpGLTF не потокобезопасен.
					SourceSkin = primitiveToSkin.TryGetValue(primitive, out var primitiveSkin)
						? SkinningImport.ReadSkinVertices(primitive, primitiveSkin, nodeToJoint, sourceVertices.Length)
						: null,
				});
			}
		}

		// Обработка примитивов независима и не трогает SharpGLTF - параллелится целиком.
		var preparedMeshes = new PreparedMesh[meshWork.Count];
		int primitivesDone = 0;
		Parallel.For(0, meshWork.Count, new ParallelOptions { CancellationToken = cancellationToken }, workIndex =>
		{
			var work = meshWork[workIndex];
			var sourceVertices = work.SourceVertices;
			var sourceIndices = work.SourceIndices;
			var sourceSkin = work.SourceSkin;

			if (work.Topology == MeshTopologyTriangles)
			{
				for (int t = 0; t + 2 < sourceIndices.Length; t += 3)
				{
					(sourceIndices[t + 1], sourceIndices[t + 2]) = (sourceIndices[t + 2], sourceIndices[t + 1]);
				}

				// Примитив без NORMAL-аксессора: по спеке glTF шейдится FLAT (per-face). Вершины
				// развариваются по треугольникам, каждая получает нормаль своей грани - ровно
				// гранёный "диско-шар" эталонного вьювера. Усреднение по вершинам (прошлая
				// версия) давало гладкую сферу, но швы дублированных вершин расходились
				// полосами в отражениях.
				if (!work.HasNormals)
				{
					var flatVertices = new Vertex[sourceIndices.Length];
					// Скин разваривается ВМЕСТЕ с геометрией: индексы вершин переписываются на
					// 0..N-1, и стрим, оставшийся в старой индексации, раздал бы вершинам чужие кости.
					var flatSkin = sourceSkin != null ? new SkinVertex[sourceIndices.Length] : null;

					for (int t = 0; t + 2 < sourceIndices.Length; t += 3)
					{
						if (flatSkin != null)
						{
							flatSkin[t] = sourceSkin[sourceIndices[t]];
							flatSkin[t + 1] = sourceSkin[sourceIndices[t + 1]];
							flatSkin[t + 2] = sourceSkin[sourceIndices[t + 2]];
						}

						var v0 = sourceVertices[sourceIndices[t]];
						var v1 = sourceVertices[sourceIndices[t + 1]];
						var v2 = sourceVertices[sourceIndices[t + 2]];

						var faceNormal = Vector3.Cross(v2.Position - v0.Position, v1.Position - v0.Position);
						faceNormal = faceNormal.LengthSquared() > 1e-16f
							? Vector3.Normalize(faceNormal)
							: Vector3.UnitY;

						v0.Normal = faceNormal;
						v1.Normal = faceNormal;
						v2.Normal = faceNormal;

						flatVertices[t] = v0;
						flatVertices[t + 1] = v1;
						flatVertices[t + 2] = v2;
						sourceIndices[t] = (uint)t;
						sourceIndices[t + 1] = (uint)(t + 1);
						sourceIndices[t + 2] = (uint)(t + 2);
					}

					sourceVertices = flatVertices;
					sourceSkin = flatSkin;
				}
			}
			var (boundsCenter, boundsRadius) = MeshUtility.ComputeBoundsData(sourceVertices);

			var finalVertices = sourceVertices;
			var finalIndices = sourceIndices;
			var finalSkin = sourceSkin;
			LodLevel[] lodLevels = null;

			if (work.Topology == MeshTopologyTriangles)
			{
				// Must run before Optimize/GenerateLods reorder/remap vertices - it needs the
				// pristine per-triangle winding to compute per-triangle tangents, but the resulting
				// per-vertex Tangent then rides along automatically through any later remap (it's
				// just another Vertex field, opaque to Meshopt's vertex-remap/simplify passes).
				// Только фоллбек: авторский glTF TANGENT (уже в вершинах, со знаком w) точнее
				// генерации - он согласован с запечкой нормал-мапы (MikkTSpace и пр.).
				if (!work.HasTangents)
				{
					MeshUtility.GenerateTangents(sourceVertices, sourceIndices);
				}

				if (finalSkin == null)
				{
					if (options.OptimizeMesh)
					{
						(finalVertices, finalIndices) = MeshUtility.OptimizeMeshData(finalVertices, finalIndices);
					}

					if (options.GenerateLods)
					{
						(finalVertices, finalIndices, lodLevels) =
							MeshUtility.GenerateLodGroupData(finalVertices, finalIndices, options.LodRatios);
					}
				}
				else
				{
					// Скиннед-меш проходит те же проходы, но СШИТОЙ вершиной: meshopt переставляет,
					// склеивает и выбрасывает вершины, не отдавая наружу полную таблицу перестановки,
					// и параллельный скин-стрим после этого разъезжается с геометрией (см. IMeshVertex).
					var packed = MeshUtility.PackSkinned(finalVertices, finalSkin);

					if (options.OptimizeMesh)
					{
						(packed, finalIndices) = MeshUtility.OptimizeMeshData(packed, finalIndices);
					}

					if (options.GenerateLods)
					{
						(packed, finalIndices, lodLevels) =
							MeshUtility.GenerateLodGroupData(packed, finalIndices, options.LodRatios);
					}

					(finalVertices, finalSkin) = MeshUtility.UnpackSkinned(packed);
				}
			}

			preparedMeshes[workIndex] = new PreparedMesh
			{
				Name = work.Name,
				Vertices = finalVertices,
				Indices = finalIndices,
				SkinVertices = finalSkin,
				LodLevels = lodLevels,
				BoundsCenter = boundsCenter,
				BoundsRadius = boundsRadius,
				HasUv = work.HasUv,
				Topology = work.Topology,
			};

			progress?.Report(0.4f + 0.55f * (Interlocked.Increment(ref primitivesDone) / (float)meshWork.Count));
		});

		prepared.Meshes.AddRange(preparedMeshes);

		// Кэш запечённых мешей для нераскладываемых матриц (см. ниже): один меш под несколькими
		// узлами с ОДИНАКОВОЙ мировой матрицей пекётся однажды.
		var bakedMeshCache = new Dictionary<(int MeshId, Matrix4x4 World), int>();

		foreach (var node in model.LogicalNodes)
		{
			if (node.Mesh == null)
			{
				continue;
			}

			// Decompose ПРОВЕРЯЕТСЯ: мировая матрица глубокой иерархии (родительский поворот поверх
			// неравномерного масштаба - Intel Sponza) содержит shear, в TRS не представимый.
			// Decompose тогда возвращает false и МУСОР в out-параметрах - геометрия таких узлов
			// съезжала и перекашивалась. Фоллбек - запечь матрицу прямо в вершины (см. BakeMeshWithMatrix).
			bool trsValid = Matrix4x4.Decompose(node.WorldMatrix, out var scale, out var rotation, out var translation);

			// Та же RH->LH конвертация, что и для вершин выше: зеркалим Z трансляции, а поворот
			// сопрягаем отражением M*R*M (M = diag(1,1,-1)), что для кватерниона даёт (-x,-y,z,w).
			translation.Z = -translation.Z;
			rotation = new Quaternion(-rotation.X, -rotation.Y, rotation.Z, rotation.W);

			foreach (var primitive in node.Mesh.Primitives)
			{
				if (primitiveToMeshIdMap.TryGetValue(primitive, out int meshId))
				{
					// Скиннед-примитив: по спеке glTF трансформация узла с мешом ИГНОРИРУЕТСЯ - меш
					// живёт в пространстве скина, и всё положение задают джойнты. Запечь сюда
					// WorldMatrix значило бы применить трансформацию узла дважды (второй раз - через
					// матрицы джойнтов), и персонаж уезжал бы вдвое дальше от начала координат.
					// Инстанс остаётся единичным: мировое размещение задаёт трансформ ENTITY, а поза -
					// палитра скиннинг-матриц.
					if (prepared.Meshes[meshId].SkinVertices != null)
					{
						prepared.Instances.Add(new InstanceData
						{
							transform = new Transform
							{
								position = Vector3.Zero,
								rotation = Quaternion.Identity,
								scale = Vector3.One,
							},
							meshId = meshId,
							materialId = primitive.Material?.LogicalIndex ?? -1,
						});
						continue;
					}

					if (!trsValid)
					{
						var cacheKey = (meshId, node.WorldMatrix);
						if (!bakedMeshCache.TryGetValue(cacheKey, out int bakedId))
						{
							bakedId = BakeMeshWithMatrix(prepared, meshId, node.WorldMatrix);
							bakedMeshCache[cacheKey] = bakedId;
						}
						meshId = bakedId;
						translation = Vector3.Zero;
						rotation = Quaternion.Identity;
						scale = Vector3.One;
					}

					var material = primitive.Material;
					int materialId = material?.LogicalIndex ?? -1;

					// Не-треугольная топология: инстанс ссылается на материал-клон с подходящим PSO
					// (создаётся в BuildFromPrepared по этому реестру).
					int topology = prepared.Meshes[meshId].Topology;
					if (topology != MeshTopologyTriangles)
					{
						int synthKey = MakeTopologyMaterialKey(topology, materialId);
						prepared.TopologyMaterialClones[synthKey] = (materialId, topology);
						materialId = synthKey;
					}

					prepared.Instances.Add(new InstanceData
					{
						transform = new Transform { position = translation, rotation = rotation, scale = scale },
						meshId = meshId,
						materialId = materialId
					});
				}
			}
		}

		progress?.Report(1f);
		prepared.MsMeshes = swPhase.ElapsedMilliseconds;
		return prepared;
	}

	/// <summary>Пошаговое создание GPU-ресурсов готовой <see cref="PreparedModel"/>: итератор
	/// возвращает ОЦЕНКУ байт, залитых в GPU на очередном шаге (текстуры материала / вершины+индексы
	/// меша). Diligent освобождает страницы upload-хипа только на FinishFrame (Present), поэтому
	/// финализация всей модели одним кадром раздувала host-visible память до гигабайт («Space in
	/// dynamic heap is almost exhausted», peak 2.5+ GB). Вызывающий (<see
	/// cref="ModelLoadRequest.FinalizeChunk"/>) двигает итератор, пока не выберет байтовый бюджет
	/// кадра, и продолжает на следующем кадре - <paramref name="result"/> наполняется по мере
	/// движения и валиден только после того, как MoveNext вернул false.</summary>
	private static IEnumerator<long> BuildFromPreparedIncremental(IGraphicsApi graphicsApi, ModelLoadOptions options,
		PreparedModel prepared, ModelLoader result)
	{
		var (vsFactoryPath, vsFileName) = options.VertexShader.ToShaderFactoryParts();
		var (psFactoryPath, psFileName) = options.PixelShader.ToShaderFactoryParts();
		// Шейдеры модели берутся из ОБЩЕГО кэша бэкенда: варианты у разных моделей практически
		// всегда одни и те же, а компиляция идёт синхронно на потоке рендера (см. CreateSharedShader).
		// Материалы модели помечены OwnsShaders=false, так что шарёный экземпляр никто не убьёт.
		// FEATURE_RT_SHADOWS и на ВЕРШИННИКЕ: сам вершинник кейворд не читает, но он переключает
		// компилятор на DXC/SM6.5 (см. DiligentShader) - D3D12 запрещает смешивать DXBC и DXIL в
		// одном PSO, и FXC-вершинник с DXC-пикселем ломал создание пайплайна.
		var vsKeywords = options.RtShadows ? new[] { "FEATURE_RT_SHADOWS" } : null;
		var modelShaderVs = graphicsApi.CreateSharedShader("Model Vertex Shader", vsFactoryPath, vsFileName,
			ShaderObjectType.Vertex, keywords: vsKeywords);
		result._ownedShaders.Add(modelShaderVs);

		// Пиксельные ВАРИАНТЫ по shader keywords (см. шапку UnlitInstancedPS.hlsl): эффекты,
		// статически известные по материалу (текстуры, transmission, dispersion, alpha clip),
		// вырезаются из кода компиляцией вместо рантайм-веток по cbuffer-флагам. Кэш - материалы
		// с одинаковым набором ключей делят один скомпилированный шейдер.
		var pixelShaderVariants = new Dictionary<string, IShaderObject>();

		IShaderObject GetPixelShaderVariant(List<string> keywords)
		{
			keywords.Sort(StringComparer.Ordinal);
			string cacheKey = string.Join(";", keywords);

			if (!pixelShaderVariants.TryGetValue(cacheKey, out var shader))
			{
				var swShader = System.Diagnostics.Stopwatch.StartNew();
				shader = graphicsApi.CreateSharedShader(
					cacheKey.Length == 0 ? "Model Pixel Shader" : $"Model Pixel Shader [{cacheKey}]",
					psFactoryPath, psFileName, ShaderObjectType.Pixel, "Main", keywords.ToArray());
				pixelShaderVariants[cacheKey] = shader;
				result._ownedShaders.Add(shader);

				result._shaderMs += swShader.ElapsedMilliseconds;
				result._shaderVariants++;
			}

			return shader;
		}

		// pm == null - встроенный дефолтный материал (без текстур/расширений).
		List<string> BuildMaterialKeywords(PreparedMaterial pm) => BuildKeywordsFromPrepared(options, pm);

		var defaultMaterial = graphicsApi.CreateMaterial("Default Material");

		// Шейдеры шареные - см. IMaterialObject.OwnsShaders. Этот материал вдобавок раздаётся
		// НЕСКОЛЬКИМ логическим индексам (все null-материалы модели ссылаются на один объект),
		// так что его Release зовётся из ModelLoader.Release столько же раз - ещё одна причина не
		// давать ему трогать шейдеры.
		defaultMaterial.OwnsShaders = false;
		defaultMaterial.SetShader(GetPixelShaderVariant(BuildMaterialKeywords(null)), modelShaderVs);

		// Белый 1x1-филлер для _MainTex/_MetallicRoughnessTex у материалов без соответствующей
		// текстуры: пиксельный шейдер статически ссылается на оба слота (ветвление по
		// PbrHas*Texture - динамическое), поэтому непривязанный дескриптор - это undefined
		// behavior на Vulkan (validation VUID-vkCmdDrawIndexedIndirect-None-08114), а не
		// безобидный «нулевой» сэмпл. Один общий на модель, создаётся лениво.
		Core.Texture fallbackTexture = null;
		ISamplerObject fallbackSampler = null;

		// Отдельный филлер для _NormalTex: белый пиксель распаковался бы в наклонённую нормаль
		// (1,1,1)->(1,1,1), а "плоский" (128,128,255) -> (0,0,1) оставляет геометрическую.
		Core.Texture flatNormalTexture = null;

		// Создаёт (лениво) оба 1x1-филлера, не привязывая их ни к какому слоту: стриминг ставит их
		// сам, со СВОИМ (авторским) сэмплером - см. BindPreparedTexture.
		void EnsureFallbackTextures()
		{
			if (fallbackTexture == null)
			{
				fallbackTexture = new Core.Texture("Model Fallback White", new CpuTextureData
				{
					Name = "Model Fallback White",
					DecodedPixels = new byte[] { 255, 255, 255, 255 },
					DecodedWidth = 1,
					DecodedHeight = 1,
				});
				fallbackTexture.Upload(graphicsApi, true);
				result._ownedTextures.Add(fallbackTexture.GpuHandle);

				fallbackSampler = graphicsApi.CreateSampler(
					name: "Model Fallback Sampler",
					filter: TextureFilter.Point,
					address: TextureAddress.Wrap,
					comparisonFunction: CompFunction.Always,
					border: Vector4.Zero);
			}

			if (flatNormalTexture == null)
			{
				flatNormalTexture = new Core.Texture("Model Fallback Flat Normal", new CpuTextureData
				{
					Name = "Model Fallback Flat Normal",
					DecodedPixels = new byte[] { 128, 128, 255, 255 },
					DecodedWidth = 1,
					DecodedHeight = 1,
				});
				flatNormalTexture.Upload(graphicsApi, true);
				result._ownedTextures.Add(flatNormalTexture.GpuHandle);
			}
		}

		void BindFallbackTexture(IMaterialObject material, string slot)
		{
			if (fallbackTexture == null)
			{
				fallbackTexture = new Core.Texture("Model Fallback White", new CpuTextureData
				{
					Name = "Model Fallback White",
					DecodedPixels = new byte[] { 255, 255, 255, 255 },
					DecodedWidth = 1,
					DecodedHeight = 1,
				});
				fallbackTexture.Upload(graphicsApi, true);
				result._ownedTextures.Add(fallbackTexture.GpuHandle);

				fallbackSampler = graphicsApi.CreateSampler(
					name: "Model Fallback Sampler",
					filter: TextureFilter.Point,
					address: TextureAddress.Wrap,
					comparisonFunction: CompFunction.Always,
					border: Vector4.Zero);
			}

			material.SetTexture(slot, fallbackTexture.GpuHandle);
			material.SetImmutableSampler(slot, fallbackSampler);
		}

		void BindFlatNormalFallback(IMaterialObject material)
		{
			// Белый филлер создаёт общий сэмплер - гарантируем его наличие.
			if (fallbackSampler == null)
			{
				BindFallbackTexture(material, "_NormalTex");
			}

			if (flatNormalTexture == null)
			{
				flatNormalTexture = new Core.Texture("Model Fallback Flat Normal", new CpuTextureData
				{
					Name = "Model Fallback Flat Normal",
					DecodedPixels = new byte[] { 128, 128, 255, 255 },
					DecodedWidth = 1,
					DecodedHeight = 1,
				});
				flatNormalTexture.Upload(graphicsApi, true);
				result._ownedTextures.Add(flatNormalTexture.GpuHandle);
			}

			material.SetTexture("_NormalTex", flatNormalTexture.GpuHandle);
			material.SetImmutableSampler("_NormalTex", fallbackSampler);
		}

		BindFallbackTexture(defaultMaterial, "_MainTex");
		BindFallbackTexture(defaultMaterial, "_OcclusionTex");
		BindFlatNormalFallback(defaultMaterial);

		// Все три филлера гарантированно созданы к этой точке (вызовы выше) - публикуем их на модели
		// для BuildAdditionalMaterialSet (см. поле-комментарии у FallbackWhiteTexture/FallbackSampler/
		// FallbackFlatNormalTexture).
		result.FallbackWhiteTexture = fallbackTexture.GpuHandle;
		result.FallbackSampler = fallbackSampler;
		result.FallbackFlatNormalTexture = flatNormalTexture.GpuHandle;

		result.materialObjects.Add(-1, defaultMaterial);

		// The built-in default material is not a glTF material, so the spec's metallic=1 default
		// would make it preview as a dark mirror in Lighting mode - neutral dielectric gray instead.
		var defaultPbr = new MaterialPbrFactors
		{
			BaseColorFactor = Vector4.One,
			MetallicFactor = 0f,
			RoughnessFactor = 0.6f,
			HasBaseColorTexture = false,
			AlphaCutoff = 0f,
			Ior = 1.5f,
			VolumeAttenuation = new Vector4(1f, 1f, 1f, 0f),
			NormalScale = 1f,
			OcclusionStrength = 1f,
			SpecularColorFactor = Vector4.One
		};
		result.MaterialPbr[-1] = defaultPbr;

		// Шейдеры + дефолтный материал с 1x1-филлерами - копейки, но это удобная точка отсечки
		// перед первым «тяжёлым» материалом.
		yield return 4096;

		// Оценка залитых в GPU байт при материализации материала: сумма несжатых RGBA-пикселей
		// его текстур (каждый Bind* делает отдельный Upload, так что считаем по слотам).
		static long EstimateMaterialBytes(PreparedMaterial pm)
		{
			if (pm == null)
			{
				return 4096;
			}

			// В режиме стриминга Pixels у всех каналов null (заливки на этой фазе нет вовсе) - оценка
			// честно выходит в «почти ноль», и финализация материалов не тратит кадровый бюджет.
			long bytes = 4096;
			bytes += SlotBytes(pm.BaseColorTexture);
			bytes += SlotBytes(pm.MetallicRoughnessTexture);
			bytes += SlotBytes(pm.NormalTexture);
			bytes += SlotBytes(pm.OcclusionTexture);
			bytes += pm.TransmissionFactor > 0f ? SlotBytes(pm.ThicknessTexture) : 0;
			return bytes;

			// Запечённый слот пикселей не несёт, но заливка в VRAM всё равно стоит времени, и
			// пропорциональна она объёму данных: BC7/BC5 - байт на тексель, плюс треть на хвост
			// мип-цепочки. Считать такие слоты бесплатными значило бы финализировать всю сцену
			// одним куском в одном кадре.
			static long SlotBytes(PreparedTexture texture)
			{
				if (texture == null)
				{
					return 0;
				}

				if (texture.Pixels != null)
				{
					return texture.Pixels.Length;
				}

				return texture.CacheKey != null
					? (long)texture.Width * texture.Height * 4 / 3
					: 0;
			}
		}

		// KHR_materials_volume: толщина задана в ЛОКАЛЬНЫХ координатах меша и по спеке умножается
		// на масштаб узла (у Khronos-семплов DragonAttenuation/DragonDispersion узел дракона имеет
		// scale 0.25 - без учёта масштаба экспонента Beer-Lambert завышается в 4 раза, и янтарное
		// стекло глушится в тёмно-красное, а слегка голубоватое - в тёмно-синее). Толщина -
		// per-material, масштаб - per-instance; для превью берём масштаб первого инстанса,
		// использующего материал (модели с volume-стеклом практически всегда один узел на меш).
		var materialScales = new Dictionary<int, float>();
		foreach (var instance in prepared.Instances)
		{
			var s = instance.transform.scale;
			materialScales.TryAdd(instance.materialId, (s.X + s.Y + s.Z) / 3f);
		}

		// Реестр стрим-текстур по исходнику: один image шарится несколькими слотами/материалами
		// (типовая ORM-текстура), апгрейд декодируется один раз и раскладывается по всем привязкам.
		var streamEntries = new Dictionary<TextureStreamSource, StreamedTexture>();

		// Кеш ассетов этой загрузки: из него берутся запечённые .dtex, когда модель пришла из .dmdl.
		// Один экземпляр на всю финализацию - он всего лишь держит пути, но создавать его на каждый
		// из сотен слотов незачем.
		var assetCache = options.Cache;

		// Уже созданные GPU-текстуры по ключу кеша. Одна запечённая картинка (типовая ORM) шарится
		// несколькими слотами и материалами; без этой карты один и тот же .dtex читался бы с диска и
		// заливался в VRAM столько раз, сколько на него ссылок, - то есть кеш экономил бы время
		// загрузки и при этом РАЗДУВАЛ бы видеопамять против некешированного пути.
		var bakedTextures = new Dictionary<string, IGpuTexture>(StringComparer.Ordinal);

		// Записи стриминга запечённых текстур по ключу кеша - тот же приём, что и streamEntries выше:
		// одна .dtex, на которую ссылаются несколько слотов, обязана стримиться ОДНОЙ записью, иначе
		// её ступени читались бы и заливались по разу на ссылку.
		var bakedStreamEntries = new Dictionary<string, StreamedTexture>(StringComparer.Ordinal);

		// Читает .dtex и создаёт GPU-текстуру, разделяя результат между всеми слотами с тем же
		// ключом. null - файла нет (кеш чистили прямо во время загрузки).
		IGpuTexture GetOrCreateBakedTexture(string cacheKey, string slot)
		{
			if (bakedTextures.TryGetValue(cacheKey, out var existing))
			{
				return existing;
			}

			if (assetCache == null)
			{
				return null;
			}

			var payload = DtexFile.TryRead(assetCache.TexturePath(cacheKey));
			if (payload == null)
			{
				return null;
			}

			// Тот же замер, что и у обычного пути: именно по нему видно, что кеш действительно
			// убирает время из финализации, а не переносит его в другое место.
			var swBaked = System.Diagnostics.Stopwatch.StartNew();
			var texture = new Core.Texture(slot, payload.ToCpuTextureData(slot));
			texture.Upload(graphicsApi, true);
			result._textureMs += swBaked.ElapsedMilliseconds;
			result._textureCount++;

			result._ownedTextures.Add(texture.GpuHandle);
			bakedTextures[cacheKey] = texture.GpuHandle;
			return texture.GpuHandle;
		}

		// Возвращает привязку (текстура + сэмплер + запись стриминга) - её переиспользует теневой
		// материал с альфа-тестом (см. ModelLoader.MaterialBaseColor). null - слот получил филлер.
		BaseColorBinding BindPreparedTexture(IMaterialObject materialObj, string slot, PreparedTexture preparedTexture)
		{
			if (preparedTexture == null)
			{
				// Белый филлер (для _ThicknessTex G=1 -> толщина остаётся чистым factor-ом).
				BindFallbackTexture(materialObj, slot);
				return null;
			}

			// Режим стриминга: пикселей ещё нет вовсе - слот получает общий 1x1-филлер (белый, для
			// _NormalTex - плоская нормаль), а первая ступень приедет из ModelStreamer. Заливать
			// здесь нечего, поэтому финализация материалов стоит копейки и геометрия появляется
			// почти сразу. Кейворды шейдера при этом ТЕ ЖЕ (ставятся по наличию текстуры в glTF),
			// так что апгрейд не трогает PSO.
			if (preparedTexture.StreamSource != null)
			{
				if (!streamEntries.TryGetValue(preparedTexture.StreamSource, out var streamEntry))
				{
					streamEntry = new StreamedTexture
					{
						FilePath = preparedTexture.StreamSource.FilePath,
						EncodedPixels = preparedTexture.StreamSource.EncodedBytes,
						CurrentSize = 0,
						TargetSize = options.MaxTextureSize,
						Texture = null,
						AddressMode = preparedTexture.AddressMode,
						FilterMode = preparedTexture.FilterMode,
					};

					streamEntries[preparedTexture.StreamSource] = streamEntry;
					result.StreamedTextures.Add(streamEntry);
				}

				// Текстура-филлер - общая 1x1 (белая; для нормалей плоская), а вот СЭМПЛЕР ставится
				// сразу авторский: он immutable и печётся в layout PSO, то есть подменить его при
				// апгрейде уже нельзя - фоллбечный Point/Wrap остался бы с текстурой навсегда.
				EnsureFallbackTextures();
				materialObj.SetTexture(slot, slot == "_NormalTex"
					? flatNormalTexture.GpuHandle
					: fallbackTexture.GpuHandle);

				var streamFilter = preparedTexture.FilterMode == TextureFilter.Linear && options.AnisotropicFiltering
					? TextureFilter.Anisotropic
					: preparedTexture.FilterMode;

				var streamSampler = graphicsApi.CreateSampler(
					name: slot + "_Sampler",
					filter: streamFilter,
					address: preparedTexture.AddressMode,
					comparisonFunction: CompFunction.Always,
					border: Vector4.Zero,
					mipLodBias: options.MipLodBias);

				// Динамический сэмплер (на texture view), а не immutable, - как в прямом пути ниже:
				// immutable для батч-материалов был мёртв из-за PSO-кэша (см. там же), а стримингу
				// динамический ещё и роднее - при горячей замене текстуры SetTexture сам перевесит
				// его на новый view (см. DiligentMaterial.SetTexture).
				materialObj.SetSampler(slot + "_sampler", streamSampler);
				result._samplerCount++;

				streamEntry.Bindings.Add((materialObj, slot));

				// Текстура здесь - общий 1x1-филлер; теневому материалу важна не она, а ЗАПИСЬ
				// стриминга: он подпишется на неё и получит те же ступени качества.
				return new BaseColorBinding
				{
					Texture = slot == "_NormalTex" ? flatNormalTexture.GpuHandle : fallbackTexture.GpuHandle,
					Sampler = streamSampler,
					Stream = streamEntry,
				};
			}

			// Запечённая текстура: мип-цепочка лежит на диске готовой к заливке. Ни декода, ни
			// RGBA8-буфера, ни GenerateMips на GPU.
			if (preparedTexture.CacheKey != null)
			{
				var bakedFilter = preparedTexture.FilterMode == TextureFilter.Linear && options.AnisotropicFiltering
					? TextureFilter.Anisotropic
					: preparedTexture.FilterMode;

				var bakedSampler = graphicsApi.CreateSampler(
					name: slot + "_Sampler",
					filter: bakedFilter,
					address: preparedTexture.AddressMode,
					comparisonFunction: CompFunction.Always,
					border: Vector4.Zero,
					mipLodBias: options.MipLodBias);

				result._samplerCount++;

				// Стриминг поверх кеша: слот получает 1x1-филлер и запись стриминга, а ступени
				// приезжают ХВОСТАМИ мип-цепочки прямо из .dtex (см. ModelStore). Верхние - самые
				// тяжёлые - уровни при этом не читаются с диска вовсе, пока качество до них не дошло,
				// и ни одна ступень не стоит ни декода, ни пересжатия.
				if (options.StreamTextures && assetCache != null)
				{
					if (!bakedStreamEntries.TryGetValue(preparedTexture.CacheKey, out var bakedStream))
					{
						bakedStream = new StreamedTexture
						{
							DtexPath = assetCache.TexturePath(preparedTexture.CacheKey),
							DtexWidth = preparedTexture.Width,
							DtexHeight = preparedTexture.Height,
							IsBlockCompressed = true,
							CurrentSize = 0,

							// Потолок качества - СОБСТВЕННЫЙ верхний уровень .dtex, а не предел
							// импорта: файл уже запечён с этим пределом, и мелкий исходник (256px при
							// пределе 2048) иначе вечно считался бы «недогруженным» - стример гонялся
							// бы за качеством, которого в файле нет.
							TargetSize = Math.Max(preparedTexture.Width, preparedTexture.Height),
							Texture = null,
							AddressMode = preparedTexture.AddressMode,
							FilterMode = preparedTexture.FilterMode,
						};

						bakedStreamEntries[preparedTexture.CacheKey] = bakedStream;
						result.StreamedTextures.Add(bakedStream);
					}

					EnsureFallbackTextures();
					var filler = slot == "_NormalTex" ? flatNormalTexture.GpuHandle : fallbackTexture.GpuHandle;

					materialObj.SetTexture(slot, filler);
					materialObj.SetSampler(slot + "_sampler", bakedSampler);
					bakedStream.Bindings.Add((materialObj, slot));

					return new BaseColorBinding
					{
						Texture = filler,
						Sampler = bakedSampler,
						Stream = bakedStream,
					};
				}

				var bakedTexture = GetOrCreateBakedTexture(preparedTexture.CacheKey, slot);
				if (bakedTexture == null)
				{
					// .dtex исчез между проверкой кеша и заливкой (кто-то чистил папку прямо во время
					// загрузки). Пикселей в cooked-модели нет и взять их неоткуда, поэтому слот
					// получает филлер - следующая загрузка увидит промах и перепечёт.
					BindFallbackTexture(materialObj, slot);
					return null;
				}

				materialObj.SetTexture(slot, bakedTexture);
				materialObj.SetSampler(slot + "_sampler", bakedSampler);

				return new BaseColorBinding
				{
					Texture = bakedTexture,
					Sampler = bakedSampler,
				};
			}

			IGpuTexture gpuTexture;
			{
				var cpuData = new CpuTextureData
				{
					Name = slot,
					DecodedPixels = preparedTexture.Pixels,
					DecodedWidth = preparedTexture.Width,
					DecodedHeight = preparedTexture.Height,
				};

				var texture = new Core.Texture(cpuData.Name, cpuData);

				// Замер отдельно от остальной финализации: она оказалась 80% времени загрузки и при этом
				// почти не зависит от ОБЪЁМА текстур - значит цена не в байтах, а в вызовах, и надо
				// знать, в каких именно.
				var swUpload = System.Diagnostics.Stopwatch.StartNew();
				texture.Upload(graphicsApi, true);
				result._textureMs += swUpload.ElapsedMilliseconds;
				result._textureCount++;

				gpuTexture = texture.GpuHandle;
				result._ownedTextures.Add(gpuTexture);
			}

			// Линейные текстуры апгрейдятся до анизотропных (тумблер в ModelLoadOptions) - без
			// этого доска/пол мылятся под острым углом; авторский point-фильтр сохраняется.
			var filterMode = preparedTexture.FilterMode == TextureFilter.Linear && options.AnisotropicFiltering
				? TextureFilter.Anisotropic
				: preparedTexture.FilterMode;

			var swSampler = System.Diagnostics.Stopwatch.StartNew();
			var samplerObject = graphicsApi.CreateSampler(
				name: slot + "_Sampler",
				filter: filterMode,
				address: preparedTexture.AddressMode,
				comparisonFunction: CompFunction.Always,
				border: Vector4.Zero,
				mipLodBias: options.MipLodBias
			);
			result._samplerMs += swSampler.ElapsedMilliseconds;
			result._samplerCount++;

			materialObj.SetTexture(slot, gpuTexture);

			// ДИНАМИЧЕСКАЯ привязка (сэмплер вешается на texture view), а не SetImmutableSampler:
			// immutable-путь для батч-материалов молча не срабатывает - Diligent подставляет дефолтный
			// сэмплер (linear wrap), и все ручки (анизотропия, mip bias) оказываются мёртвыми.
			// Замерено пробником: кадры с ANISO=0/1 и MIPBIAS=+4 были БИТ-В-БИТ одинаковыми.
			materialObj.SetSampler(slot + "_sampler", samplerObject);

			return new BaseColorBinding { Texture = gpuTexture, Sampler = samplerObject, Stream = null };
		}

		// Записывает РЕАЛЬНУЮ (не филлер) привязку слота в result.MaterialTextureBindings под ключом
		// материала - см. поле-комментарий. Единственный писатель этого словаря.
		void TrackBinding(int materialKey, string slot, BaseColorBinding binding)
		{
			if (binding == null)
			{
				return;
			}

			if (!result.MaterialTextureBindings.TryGetValue(materialKey, out var slots))
			{
				slots = new Dictionary<string, BaseColorBinding>();
				result.MaterialTextureBindings[materialKey] = slots;
			}

			slots[slot] = binding;
		}

		// vs передаётся параметром (а не правится повторным SetShader): DiligentMaterial.SetShader
		// release-ит ранее установленные шейдеры, а они шарятся между материалами - повторный вызов
		// на живом наборе роняет процесс двойным освобождением.
		//
		// materialKey - ключ, под которым будет зарегистрирован ИТОГОВЫЙ материал в
		// result.materialObjects (обычный логический индекс или синтетический ключ клона топологии,
		// см. MakeTopologyMaterialKey) - нужен только чтобы разложить реальные привязки текстур в
		// result.MaterialTextureBindings (см. TrackBinding) для BuildAdditionalMaterialSet.
		IMaterialObject BuildMaterialObject(PreparedMaterial pm, string name, IShaderObject vs, int materialKey,
			out BaseColorBinding baseColor)
		{
			var swCreate = System.Diagnostics.Stopwatch.StartNew();
			var materialObj = graphicsApi.CreateMaterial(name);

			// Шейдеры ШАРЕНЫЕ между материалами модели (вариантный кэш + один VS): освобождать их
			// материалу нельзя - это декремент чужого счётчика ссылок и падение на следующем
			// материале. См. IMaterialObject.OwnsShaders и ModelLoader.Release.
			materialObj.OwnsShaders = false;
			result._matCreateMs += swCreate.ElapsedMilliseconds;

			var swSetShader = System.Diagnostics.Stopwatch.StartNew();
			materialObj.SetShader(GetPixelShaderVariant(BuildMaterialKeywords(pm)), vs);
			result._matShaderMs += swSetShader.ElapsedMilliseconds;

			baseColor = BindPreparedTexture(materialObj, "_MainTex", pm.BaseColorTexture);
			TrackBinding(materialKey, "_MainTex", baseColor);

			// Слот объявлен в шейдере только под HAS_MR_TEXTURE (см. UnlitInstancedPS.hlsl) - этот
			// кейворд ставится только когда у материала реально есть MR-текстура, так что фоллбек
			// тут не нужен и не должен биндиться (иначе immutable sampler без ресурса в шейдере).
			if (pm.MetallicRoughnessTexture != null)
			{
				TrackBinding(materialKey, "_MetallicRoughnessTex",
					BindPreparedTexture(materialObj, "_MetallicRoughnessTex", pm.MetallicRoughnessTexture));
			}

			// Слот объявлен в шейдере только под MATERIAL_TRANSMISSION (см. UnlitInstancedPS.hlsl) -
			// у остальных материалов кейворд выключен, и биндить нечего.
			if (pm.TransmissionFactor > 0f)
			{
				TrackBinding(materialKey, "_ThicknessTex",
					BindPreparedTexture(materialObj, "_ThicknessTex", pm.ThicknessTexture));
			}

			if (pm.NormalTexture != null)
			{
				TrackBinding(materialKey, "_NormalTex",
					BindPreparedTexture(materialObj, "_NormalTex", pm.NormalTexture));
			}
			else
			{
				BindFlatNormalFallback(materialObj);
			}

			// Белый филлер (R=1) = "ничего не заслонено" - has-флаг не нужен.
			TrackBinding(materialKey, "_OcclusionTex",
				BindPreparedTexture(materialObj, "_OcclusionTex", pm.OcclusionTexture));

			return materialObj;
		}

		// scaleKey - ключ, под которым ИНСТАНСЫ ссылаются на материал (для клонов топологий это
		// синтетический ключ, см. MakeTopologyMaterialKey), т.к. materialScales собран по инстансам.
		MaterialPbrFactors BuildFactors(PreparedMaterial pm, int scaleKey)
		{
			var averageBaseColor = ComputeAverageBaseColor(pm);
			return new MaterialPbrFactors
			{
			BaseColorFactor = pm.BaseColorFactor,
			AverageBaseColor = new Vector3(averageBaseColor.X, averageBaseColor.Y, averageBaseColor.Z),
			AverageAlpha = averageBaseColor.W,
			MetallicFactor = pm.MetallicFactor,
			RoughnessFactor = pm.RoughnessFactor,
			HasBaseColorTexture = pm.BaseColorTexture != null,
			HasMetallicRoughnessTexture = pm.MetallicRoughnessTexture != null,
			NormalScale = pm.NormalScale,
			OcclusionStrength = pm.OcclusionStrength,
			OcclusionUvSet = pm.OcclusionUvSet,
			UvTransform = pm.UvTransform,
			UvOffset = pm.UvOffset,
			HasUvTransform = pm.HasUvTransform,
			AlphaCutoff = pm.AlphaCutoff,
			AlphaMode = pm.AlphaMode,
			SoftAlphaFraction = pm.SoftAlphaFraction,
			TransmissionFactor = pm.TransmissionFactor,
			Ior = pm.Ior,
			Dispersion = pm.Dispersion,
			SheenColorRoughness = new Vector4(pm.SheenColorFactor, pm.SheenRoughnessFactor),
			SpecularColorFactor = new Vector4(pm.SpecularColorFactor, pm.SpecularFactor),
			VolumeAttenuation = ScaleVolumeAttenuation(pm, materialScales, scaleKey),
			ThicknessWorld = pm.ThicknessFactor *
				(materialScales.TryGetValue(scaleKey, out var nodeScale) && nodeScale > 0f ? nodeScale : 1f)
			};
		}

		foreach (var preparedMaterial in prepared.Materials)
		{
			if (preparedMaterial.IsNull)
			{
				result.materialObjects.Add(preparedMaterial.LogicalIndex, defaultMaterial);
				result.MaterialPbr[preparedMaterial.LogicalIndex] = defaultPbr;
				continue;
			}

			var swMat = System.Diagnostics.Stopwatch.StartNew();
			var builtMaterial = BuildMaterialObject(preparedMaterial, preparedMaterial.Name, modelShaderVs,
				preparedMaterial.LogicalIndex, out var builtBaseColor);
			result._materialMs += swMat.ElapsedMilliseconds;
			result._materialCount++;

			if (builtBaseColor != null)
			{
				result.MaterialBaseColor[preparedMaterial.LogicalIndex] = builtBaseColor;
			}

			result.materialObjects.Add(preparedMaterial.LogicalIndex, builtMaterial);
			result.MaterialPbr[preparedMaterial.LogicalIndex] =
				BuildFactors(preparedMaterial, preparedMaterial.LogicalIndex);

			yield return EstimateMaterialBytes(preparedMaterial);
		}

		// Материалы-клоны под не-треугольные топологии (см. PrepareModel): тот же шейдинг и
		// текстуры, но отдельный объект материала - RegisterModelResources назначит ему PSO с
		// нужной PrimitiveTopology, а батч-рендерер и так группирует индирект-дроу по материалу,
		// так что смешение топологий в одной модели больше ничего не требует.
		IShaderObject pointShaderVs = null;

		foreach (var (synthKey, clone) in prepared.TopologyMaterialClones)
		{
			PreparedMaterial source = null;
			if (clone.SourceMaterial >= 0)
			{
				source = prepared.Materials.Find(m => m.LogicalIndex == clone.SourceMaterial && !m.IsNull);
			}

			// PSO с POINT_LIST обязан писать builtin PointSize из VS (Vulkan
			// VUID-VkGraphicsPipelineCreateInfo-topology-08773) - точечным клонам достаётся
			// *PointVS-вариант, лежащий рядом со штатным (конвенция имени; для нестандартного VS
			// из опций остаётся обычный - валидация ругнётся, но на большинстве драйверов рендер
			// работает).
			var cloneVs = modelShaderVs;
			if (clone.Topology == MeshTopologyPoints)
			{
				if (pointShaderVs == null && vsFileName == "UnlitInstancedVS.hlsl")
				{
					// Добавляется в _ownedShaders сразу после создания - см. ниже. Кейворды - как у
					// основного вершинника (DXC-паритет с RT-вариантом пикселя).
					pointShaderVs = graphicsApi.CreateSharedShader("Model Point Vertex Shader", vsFactoryPath,
						"UnlitInstancedPointVS.hlsl", ShaderObjectType.Vertex, keywords: vsKeywords);
					result._ownedShaders.Add(pointShaderVs);
				}

				cloneVs = pointShaderVs ?? modelShaderVs;
			}

			IMaterialObject materialObj;
			MaterialPbrFactors factors;
			if (source == null)
			{
				materialObj = graphicsApi.CreateMaterial($"Default Material (topology {clone.Topology})");

				// Шейдеры здесь ШАРЕНЫЕ (вариантный кэш + один VS на модель) - освобождает их
				// ModelLoader.Release, по разу на каждый. См. IMaterialObject.OwnsShaders.
				materialObj.OwnsShaders = false;
				materialObj.SetShader(GetPixelShaderVariant(BuildMaterialKeywords(null)), cloneVs);
				BindFallbackTexture(materialObj, "_MainTex");
				BindFallbackTexture(materialObj, "_OcclusionTex");
				BindFlatNormalFallback(materialObj);
				factors = defaultPbr;
			}
			else
			{
				materialObj = BuildMaterialObject(source, $"{source.Name} (topology {clone.Topology})", cloneVs,
					synthKey, out var cloneBaseColor);
				factors = BuildFactors(source, synthKey);

				if (cloneBaseColor != null)
				{
					result.MaterialBaseColor[synthKey] = cloneBaseColor;
				}
			}

			factors.Topology = clone.Topology;
			result.materialObjects.Add(synthKey, materialObj);
			result.MaterialPbr[synthKey] = factors;

			yield return EstimateMaterialBytes(source);
		}

		foreach (var preparedMesh in prepared.Meshes)
		{
			var swMesh = System.Diagnostics.Stopwatch.StartNew();
			var meshObj = graphicsApi.CreateMesh(preparedMesh.Name);
			meshObj.SetVertices(preparedMesh.Vertices);
			meshObj.SetIndices(preparedMesh.Indices);
			result._meshMs += swMesh.ElapsedMilliseconds;
			result._meshCount++;
			meshObj.SetBounds(preparedMesh.BoundsCenter, preparedMesh.BoundsRadius);

			if (preparedMesh.LodLevels != null)
			{
				UploadLodGroup(meshObj, preparedMesh.LodLevels);
			}

			result.Meshes.Add(meshObj);
			result.MeshHasUv.Add(preparedMesh.HasUv);
			result.MeshSkin.Add(preparedMesh.SkinVertices);

			yield return (long)preparedMesh.Vertices.Length * VertexSizeBytes + (long)preparedMesh.Indices.Length * sizeof(uint);
		}

		result.Skeleton = prepared.Skeleton;
		result.Animations.AddRange(prepared.Animations);
		result.instances.AddRange(prepared.Instances);

		// Потриугольное альбедо из текстур - пока CPU-пиксели base color ещё живы (после
		// финализации они освобождаются). Потребитель - probe-GI бейкер: цвет отскока и
		// RT-отражений в разрешении треугольников вместо одного среднего на материал.
		ComputeTriangleAlbedoFromTextures(result, prepared);
	}

	/// <summary>Линейное альбедо КАЖДОГО треугольника меша: base color текстуры в центроиде UV
	/// (точечная выборка с заворотом) x линейный фактор. Ключ - meshId; меши без текстуры/UV или
	/// без CPU-пикселей (стриминг, cooked-модель) пропускаются - потребитель падает на средний
	/// цвет материала (<see cref="MaterialPbrFactors.AverageBaseColor"/>). Стоимость - единицы
	/// миллисекунд на Sponza (одна выборка на треугольник) на фоне декода текстур.</summary>
	private static void ComputeTriangleAlbedoFromTextures(ModelLoader result, PreparedModel prepared)
	{
		var materialByLogical = new Dictionary<int, PreparedMaterial>();
		foreach (var pm in prepared.Materials)
		{
			materialByLogical[pm.LogicalIndex] = pm;

			// Плитка альбедо материала - тем же проходом, пока CPU-пиксели живы.
			var tileSource = pm.BaseColorTexture;
			if (tileSource?.Pixels != null && tileSource.Width > 0 && tileSource.Height > 0 &&
				!result.MaterialAlbedoTile.ContainsKey(pm.LogicalIndex))
			{
				result.MaterialAlbedoTile[pm.LogicalIndex] = BuildAlbedoTile(tileSource);
			}
		}

		// COOKED-путь: пикселей нет, но атрибуты приехали из .dmdl готовыми - распаковываем и
		// выходим (см. PreparedModel.TriangleAttributes / EnsureTriangleAttributes).
		if (prepared.TriangleAttributes.Count > 0)
		{
			foreach (var (meshId, packed) in prepared.TriangleAttributes)
			{
				int count = packed.Length / 5;
				var albedoOut = new Vector3[count];
				var metalOut = new float[count];
				var roughOut = new float[count];
				for (int t = 0; t < count; t++)
				{
					int b = t * 5;
					albedoOut[t] = new Vector3(
						MathF.Pow(packed[b] / 255f, 2.2f),
						MathF.Pow(packed[b + 1] / 255f, 2.2f),
						MathF.Pow(packed[b + 2] / 255f, 2.2f));
					metalOut[t] = packed[b + 3] / 255f;
					roughOut[t] = packed[b + 4] / 255f;
				}

				result.TriangleAlbedo[meshId] = albedoOut;
				result.TriangleMetalness[meshId] = metalOut;
				result.TriangleRoughness[meshId] = roughOut;
			}

			return;
		}

		foreach (var inst in prepared.Instances)
		{
			if (inst.meshId < 0 || inst.meshId >= prepared.Meshes.Count ||
				result.TriangleAlbedo.ContainsKey(inst.meshId))
			{
				continue;
			}

			if (!materialByLogical.TryGetValue(inst.materialId, out var pm))
			{
				continue;
			}

			// Пикселей base color может не быть (стриминг/cooked) - это НЕ повод пропускать меш
			// целиком: потриугольная металличность/шероховатость берётся из СВОЕЙ текстуры (ниже),
			// а альбедо тогда честно падает на средний цвет материала.
			var texture = pm.BaseColorTexture;
			bool hasBasePixels = texture?.Pixels != null && texture.Width > 0 && texture.Height > 0;

			var mesh = prepared.Meshes[inst.meshId];
			if (!mesh.HasUv || mesh.Vertices == null || mesh.Indices == null || mesh.Indices.Length < 3)
			{
				continue;
			}

			// Средний цвет материала - фолбэк альбедо, когда пикселей нет (тот же источник, что у
			// потребителя: MaterialPbrFactors.AverageBaseColor).
			var factor = hasBasePixels
				? new Vector3(pm.BaseColorFactor.X, pm.BaseColorFactor.Y, pm.BaseColorFactor.Z)
				: new Vector3(ComputeAverageBaseColor(pm).X, ComputeAverageBaseColor(pm).Y,
					ComputeAverageBaseColor(pm).Z);
			int triCount = mesh.Indices.Length / 3;
			var albedo = new Vector3[triCount];

			// Буфер выборок - ОДИН на меш: stackalloc внутри цикла по треугольникам копит стек
			// (кадр метода не освобождается до выхода) и на модели уровня Sponza его срывает.
			Span<Vector2> taps = stackalloc Vector2[7];

			// Металличность - тем же проходом (те же центроиды UV), из B-канала MR-текстуры
			// (glTF: G - roughness, B - metallic; данные ЛИНЕЙНЫЕ, без sRGB-декода).
			var mrTexture = pm.MetallicRoughnessTexture;

			// Пикселей MR-текстуры нет (стриминг/cooked), а материал ПОТЕНЦИАЛЬНО металлический
			// (фактор > 0.5 - у glTF-материалов с MR-текстурой он по умолчанию 1): декодируем её
			// МЕЛКО, только ради потриугольных метал/шероховатости. Без этого сцена со стримингом
			// получала фолбэк «фактор = 1» по обоим каналам, то есть «весь материал - шершавый
			// металл»: цепочка отскоков RT-отражений не запускалась НИКОГДА (диагностика -
			// отладочный вид «RT bounce chain»: сплошь зелёный). Стоимость ограничена: декод
			// идёт только у металлических материалов и в 256px.
			// Пиксели - в ЛОКАЛЬНЫХ переменных, а не в PreparedTexture: тот же экземпляр может уйти
			// в печку ассетов, и подмена его пикселей мелким декодом запекла бы в .dtex 256px.
			var mrPixels = mrTexture?.Pixels;
			int mrWidth = mrTexture?.Width ?? 0;
			int mrHeight = mrTexture?.Height ?? 0;

			if (mrPixels == null && mrTexture?.StreamSource != null && pm.MetallicFactor > 0.5f)
			{
				try
				{
					var encoded = mrTexture.StreamSource.EncodedBytes
						?? (mrTexture.StreamSource.FilePath != null && File.Exists(mrTexture.StreamSource.FilePath)
							? File.ReadAllBytes(mrTexture.StreamSource.FilePath)
							: null);
					if (encoded != null)
					{
						var levels = DecodeEncodedImageLadder(encoded, 256, 256, 2);
						if (levels.Count > 0)
						{
							var top = levels[levels.Count - 1];
							mrPixels = top.Pixels;
							mrWidth = top.Width;
							mrHeight = top.Height;
						}
					}
				}
				catch (Exception)
				{
					// Декод - оптимизация качества отражений, а не источник правды: не вышло -
					// молча падаем на факторы материала.
				}
			}

			bool hasMrPixels = mrPixels != null && mrWidth > 0 && mrHeight > 0;
			var metalness = hasMrPixels ? new float[triCount] : null;
			var roughness = hasMrPixels ? new float[triCount] : null;

			for (int t = 0; t < triCount; t++)
			{
				uint i0 = mesh.Indices[t * 3], i1 = mesh.Indices[t * 3 + 1], i2 = mesh.Indices[t * 3 + 2];
				if (i0 >= mesh.Vertices.Length || i1 >= mesh.Vertices.Length || i2 >= mesh.Vertices.Length)
				{
					albedo[t] = factor;
					if (metalness != null)
					{
						metalness[t] = pm.MetallicFactor;
						roughness![t] = pm.RoughnessFactor;
					}
					continue;
				}

				// СЕМЬ точек по треугольнику вместо одного центроида: центр, вершины (поджатые
				// внутрь) и середины рёбер. Одна выборка ловит шум текстуры - в MR-картах
				// реальных ассетов канал металличности «крапчатый», и у отдельных треугольников
				// неметаллической ткани центроид попадал в тексель 0.6+, что в RT-отражениях
				// читалось выбросами по треугольникам. Усреднение убирает крапинки, не размывая
				// крупные детали (внутри треугольника цвет всё равно один).
				var uvA = mesh.Vertices[i0].TexCoord;
				var uvB = mesh.Vertices[i1].TexCoord;
				var uvC = mesh.Vertices[i2].TexCoord;
				var uvCentroid = (uvA + uvB + uvC) / 3f;
				taps[0] = uvCentroid;
				taps[1] = Vector2.Lerp(uvA, uvCentroid, 0.25f);
				taps[2] = Vector2.Lerp(uvB, uvCentroid, 0.25f);
				taps[3] = Vector2.Lerp(uvC, uvCentroid, 0.25f);
				taps[4] = Vector2.Lerp((uvA + uvB) * 0.5f, uvCentroid, 0.25f);
				taps[5] = Vector2.Lerp((uvB + uvC) * 0.5f, uvCentroid, 0.25f);
				taps[6] = Vector2.Lerp((uvC + uvA) * 0.5f, uvCentroid, 0.25f);

				var albedoSum = Vector3.Zero;
				float metalSum = 0f, roughSum = 0f;
				int albedoTaps = 0, mrTaps = 0;

				foreach (var tap in taps)
				{
					// Заворот UV как у Wrap-сэмплера (отрицательные тоже).
					float u = tap.X - MathF.Floor(tap.X);
					float v = tap.Y - MathF.Floor(tap.Y);

					if (hasBasePixels)
					{
						int px = Math.Clamp((int)(u * texture!.Width), 0, texture.Width - 1);
						int py = Math.Clamp((int)(v * texture.Height), 0, texture.Height - 1);
						int idx = (py * texture.Width + px) * 4;
						if (idx + 2 < texture.Pixels!.Length)
						{
							// sRGB -> linear тем же pow(2.2), что и шейдер (см. UnlitInstancedPS.hlsl).
							albedoSum += new Vector3(
								MathF.Pow(texture.Pixels[idx] / 255f, 2.2f),
								MathF.Pow(texture.Pixels[idx + 1] / 255f, 2.2f),
								MathF.Pow(texture.Pixels[idx + 2] / 255f, 2.2f));
							albedoTaps++;
						}
					}

					if (metalness != null)
					{
						int mx = Math.Clamp((int)(u * mrWidth), 0, mrWidth - 1);
						int my = Math.Clamp((int)(v * mrHeight), 0, mrHeight - 1);
						int mBase = (my * mrWidth + mx) * 4;
						if (mBase + 2 < mrPixels!.Length)
						{
							// glTF-упаковка: G - roughness, B - metallic; данные линейные.
							metalSum += mrPixels[mBase + 2] / 255f;
							roughSum += mrPixels[mBase + 1] / 255f;
							mrTaps++;
						}
					}
				}

				albedo[t] = albedoTaps > 0 ? albedoSum / albedoTaps * factor : factor;

				if (metalness != null)
				{
					metalness[t] = mrTaps > 0 ? metalSum / mrTaps * pm.MetallicFactor : pm.MetallicFactor;
					roughness![t] = mrTaps > 0 ? roughSum / mrTaps * pm.RoughnessFactor : pm.RoughnessFactor;
				}
			}

			result.TriangleAlbedo[inst.meshId] = albedo;
			if (metalness != null)
			{
				result.TriangleMetalness[inst.meshId] = metalness;
				result.TriangleRoughness[inst.meshId] = roughness!;
			}
		}
	}

	/// <summary>Считает <see cref="PreparedModel.TriangleAttributes"/> - упакованные потриугольные
	/// альбедо/металличность/шероховатость - ПОКА ЖИВЫ ПИКСЕЛИ текстур. Зовётся печкой ассетов
	/// перед записью .dmdl: у cooked-модели пикселей нет, и без этого блока RT-отражения теряли и
	/// текстурный цвет хитов, и материал (цепочка отскоков не запускалась - «металла в сцене
	/// нет»). Побочный эффект осознан: на модель уходит 5 байт на треугольник в кеше.</summary>
	internal static void EnsureTriangleAttributes(PreparedModel prepared)
	{
		if (prepared.TriangleAttributes.Count > 0)
		{
			return;
		}

		// Считаем тем же кодом, что и на обычной загрузке, - через временный контейнер.
		var scratch = new ModelLoader();
		ComputeTriangleAlbedoFromTextures(scratch, prepared);

		foreach (var (meshId, albedo) in scratch.TriangleAlbedo)
		{
			scratch.TriangleMetalness.TryGetValue(meshId, out var metal);
			scratch.TriangleRoughness.TryGetValue(meshId, out var rough);

			var packed = new byte[albedo.Length * 5];
			for (int t = 0; t < albedo.Length; t++)
			{
				int b = t * 5;
				packed[b] = EncodeUnitSrgb(albedo[t].X);
				packed[b + 1] = EncodeUnitSrgb(albedo[t].Y);
				packed[b + 2] = EncodeUnitSrgb(albedo[t].Z);
				packed[b + 3] = EncodeUnit(metal != null && t < metal.Length ? metal[t] : 0f);
				packed[b + 4] = EncodeUnit(rough != null && t < rough.Length ? rough[t] : 1f);
			}

			prepared.TriangleAttributes[meshId] = packed;
		}
	}

	private static byte EncodeUnit(float value) =>
		(byte)Math.Clamp((int)(Math.Clamp(value, 0f, 1f) * 255f + 0.5f), 0, 255);

	private static byte EncodeUnitSrgb(float linear) =>
		EncodeUnit(MathF.Pow(Math.Clamp(linear, 0f, 1f), 1f / 2.2f));

	/// <summary>Бокс-даунсемпл base color текстуры в плитку <see cref="AlbedoTileSize"/>² (см.
	/// <see cref="MaterialAlbedoTile"/>). Усреднение в линейном пространстве, но по РАЗРЕЖЕННОЙ
	/// сетке (до 4x4 сэмплов на тексель плитки, как stride у ComputeAverageBaseColor): полный
	/// проход по 2К-текстуре стоил бы сотни миллионов выборок на модель, а плитке 128² больше
	/// точности и не нужно.</summary>
	private static byte[] BuildAlbedoTile(PreparedTexture texture)
	{
		const int size = AlbedoTileSize;

		// sRGB -> linear через таблицу: pow на каждый сэмпл - главная цена всего прохода.
		Span<float> toLinear = stackalloc float[256];
		for (int i = 0; i < 256; i++)
		{
			toLinear[i] = MathF.Pow(i / 255f, 2.2f);
		}

		var tile = new byte[size * size * 4];
		var pixels = texture.Pixels!;
		for (int ty = 0; ty < size; ty++)
		{
			int y0 = (int)((long)ty * texture.Height / size);
			int y1 = Math.Max(y0 + 1, (int)((long)(ty + 1) * texture.Height / size));
			int strideY = Math.Max(1, (y1 - y0) / 4);
			for (int tx = 0; tx < size; tx++)
			{
				int x0 = (int)((long)tx * texture.Width / size);
				int x1 = Math.Max(x0 + 1, (int)((long)(tx + 1) * texture.Width / size));
				int strideX = Math.Max(1, (x1 - x0) / 4);

				float r = 0f, g = 0f, b = 0f;
				int count = 0;
				for (int y = y0; y < y1; y += strideY)
				{
					int row = y * texture.Width;
					for (int x = x0; x < x1; x += strideX)
					{
						int idx = (row + x) * 4;
						if (idx + 2 >= pixels.Length)
						{
							continue;
						}

						r += toLinear[pixels[idx]];
						g += toLinear[pixels[idx + 1]];
						b += toLinear[pixels[idx + 2]];
						count++;
					}
				}

				int outIdx = (ty * size + tx) * 4;
				if (count > 0)
				{
					float inv = 1f / count;
					tile[outIdx] = (byte)Math.Clamp((int)(MathF.Pow(r * inv, 1f / 2.2f) * 255f + 0.5f), 0, 255);
					tile[outIdx + 1] = (byte)Math.Clamp((int)(MathF.Pow(g * inv, 1f / 2.2f) * 255f + 0.5f), 0, 255);
					tile[outIdx + 2] = (byte)Math.Clamp((int)(MathF.Pow(b * inv, 1f / 2.2f) * 255f + 0.5f), 0, 255);
				}

				tile[outIdx + 3] = 255;
			}
		}

		return tile;
	}

	/// <summary>Shader-кейворды материала по сырому <see cref="PreparedMaterial"/> - единственный
	/// источник истины и для финализации (локальный BuildMaterialKeywords внутри
	/// <see cref="BuildFromPreparedIncremental"/>), и для фоновой прекомпиляции
	/// (<see cref="PrecompileShaderVariants"/>): разойдись наборы, прекомпиляция грела бы не те
	/// варианты, и финализация снова компилировала бы синхронно на GPU-потоке.
	/// pm == null - встроенный дефолтный материал (без текстур/расширений).</summary>
	private static List<string> BuildKeywordsFromPrepared(ModelLoadOptions options, PreparedMaterial pm)
	{
		var keywords = new List<string>();

		if (options.PreviewLightingFeatures)
		{
			keywords.Add("FEATURE_NORMAL_MAPS");
			keywords.Add("FEATURE_OCCLUSION");
			keywords.Add("FEATURE_SHADOWS");
		}

		// Теневые лучи по TLAS - вариант компилируется DXC/SM6.5 (см. DiligentShader) и требует
		// привязанного TLAS; включается только на устройстве с inline-трассировкой.
		if (options.RtShadows)
		{
			keywords.Add("FEATURE_RT_SHADOWS");
		}

		// Тонкий G-buffer отражений вторым/третьим MRT-слотом (см. ModelLoadOptions.ReflectionGbuffer).
		if (options.ReflectionGbuffer)
		{
			keywords.Add("FEATURE_REFLECTION_GBUFFER");
		}

		if (pm == null)
		{
			return keywords;
		}

		if (pm.BaseColorTexture != null)
		{
			keywords.Add("HAS_BASECOLOR_TEXTURE");
		}
		if (pm.MetallicRoughnessTexture != null)
		{
			keywords.Add("HAS_MR_TEXTURE");
		}
		if (pm.AlphaCutoff > 0f)
		{
			keywords.Add("MATERIAL_ALPHA_CLIP");
		}
		if (pm.TransmissionFactor > 0f)
		{
			keywords.Add("MATERIAL_TRANSMISSION");
			if (pm.Dispersion > 0f)
			{
				keywords.Add("MATERIAL_DISPERSION");
			}
		}
		if (pm.SheenColorFactor != Vector3.Zero)
		{
			keywords.Add("MATERIAL_SHEEN");
		}

		return keywords;
	}

	/// <summary>Компилирует шейдер-варианты модели ЕЩЁ В ФОНОВОЙ фазе загрузки (см.
	/// ModelLoadRequest): наборы кейвордов известны сразу после парса материалов, а создание
	/// ресурсов у IRenderDevice, в отличие от контекстов, потокобезопасно. Без этого компиляция
	/// происходила лениво - из DiligentMaterial.SetShader во время финализации, то есть синхронно
	/// на GPU-потоке: секунды фриза на КАЖДЫЙ ещё не виденный вариант UnlitInstancedPS (12+ с у
	/// Sponza при холодном кеше байткода, см. DiligentShaderBytecodeCache). Здесь же варианты
	/// компилируются параллельно, пока грузятся текстуры, и финализации остаётся готовый
	/// нативный объект из общего кэша (CreateSharedShader выдаёт ТОТ ЖЕ экземпляр - ключ кэша
	/// совпадает с ключом, который потом соберёт GetPixelShaderVariant).
	///
	/// Материалы-клоны не-треугольных топологий не греются: их вершинный шейдер зависит от
	/// топологии (см. BuildTopologyClones), встречаются они редко и компилируются по-старому.</summary>
	private static void PrecompileShaderVariants(IGraphicsApi graphicsApi, ModelLoadOptions options,
		PreparedModel prepared, CancellationToken cancellationToken)
	{
		var (vsFactoryPath, vsFileName) = options.VertexShader.ToShaderFactoryParts();
		var (psFactoryPath, psFileName) = options.PixelShader.ToShaderFactoryParts();

		var shaders = new List<IShaderObject>
		{
			// Кейворды вершинника - как в финализации (DXC-паритет RT-варианта, см. там же).
			graphicsApi.CreateSharedShader("Model Vertex Shader", vsFactoryPath, vsFileName,
				ShaderObjectType.Vertex,
				keywords: options.RtShadows ? new[] { "FEATURE_RT_SHADOWS" } : null)
		};

		var seenVariants = new HashSet<string>(StringComparer.Ordinal);
		void AddVariant(PreparedMaterial pm)
		{
			var keywords = BuildKeywordsFromPrepared(options, pm);
			keywords.Sort(StringComparer.Ordinal);
			var cacheKey = string.Join(";", keywords);
			if (!seenVariants.Add(cacheKey))
			{
				return;
			}

			shaders.Add(graphicsApi.CreateSharedShader(
				cacheKey.Length == 0 ? "Model Pixel Shader" : $"Model Pixel Shader [{cacheKey}]",
				psFactoryPath, psFileName, ShaderObjectType.Pixel, "Main", keywords.ToArray()));
		}

		// null-материалы модели получают встроенный дефолтный (см. BuildFromPreparedIncremental) -
		// его вариант нужен всегда.
		AddVariant(null);
		foreach (var pm in prepared.Materials)
		{
			if (!pm.IsNull)
			{
				AddVariant(pm);
			}
		}

		// Параллельно: вариантов единицы, но холодный стоит секунды - последовательный прогрев
		// растягивал бы фоновую фазу почти на их сумму. Compile идемпотентен и сам держит замок
		// экземпляра, отмена проверяется на входе в каждый элемент.
		Parallel.ForEach(shaders, new ParallelOptions { CancellationToken = cancellationToken },
			shader => shader.Compile());
	}

	/// <summary>Те же shader-кейворды, что и <see cref="BuildKeywordsFromPrepared"/>, но выведенные
	/// из уже посчитанных <see cref="MaterialPbrFactors"/> вместо сырого <see cref="PreparedMaterial"/>
	/// (которого больше нет - PrepareModel-данные живут только до конца ПЕРВОЙ финализации, см.
	/// ModelLoadRequest.FinalizeChunk).
	/// pbr == null - встроенный дефолтный материал (материал-клон без источника), как и pm == null там.</summary>
	private static List<string> BuildKeywordsFromFactors(ModelLoadOptions options, MaterialPbrFactors? pbr)
	{
		var keywords = new List<string>();

		if (options.PreviewLightingFeatures)
		{
			keywords.Add("FEATURE_NORMAL_MAPS");
			keywords.Add("FEATURE_OCCLUSION");
			keywords.Add("FEATURE_SHADOWS");
		}

		// Зеркало BuildKeywordsFromPrepared - наборы обязаны совпадать (см. комментарий там).
		if (options.RtShadows)
		{
			keywords.Add("FEATURE_RT_SHADOWS");
		}

		if (options.ReflectionGbuffer)
		{
			keywords.Add("FEATURE_REFLECTION_GBUFFER");
		}

		if (pbr == null)
		{
			return keywords;
		}

		var f = pbr.Value;
		if (f.HasBaseColorTexture)
		{
			keywords.Add("HAS_BASECOLOR_TEXTURE");
		}
		if (f.HasMetallicRoughnessTexture)
		{
			keywords.Add("HAS_MR_TEXTURE");
		}
		if (f.AlphaCutoff > 0f)
		{
			keywords.Add("MATERIAL_ALPHA_CLIP");
		}
		if (f.TransmissionFactor > 0f)
		{
			keywords.Add("MATERIAL_TRANSMISSION");
			if (f.Dispersion > 0f)
			{
				keywords.Add("MATERIAL_DISPERSION");
			}
		}
		if (new Vector3(f.SheenColorRoughness.X, f.SheenColorRoughness.Y, f.SheenColorRoughness.Z) != Vector3.Zero)
		{
			keywords.Add("MATERIAL_SHEEN");
		}

		return keywords;
	}

	/// <summary>
	/// Builds an ADDITIONAL, independent set of <see cref="IMaterialObject"/>s for an already-loaded
	/// <paramref name="model"/> - for a second (or Nth) viewport/environment that needs its OWN
	/// materials to register into its OWN batch renderer (see <see cref="DiligentBatchRenderer.Register"/>:
	/// registering one material object into a second batch renderer silently steals it from the first -
	/// and PSOs additionally bake per-environment SampleCount/RenderTargetFormats at registration time,
	/// see DiligentBatchRenderer ~930-954).
	///
	/// Does NOT touch the GPU beyond creating small material/PSO objects: shaders come from the
	/// device-wide shared cache (<see cref="IGraphicsApi.CreateSharedShader"/> - calling it again with
	/// the same keys is a cache hit, no recompilation), and textures/samplers are the SAME already-
	/// uploaded GPU objects <paramref name="model"/> owns (see <see cref="MaterialTextureBindings"/>,
	/// <see cref="FallbackWhiteTexture"/> et al.) - nothing is re-decoded or re-uploaded.
	///
	/// A material bound to a texture that is still mid-<see cref="ModelLoadOptions.StreamTextures"/>
	/// picks up whatever quality is CURRENT on the shared <see cref="StreamedTexture"/> entry (not the
	/// stale filler captured when the first set was built - see <see cref="StreamedTexture.Texture"/>),
	/// and registers itself into that entry's <see cref="StreamedTexture.Bindings"/> so future quality
	/// upgrades hot-swap THIS set's SRBs too, exactly like the first one (see
	/// DecaEngine.Editor.ECS.ModelStreamer.PumpTextureUpgrades / ModelStore's equivalent pump).
	///
	/// <paramref name="options"/> MUST have the same <see cref="ModelLoadOptions.Signature"/> the model
	/// was originally loaded with - anisotropy/mip bias/keyword toggles are read here again rather than
	/// re-derived from <paramref name="model"/>, and a mismatch would silently desync the second set
	/// from what its textures/samplers actually are.
	/// </summary>
	public static OrderedDictionary<int, IMaterialObject> BuildAdditionalMaterialSet(IGraphicsApi graphicsApi,
		ModelLoadOptions options, ModelLoader model)
	{
		var (vsFactoryPath, vsFileName) = options.VertexShader.ToShaderFactoryParts();
		var (psFactoryPath, psFileName) = options.PixelShader.ToShaderFactoryParts();

		// Кейворды вершинника - как в финализации (DXC-паритет RT-варианта, см. там же).
		var modelShaderVs = graphicsApi.CreateSharedShader("Model Vertex Shader", vsFactoryPath, vsFileName,
			ShaderObjectType.Vertex,
			keywords: options.RtShadows ? new[] { "FEATURE_RT_SHADOWS" } : null);

		var pixelShaderVariants = new Dictionary<string, IShaderObject>();
		IShaderObject GetPixelShaderVariant(List<string> keywords)
		{
			keywords.Sort(StringComparer.Ordinal);
			var cacheKey = string.Join(";", keywords);
			if (!pixelShaderVariants.TryGetValue(cacheKey, out var shader))
			{
				shader = graphicsApi.CreateSharedShader(
					cacheKey.Length == 0 ? "Model Pixel Shader" : $"Model Pixel Shader [{cacheKey}]",
					psFactoryPath, psFileName, ShaderObjectType.Pixel, "Main", keywords.ToArray());
				pixelShaderVariants[cacheKey] = shader;
			}

			return shader;
		}

		IShaderObject pointShaderVs = null;

		// Биндит один слот из уже загруженных ресурсов модели: реальная привязка (см.
		// MaterialTextureBindings) - тем же СЭМПЛЕРОМ (сэмплеры шарятся между окружениями, см. class-doc
		// у ModelStore) и АКТУАЛЬНОЙ текстурой стрим-записи, если она есть; иначе - тот же общий филлер,
		// каким пользуется первый набор (fallbackTexture параметр).
		void BindShared(IMaterialObject materialObj, string slot, Dictionary<string, BaseColorBinding> slots,
			IGpuTexture fallbackTexture)
		{
			if (slots != null && slots.TryGetValue(slot, out var binding))
			{
				var currentTexture = binding.Stream?.Texture ?? binding.Texture;
				materialObj.SetTexture(slot, currentTexture);
				materialObj.SetSampler(slot + "_sampler", binding.Sampler);
				binding.Stream?.Bindings.Add((materialObj, slot));
				return;
			}

			if (fallbackTexture == null)
			{
				return;
			}

			materialObj.SetTexture(slot, fallbackTexture);
			materialObj.SetImmutableSampler(slot, model.FallbackSampler);
		}

		var result = new OrderedDictionary<int, IMaterialObject>();

		for (int i = 0; i < model.materialObjects.Count; i++)
		{
			var kvp = model.materialObjects.GetAt(i);
			var key = kvp.Key;
			model.MaterialPbr.TryGetValue(key, out var pbr);

			var vs = modelShaderVs;
			if (pbr.Topology == MeshTopologyPoints)
			{
				// PSO с POINT_LIST обязан писать builtin PointSize из VS (см. тот же выбор в
				// BuildFromPreparedIncremental) - тот же именной вариант вершинного шейдера.
				if (pointShaderVs == null && vsFileName == "UnlitInstancedVS.hlsl")
				{
					pointShaderVs = graphicsApi.CreateSharedShader("Model Point Vertex Shader", vsFactoryPath,
						"UnlitInstancedPointVS.hlsl", ShaderObjectType.Vertex);
				}

				vs = pointShaderVs ?? modelShaderVs;
			}

			var materialObj = graphicsApi.CreateMaterial($"Model Material {key} (env clone)");

			// Как и у первого набора: шейдеры - шарёные device-кэшем объекты, Release на них - no-op
			// (см. DiligentShader.IsShared), поэтому этому набору не нужен собственный список owned-
			// шейдеров - освобождать здесь нечего.
			materialObj.OwnsShaders = false;
			materialObj.SetShader(GetPixelShaderVariant(BuildKeywordsFromFactors(options, pbr)), vs);

			model.MaterialTextureBindings.TryGetValue(key, out var slots);

			BindShared(materialObj, "_MainTex", slots, model.FallbackWhiteTexture);
			if (pbr.HasMetallicRoughnessTexture)
			{
				BindShared(materialObj, "_MetallicRoughnessTex", slots, null);
			}
			if (pbr.TransmissionFactor > 0f)
			{
				BindShared(materialObj, "_ThicknessTex", slots, model.FallbackWhiteTexture);
			}
			BindShared(materialObj, "_NormalTex", slots, model.FallbackFlatNormalTexture);
			BindShared(materialObj, "_OcclusionTex", slots, model.FallbackWhiteTexture);

			result.Add(key, materialObj);
		}

		return result;
	}

	private static readonly int VertexSizeBytes = System.Runtime.CompilerServices.Unsafe.SizeOf<Vertex>();

	/// <summary>Вынесено из <see cref="BuildFromPreparedIncremental"/>: unsafe-блок в теле итератора
	/// недопустим, а нативная копия LOD-таблицы обязана жить в неуправляемой памяти для SetLodGroup.</summary>
	private static void UploadLodGroup(IMeshObject meshObj, LodLevel[] lodLevels)
	{
		unsafe
		{
			var lodsNative = UnsafeArray.Allocate<LodLevel>(lodLevels.Length);
			for (int i = 0; i < lodLevels.Length; i++)
			{
				UnsafeArray.Set(lodsNative, i, lodLevels[i]);
			}
			meshObj.SetLodGroup(lodsNative);
		}
	}

	/// <summary>Домножает предвычисленную экспоненту Beer-Lambert (w) на масштаб узла-инстанса -
	/// см. комментарий у materialScales в <see cref="BuildFromPreparedIncremental"/>.</summary>
	private static Vector4 ScaleVolumeAttenuation(PreparedMaterial material, Dictionary<int, float> materialScales, int scaleKey)
	{
		var volume = material.VolumeAttenuation;
		if (volume.W > 0f && materialScales.TryGetValue(scaleKey, out var scale) && scale > 0f)
		{
			volume.W *= scale;
		}

		return volume;
	}

	/// <summary>Фоллбек для узлов, чья мировая матрица не раскладывается в TRS (shear от родительского
	/// поворота поверх неравномерного масштаба - Matrix4x4.Decompose возвращает false): матрица
	/// запекается прямо в копию вершин, инстанс получает identity-трансформ. Матрица приходит в
	/// RH-конвенции glTF и переводится в LH движка сопряжением M*W*M (M = diag(1,1,-1)) - вершины
	/// исходного меша уже отзеркалены по Z при чтении атрибутов.</summary>
	private static int BakeMeshWithMatrix(PreparedModel prepared, int meshId, Matrix4x4 worldRh)
	{
		var source = prepared.Meshes[meshId];
		var mirrorZ = Matrix4x4.CreateScale(1f, 1f, -1f);
		var world = mirrorZ * worldRh * mirrorZ;

		// Нормали - через inverse-transpose: под неравномерным масштабом/сдвигом прямое умножение
		// уводит их с перпендикуляра к поверхности.
		Matrix4x4.Invert(world, out var inverse);
		var normalMatrix = Matrix4x4.Transpose(inverse);

		var vertices = new Vertex[source.Vertices.Length];
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		for (int i = 0; i < vertices.Length; i++)
		{
			var vertex = source.Vertices[i];
			vertex.Position = Vector3.Transform(vertex.Position, world);
			vertex.Normal = SafeNormalize(Vector3.TransformNormal(vertex.Normal, normalMatrix));
			var tangent = SafeNormalize(Vector3.TransformNormal(
				new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), world));
			vertex.Tangent = new Vector4(tangent, vertex.Tangent.W);
			vertices[i] = vertex;
			min = Vector3.Min(min, vertex.Position);
			max = Vector3.Max(max, vertex.Position);
		}

		// Зеркалящая матрица (отрицательный детерминант) обращает обход треугольников - без
		// инверсии индексов culling выворачивает геометрию наизнанку. Свап покрывает и LOD-ы:
		// их LodLevel-ы - диапазоны в этом же индекс-буфере. Знак битангента флипается по той же
		// причине, что при базовом Z-зеркалировании (см. Vertex.Tangent).
		var indices = source.Indices;
		if (world.GetDeterminant() < 0f && source.Topology == MeshTopologyTriangles)
		{
			indices = (uint[])indices.Clone();
			for (int i = 0; i + 2 < indices.Length; i += 3)
			{
				(indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
			}
			for (int i = 0; i < vertices.Length; i++)
			{
				vertices[i].Tangent.W = -vertices[i].Tangent.W;
			}
		}

		prepared.Meshes.Add(new PreparedMesh
		{
			Name = source.Name + " (baked transform)",
			Vertices = vertices,
			Indices = indices,
			LodLevels = source.LodLevels,
			BoundsCenter = (min + max) * 0.5f,
			BoundsRadius = MathF.Max(0.0001f, (max - min).Length() * 0.5f),
			HasUv = source.HasUv,
			Topology = source.Topology,
		});
		return prepared.Meshes.Count - 1;
	}

	private static Vector3 SafeNormalize(Vector3 v)
	{
		return v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : v;
	}

	/// <summary>Собирает PreparedTexture из заранее декодированных пикселей image (параллельный декод
	/// в начале PrepareModel; кэш заодно дедуплицирует image, разделяемый несколькими
	/// материалами/каналами - пиксельный массив шарится, дальше он только читается) + настроек
	/// сэмплера. Сэмплер в glTF опционален (нет - значит wrap + linear по спеке): WaterBottle и
	/// другие Khronos-семплы без явных сэмплеров роняли загрузку NRE.</summary>
	private static PreparedTexture DecodeTexture(SharpGLTF.Schema2.Texture texture, int maxSize,
		Dictionary<SharpGLTF.Schema2.Image, (byte[] Pixels, int Width, int Height)> decodedImages,
		Dictionary<SharpGLTF.Schema2.Image, TextureStreamSource> streamSources,
		Dictionary<int, string> externalImagePaths = null)
	{
		var sampler = texture.Sampler;
		var prepared = new PreparedTexture
		{
			AddressMode = sampler != null ? ToAddressMode(sampler.WrapS) : TextureAddress.Wrap,
			FilterMode = sampler != null ? ToFilter(sampler.MinFilter, sampler.MagFilter) : TextureFilter.Linear,
			SourceImage = texture.PrimaryImage,
		};

		if (streamSources != null)
		{
			// Стриминг: пикселей на этой фазе нет вовсе - слот получит 1x1-филлер, а первая ступень
			// приедет из ModelStreamer. Страховка на канал, не учтённый пре-сбором usedImages.
			if (!streamSources.TryGetValue(texture.PrimaryImage, out var streamSource))
			{
				streamSource = CreateStreamSource(texture.PrimaryImage, externalImagePaths);
				streamSources[texture.PrimaryImage] = streamSource;
			}

			prepared.StreamSource = streamSource;
			return prepared;
		}

		if (!decodedImages.TryGetValue(texture.PrimaryImage, out var decoded))
		{
			// Страховка: канал, не учтённый пре-сбором usedImages, декодируется на месте.
			decoded = DecodeImagePixels(texture.PrimaryImage, maxSize);
			decodedImages[texture.PrimaryImage] = decoded;
		}

		prepared.Pixels = decoded.Pixels;
		prepared.Width = decoded.Width;
		prepared.Height = decoded.Height;
		return prepared;
	}

	/// <summary>Источник ре-декодов для стриминга: путь к ВНЕШНЕМУ файлу картинки, если он известен
	/// (типовая .gltf-сцена - папка с PNG рядом), иначе копия встроенных байт (.glb / data-URI).
	/// Путь предпочтительнее ровно по памяти: у Sponza сотни 4K-исходников, и держать их все в
	/// managed-куче всю сессию - гигабайты на ровном месте.</summary>
	private static TextureStreamSource CreateStreamSource(SharpGLTF.Schema2.Image image,
		Dictionary<int, string> externalImagePaths)
	{
		// Внешний файл, чьё чтение мы подменили заглушкой при парсинге (см. LoadModelRoot): в
		// памяти его нет вовсе, читаем с диска в момент апгрейда.
		if (externalImagePaths != null && externalImagePaths.TryGetValue(image.LogicalIndex, out var path))
		{
			return new TextureStreamSource { FilePath = path };
		}

		var sourcePath = image.Content.SourcePath;
		if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
		{
			return new TextureStreamSource { FilePath = sourcePath };
		}

		// Встроенная картинка (.glb / data-URI / bufferView) - её байты и так уже в памяти модели.
		return new TextureStreamSource { EncodedBytes = image.Content.Content.ToArray() };
	}

	/// <summary>Минимальный валидный PNG 1x1 - заглушка вместо реального содержимого внешних
	/// картинок при стриминге (см. <see cref="LoadModelRoot"/>).</summary>
	private static readonly byte[] StubPng = Convert.FromBase64String(
		"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

	/// <summary>
	/// Парсит glTF. В обычном режиме - как раньше. В режиме стриминга внешние файлы картинок НЕ
	/// ЧИТАЮТСЯ ВОВСЕ: их содержимое подменяется 1x1-заглушкой, а на выход отдаётся карта
	/// «логический индекс image -> путь к файлу», по которой стример читает нужную картинку с диска
	/// в момент, когда она реально понадобилась материалу.
	///
	/// Это и была главная причина «сцена пустая, редактор висит две минуты»: SharpGLTF грузит
	/// содержимое КАЖДОГО image при разборе документа, то есть Sponza затягивала в managed-кучу все
	/// свои сотни мегабайт (а с Intel-версией - гигабайты) PNG ещё до того, как появлялась хоть
	/// одна вершина, - и всё это до единого байта тут же становилось мусором, потому что декод
	/// текстур в этой фазе уже не делается.
	/// </summary>
	private static ModelRoot LoadModelRoot(string modelPath, ModelLoadOptions options,
		out Dictionary<int, string> externalImagePaths)
	{
		externalImagePaths = null;

		var settings = new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.TryFix };

		// Только для текстового .gltf: у .glb картинки лежат внутри самого файла, подменять нечего.
		if (!options.StreamTextures ||
			!string.Equals(Path.GetExtension(modelPath), ".gltf", StringComparison.OrdinalIgnoreCase))
		{
			return ModelRoot.Load(modelPath, settings);
		}

		// URI картинок берём из JSON напрямую: порядок элементов "images" совпадает с
		// ModelRoot.LogicalImages, а разбирать их через SharpGLTF мы как раз и не хотим.
		var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(modelPath)) ?? Environment.CurrentDirectory;
		var pathsByIndex = new Dictionary<int, string>();
		var stubbedUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		try
		{
			using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(modelPath));
			if (json.RootElement.TryGetProperty("images", out var images) &&
				images.ValueKind == System.Text.Json.JsonValueKind.Array)
			{
				int index = 0;
				foreach (var image in images.EnumerateArray())
				{
					if (image.TryGetProperty("uri", out var uriElement) &&
						uriElement.GetString() is { Length: > 0 } uri &&
						!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
					{
						var relative = Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar);
						var fullPath = Path.Combine(baseDirectory, relative);
						if (File.Exists(fullPath))
						{
							pathsByIndex[index] = fullPath;
							stubbedUris.Add(uri);
							stubbedUris.Add(Uri.UnescapeDataString(uri));
						}
					}

					index++;
				}
			}
		}
		catch (Exception)
		{
			// Не разобрали JSON сами - просто грузим обычным путём (медленно, но верно).
			return ModelRoot.Load(modelPath, settings);
		}

		if (pathsByIndex.Count == 0)
		{
			return ModelRoot.Load(modelPath, settings);
		}

		var context = ReadContext
			.Create(uri =>
			{
				if (stubbedUris.Contains(uri))
				{
					return new ArraySegment<byte>(StubPng);
				}

				var candidate = Path.Combine(baseDirectory, Uri.UnescapeDataString(uri)
					.Replace('/', Path.DirectorySeparatorChar));
				if (!File.Exists(candidate))
				{
					candidate = Path.Combine(baseDirectory, uri);
				}

				return new ArraySegment<byte>(File.ReadAllBytes(candidate));
			})
			.WithSettingsFrom(settings);

		externalImagePaths = pathsByIndex;
		return context.ReadSchema2(Path.GetFileName(modelPath));
	}

	/// <summary>Декодирование картинки (PNG/JPG) + даунскейл до <paramref name="maxSize"/> (см.
	/// ModelLoadOptions.MaxTextureSize). Чистый CPU без разделяемого состояния - зовётся из
	/// Parallel.For в PrepareModel.</summary>
	private static (byte[] Pixels, int Width, int Height) DecodeImagePixels(SharpGLTF.Schema2.Image image, int maxSize)
		=> DecodeEncodedImage(image.Content.Content.ToArray(), maxSize);

	/// <summary>Декод сжатой картинки (PNG/JPG) с даунскейлом до <paramref name="maxSize"/> (0 = без
	/// лимита). Публичный - им же фоновые апгрейды стрим-текстур ре-декодируют сохранённые исходники
	/// (см. <see cref="StreamedTextures"/>). Чистый CPU без разделяемого состояния - безопасен из
	/// любого потока; учти, что декод идёт в ПОЛНОМ разрешении файла и только потом ужимается (stb
	/// иначе не умеет) - пиковая память по одной задаче на 4K-исходнике ~64 МБ.</summary>
	public static (byte[] Pixels, int Width, int Height) DecodeEncodedImage(byte[] encodedBytes, int maxSize)
	{
		var decoded = ImageResult.FromMemory(encodedBytes, ColorComponents.RedGreenBlueAlpha);

		var pixels = decoded.Data;
		int width = decoded.Width;
		int height = decoded.Height;
		while (maxSize > 0 && (width > maxSize || height > maxSize))
		{
			(pixels, width, height) = DownscaleHalf(pixels, width, height);
		}

		return (pixels, width, height);
	}

	/// <summary>
	/// Декод сжатой картинки СРАЗУ ВСЕЙ ЛЕСТНИЦЕЙ качества - от <paramref name="firstSize"/> до
	/// <paramref name="maxSize"/> с шагом <paramref name="stepFactor"/> (степени двойки), в порядке
	/// ВОЗРАСТАНИЯ. Существует ради прогрессивного стриминга (см. DecaEngine.Editor.ECS.ModelStore):
	/// stb декодирует файл только в полном разрешении, поэтому ступень "64px" стоит ровно столько же,
	/// сколько полный декод - и лестница из четырёх ступеней раньше означала ЧЕТЫРЕ полных декода
	/// одного и того же файла. Здесь файл декодируется РОВНО ОДИН РАЗ, а ступени снимаются с той же
	/// цепочки половинных даунскейлов, которую даунскейл до целевого размера и так проходит: младшие
	/// ступени достаются практически даром.
	///
	/// Пустой список - декодировать нечего. Уровни отдаются отдельными массивами: потребитель заливает
	/// их по одному, начиная с самого маленького (модель появляется в кадре сразу), и держит остаток в
	/// памяти до заливки - см. ModelStore.PendingDecodeBytesBudget про потолок этого остатка.
	/// </summary>
	public static List<(byte[] Pixels, int Width, int Height)> DecodeEncodedImageLadder(
		byte[] encodedBytes, int maxSize, int firstSize, int stepFactor)
	{
		var decoded = ImageResult.FromMemory(encodedBytes, ColorComponents.RedGreenBlueAlpha);

		var pixels = decoded.Data;
		int width = decoded.Width;
		int height = decoded.Height;
		while (maxSize > 0 && (width > maxSize || height > maxSize))
		{
			(pixels, width, height) = DownscaleHalf(pixels, width, height);
		}

		// Верхняя ступень - то, что получилось после даунскейла до потолка; ниже неё идут ступени,
		// каждая в stepFactor раз мельче, пока не пройдена firstSize. Порядок в списке - по
		// возрастанию, поэтому собираем с конца.
		var levels = new List<(byte[] Pixels, int Width, int Height)> { (pixels, width, height) };
		var halvings = 1;
		for (int step = Math.Max(2, stepFactor); step > 2; step >>= 1)
		{
			halvings++;
		}

		while (firstSize > 0 && Math.Max(width, height) > firstSize)
		{
			for (int i = 0; i < halvings && Math.Max(width, height) > 1; i++)
			{
				(pixels, width, height) = DownscaleHalf(pixels, width, height);
			}

			levels.Add((pixels, width, height));

			if (Math.Max(width, height) <= 1)
			{
				break;
			}
		}

		levels.Reverse();
		return levels;
	}

	/// <summary>Бокс-фильтр 2x2 в один шаг вдвое - то же усреднение, что GPU GenerateMips, поэтому
	/// картинка после даунскейла совпадает с тем, что сэмплер и так показал бы на этом мипе.
	/// Нечётные размеры клампятся к краю (последние строка/столбец усредняются сами с собой).</summary>
	private static (byte[] pixels, int width, int height) DownscaleHalf(byte[] pixels, int width, int height)
	{
		int newWidth = Math.Max(1, width / 2);
		int newHeight = Math.Max(1, height / 2);
		var result = new byte[newWidth * newHeight * 4];

		for (int y = 0; y < newHeight; y++)
		{
			int srcY0 = Math.Min(height - 1, y * 2);
			int srcY1 = Math.Min(height - 1, y * 2 + 1);
			for (int x = 0; x < newWidth; x++)
			{
				int srcX0 = Math.Min(width - 1, x * 2);
				int srcX1 = Math.Min(width - 1, x * 2 + 1);
				int p00 = (srcY0 * width + srcX0) * 4;
				int p01 = (srcY0 * width + srcX1) * 4;
				int p10 = (srcY1 * width + srcX0) * 4;
				int p11 = (srcY1 * width + srcX1) * 4;
				int dst = (y * newWidth + x) * 4;
				for (int c = 0; c < 4; c++)
				{
					result[dst + c] = (byte)((pixels[p00 + c] + pixels[p01 + c] + pixels[p10 + c] + pixels[p11 + c] + 2) >> 2);
				}
			}
		}

		return (result, newWidth, newHeight);
	}

	/// <summary>Среднее линейное альбедо материала для <see cref="MaterialPbrFactors.AverageBaseColor"/>:
	/// разреженное среднее по base color текстуре (sRGB → linear), умноженное на линейный фактор.
	/// Без текстуры - просто фактор. Альфа (линейная, без sRGB) уходит в
	/// <see cref="MaterialPbrFactors.AverageAlpha"/> - по ней probe-GI бейкер отличает реально
	/// «дырявые» материалы (листва/трава/решётки, средняя альфа мала) от сплошных, которые
	/// экспортер зачем-то пометил MASK/BLEND (камень с альфой ~1) - см. ProbeGiBaker.</summary>
	private static Vector4 ComputeAverageBaseColor(PreparedMaterial pm)
	{
		EnsureAverageBaseColor(pm);
		return pm.AverageBaseColorRgba.Value;
	}

	/// <summary>Считает <see cref="PreparedMaterial.AverageBaseColorRgba"/>, если он ещё не посчитан.
	/// Вызывать ОБЯЗАТЕЛЬНО пока живы пиксели base color: и при обычной загрузке (лениво, из
	/// BuildFactors), и перед записью .dmdl - у печки свой экземпляр PreparedModel, который через
	/// финализацию не проходит, так что лениво он бы остался пустым и в кеш уехал бы фактор.</summary>
	internal static void EnsureAverageBaseColor(PreparedMaterial pm)
	{
		if (pm.AverageBaseColorRgba.HasValue)
		{
			return;
		}

		pm.AverageBaseColorRgba = ComputeAverageBaseColorCore(pm);
		pm.SoftAlphaFraction = ComputeSoftAlphaFraction(pm);
	}

	/// <summary>Доля текселей base color с «промежуточной» альфой (0.1..0.9) - насколько альфа-канал
	/// БИНАРЕН.
	///
	/// Отвечает на вопрос, который alphaMode не решает: у экспортов сплошь и листва, и накладные
	/// декали помечены одним и тем же BLEND (Intel Sponza: LeafSpring, dirt_decal - все BLEND), а
	/// вести себя в тени они обязаны противоположно. Листва - вырезка: альфа почти везде 0 или 1,
	/// бинарная тень по ней осмысленна и нужна. Декаль грязи - мягкая размазка по всему диапазону,
	/// бинарной тени у неё быть не может в принципе, и любая попытка её отбросить даёт тёмную кляксу
	/// формы своей же текстуры на стене, к которой декаль приклеена.
	///
	/// -1 = не считалось (пикселей не было).</summary>
	private static float ComputeSoftAlphaFraction(PreparedMaterial pm)
	{
		var texture = pm.BaseColorTexture;
		if (texture?.Pixels == null || texture.Width <= 0 || texture.Height <= 0)
		{
			return -1f;
		}

		int pixelCount = texture.Width * texture.Height;
		int stride = Math.Max(1, pixelCount / 4096);
		int soft = 0;
		int count = 0;
		for (int i = 0; i < pixelCount; i += stride)
		{
			int idx = i * 4;
			if (idx + 3 >= texture.Pixels.Length)
			{
				break;
			}

			float a = texture.Pixels[idx + 3] / 255f;
			if (a > 0.1f && a < 0.9f)
			{
				soft++;
			}

			count++;
		}

		return count > 0 ? (float)soft / count : -1f;
	}

	private static Vector4 ComputeAverageBaseColorCore(PreparedMaterial pm)
	{
		var factor = new Vector3(pm.BaseColorFactor.X, pm.BaseColorFactor.Y, pm.BaseColorFactor.Z);
		var texture = pm.BaseColorTexture;
		if (texture?.Pixels == null || texture.Width <= 0 || texture.Height <= 0)
		{
			// Пикселей нет. Два разных случая, и путать их нельзя:
			//
			// 1. Текстуры у слота нет вовсе - материал целиком описан фактором, среднее и есть фактор.
			//
			// 2. Текстура ЕСТЬ, но пикселей нет: режим стриминга (см. ModelLoadOptions.StreamTextures -
			//    им грузит Scene View) при ПРОМАХЕ кеша, то есть пока фоновый бейк не положил .dmdl со
			//    средним. Здесь фактор - не ответ, а тихая ложь: у glTF-материалов он почти всегда
			//    (1,1,1,1), то есть альфа выходит единицей, и по ней отбор «дырявой» геометрии
			//    (AverageAlpha < 0.6, см. ModelViewportEnvironment и ProbeGi) молча выключается. Плата
			//    за это - альфа-тест в тени пропадает у ВСЕЙ MASK/BLEND-геометрии: занавеси и накладные
			//    планки грязи/потёков Intel Sponza начинают отбрасывать тень СПЛОШНЫМ квадратом, что на
			//    стене читается крупными гладкими кляксами.
			//
			//    Поэтому неизвестная альфа объявляется НУЛЁМ - то есть «считать дырявым», - и только у
			//    материалов, которые glTF пометил MASK/BLEND (AlphaCutoff > 0). Цена ошибки в эту
			//    сторону - лишний дроу-колл на каскад у пары материалов, пока не приехал бейк; в
			//    обратную - тот самый сплошной квад в тени. RGB при этом остаётся фактором: его
			//    альфа-режим не касается, а по нему красит баунс probe-GI.
			float unknownAlpha = texture != null && pm.AlphaCutoff > 0f ? 0f : pm.BaseColorFactor.W;
			return new Vector4(factor, unknownAlpha);
		}

		// Каждый ~16-й пиксель: среднему хватает, а гигантские атласы не тормозят загрузку.
		int pixelCount = texture.Width * texture.Height;
		int stride = Math.Max(1, pixelCount / 4096);
		var sum = Vector3.Zero;
		float alphaSum = 0f;
		int count = 0;
		for (int i = 0; i < pixelCount; i += stride)
		{
			int idx = i * 4;
			if (idx + 3 >= texture.Pixels.Length)
			{
				break;
			}

			// sRGB → linear тем же pow(2.2), что и шейдер (см. UnlitInstancedPS.hlsl).
			sum += new Vector3(
				MathF.Pow(texture.Pixels[idx] / 255f, 2.2f),
				MathF.Pow(texture.Pixels[idx + 1] / 255f, 2.2f),
				MathF.Pow(texture.Pixels[idx + 2] / 255f, 2.2f));
			alphaSum += texture.Pixels[idx + 3] / 255f;
			count++;
		}

		return count > 0
			? new Vector4(sum / count * factor, alphaSum / count * pm.BaseColorFactor.W)
			: new Vector4(factor, pm.BaseColorFactor.W);
	}

	/// <summary>Сжатый исходник одной glTF-картинки для стриминга качества - один на image, шарится
	/// всеми PreparedTexture его каналов/материалов; в финализации по нему группируются привязки в
	/// один <see cref="StreamedTexture"/>.</summary>
	internal sealed class TextureStreamSource
	{
		/// <summary>Внешний файл картинки (предпочтительно - ничего не держим в памяти).</summary>
		public string FilePath;

		/// <summary>Встроенные байты (.glb / data-URI), когда файла на диске нет.</summary>
		public byte[] EncodedBytes;
	}

	internal sealed class PreparedTexture
	{
		public byte[] Pixels;
		public int Width;
		public int Height;
		public TextureAddress AddressMode;
		public TextureFilter FilterMode;

		/// <summary>null = стриминг выключен (обычный полноразмерный декод).</summary>
		public TextureStreamSource StreamSource;

		/// <summary>Ключ запечённой BC-текстуры в кеше ассетов (см. DecaEngine.Graphics.Assets.AssetCache).
		/// Когда не null, пиксели брать неоткуда и не нужно: слот заливается прямо из .dtex готовой
		/// мип-цепочкой. Это и есть штатный путь при попадании в кеш - именно он убирает из загрузки
		/// и декод PNG, и RGBA8-буферы, и генерацию мипов на GPU.</summary>
		public string CacheKey;

		/// <summary>Картинка glTF, из которой декодирован слот. Нужна только фазе бейка - по её
		/// СЖАТЫМ байтам считается ключ кеша (см. AssetCache.TextureKey). В .dmdl не попадает и при
		/// загрузке из кеша всегда null: там glTF не открывается вовсе.</summary>
		public SharpGLTF.Schema2.Image SourceImage;
	}

	internal sealed class PreparedMaterial
	{
		public int LogicalIndex;
		public bool IsNull;
		public string Name;
		public PreparedTexture BaseColorTexture;
		public PreparedTexture MetallicRoughnessTexture;
		public PreparedTexture NormalTexture;
		public float NormalScale = 1f;
		public PreparedTexture OcclusionTexture;
		public float OcclusionStrength = 1f;

		/// <summary>glTF texCoord occlusion-канала (0/1, см. MaterialPbrFactors.OcclusionUvSet).</summary>
		public int OcclusionUvSet;
		public PreparedTexture ThicknessTexture;

		// KHR_texture_transform (см. MaterialPbrFactors.UvTransform/UvOffset/HasUvTransform).
		public Vector4 UvTransform;
		public Vector2 UvOffset;
		public bool HasUvTransform;

		// glTF spec defaults - overwritten in PrepareModel only when the material authored them.
		public Vector4 BaseColorFactor = Vector4.One;
		public float MetallicFactor = 1f;
		public float RoughnessFactor = 1f;
		public float AlphaCutoff;
		public MaterialAlphaMode AlphaMode;

		/// <summary>См. <see cref="ComputeSoftAlphaFraction"/>. Считается по ПИКСЕЛЯМ, поэтому обязана
		/// попадать в .dmdl - в cooked-модели пикселей нет. -1 = не считалось.</summary>
		public float SoftAlphaFraction = -1f;
		public float TransmissionFactor;
		public float Ior = 1.5f;
		public float Dispersion;
		public Vector4 VolumeAttenuation = new(1f, 1f, 1f, 0f);
		public float ThicknessFactor;

		// KHR_materials_sheen (нулевой цвет = выключено; roughness-дефолт спеки 0).
		public Vector3 SheenColorFactor;
		public float SheenRoughnessFactor;

		// KHR_materials_specular (дефолты спеки: белый цвет, вес 1 = тождественно).
		public Vector3 SpecularColorFactor = Vector3.One;
		public float SpecularFactor = 1f;

		/// <summary>Среднее base color: rgb - линейное альбедо, w - средняя альфа (см.
		/// <see cref="EnsureAverageBaseColor"/>). Считается ПО ПИКСЕЛЯМ текстуры, поэтому обязано
		/// попадать в .dmdl: в cooked-модели пикселей нет вовсе (CookedModelFile.WriteTexture), и
		/// пересчитать это при загрузке из кеша не из чего. Пока поле не сохранялось, у всей
		/// cooked-сцены альфа выходила равной фактору (=1), а по ней отбираются «дырявые» материалы -
		/// листва теряла и альфа-тест в тенях (ModelViewportEnvironment), и исключение из BVH
		/// probe-GI (ProbeGi), то есть кроны отбрасывали тень сплошными квадратами.
		/// null = ещё не считалось.</summary>
		public Vector4? AverageBaseColorRgba;
	}

	/// <summary>Сырьё одного glTF-примитива, собранное последовательной фазой PrepareModel (чтение
	/// SharpGLTF не потокобезопасно) для параллельной CPU-обработки (winding/нормали/тангенты/
	/// meshopt/LOD). Индекс в списке work-item-ов = будущий meshId.</summary>
	private sealed class MeshWorkItem
	{
		public string Name;
		public Vertex[] SourceVertices;
		public uint[] SourceIndices;
		public int Topology;
		public bool HasUv;
		public bool HasNormals;
		public bool HasTangents;

		/// <summary>Скин-стрим примитива, null у статической геометрии (см. <see cref="SkinVertex"/>).</summary>
		public SkinVertex[] SourceSkin;
	}

	internal sealed class PreparedMesh
	{
		public string Name;
		public Vertex[] Vertices;
		public uint[] Indices;
		public LodLevel[] LodLevels;

		/// <summary>Скиннинг-атрибуты, параллельные <see cref="Vertices"/>; null - меш статический и
		/// рисуется прежним путём без compute-скиннинга.</summary>
		public SkinVertex[] SkinVertices;
		public Vector3 BoundsCenter;
		public float BoundsRadius;
		public bool HasUv;

		/// <summary>Код топологии (MeshTopology*-константы).</summary>
		public int Topology;
	}

	internal sealed class PreparedModel
	{
		public List<PreparedMaterial> Materials = new();
		public List<PreparedMesh> Meshes = new();
		public List<InstanceData> Instances = new();

		/// <summary>Скелет модели, null у статической. Один на модель, даже если скинов несколько
		/// (см. <see cref="SkinningImport.BuildSkeleton"/>).</summary>
		public PreparedSkeleton Skeleton;

		/// <summary>Клипы, разложенные по джойнтам <see cref="Skeleton"/>. Пусто, если скелета нет
		/// или ни один клип его не задевает.</summary>
		public List<PreparedAnimation> Animations = new();

		/// <summary>Реестр материалов-клонов для не-треугольных топологий: синтетический ключ ->
		/// (исходный glTF-материал, код топологии). Заполняется в PrepareModel, материализуется в
		/// BuildFromPrepared.</summary>
		public Dictionary<int, (int SourceMaterial, int Topology)> TopologyMaterialClones = new();

		/// <summary>Тайминги фоновых фаз, мс - для диагностики (см. ModelLoader.Timings). Без них
		/// оптимизация загрузки превращается в гадание: фазы стоят очень по-разному на разных
		/// ассетах, и «очевидный» виновник обычно не тот.</summary>
		public long MsParse, MsDecode, MsMaterials, MsMeshes;

		/// <summary>Сколько уникальных картинок декодировано и сколько мегабайт они заняли
		/// несжатыми - главный вкладчик в пиковую память загрузки.</summary>
		public int DecodedImages;
		public long DecodedBytes;

		/// <summary>Потриугольные атрибуты материала (ключ - meshId), по 5 байт на треугольник:
		/// альбедо RGB в sRGB-кодировке + металличность + шероховатость. Считаются ПО ПИКСЕЛЯМ
		/// текстур, поэтому обязаны попадать в .dmdl: в cooked-модели пикселей нет вовсе, и
		/// пересчитать это на загрузке не из чего - без них у RT-отражений оставались плоский
		/// средний цвет и «неизвестный» материал (цепочка отскоков не запускалась никогда).
		/// Пусто = ещё не считалось (см. <see cref="EnsureTriangleAttributes"/>).</summary>
		public Dictionary<int, byte[]> TriangleAttributes = new();
	}

	/// <summary>
	/// Handle to an in-flight background <see cref="ModelLoader"/> load (see <see cref="BeginLoadAsync"/>).
	/// Poll <see cref="PrepareTask"/>/<see cref="Progress"/> from the render loop and, once the task
	/// completes successfully, call <see cref="FinalizeOnMainThread"/> on the graphics thread to create
	/// the actual GPU resources and obtain the ready <see cref="ModelLoader"/>.
	/// </summary>
	public sealed class ModelLoadRequest
	{
		private readonly IGraphicsApi _graphicsApi;
		private readonly ModelLoadOptions _options;
		private readonly ProgressTracker _progressTracker = new();

		public string ModelPath { get; }
		public Task PrepareTask { get; }
		public float Progress => _progressTracker.Value;

		private PreparedModel _prepared;

		// Состояние пошаговой финализации (см. FinalizeChunk): наполовину построенный ModelLoader и
		// текущая позиция итератора BuildFromPreparedIncremental. Живёт между кадрами.
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
				_prepared = PrepareModel(modelPath, options, combinedProgress, cancellationToken);
				// Прогрев шейдер-вариантов ЗДЕСЬ, в фоне - иначе они компилируются лениво из
				// SetShader во время финализации, синхронно на GPU-потоке (см. PrecompileShaderVariants).
				PrecompileShaderVariants(graphicsApi, options, _prepared, cancellationToken);
			}, cancellationToken);
		}

		/// <summary>Дефолтный байтовый бюджет одного вызова <see cref="FinalizeChunk"/>. Подобран под
		/// дефолтный dynamicHeapSize Diligent-а: страницы upload-хипа возвращаются в пул только на
		/// FinishFrame (Present), поэтому бюджет кадра и определяет пиковый расход host-visible
		/// памяти при загрузке (у Sponza одним махом выходило 2.5+ GB и «Space in dynamic heap is
		/// almost exhausted» с принудительным idle GPU).</summary>
		public const long DefaultFinalizeBudgetBytes = 96L << 20;

		/// <summary>
		/// Creates the GPU resources (shaders/materials/textures/meshes) for a completed background load
		/// and returns the ready <see cref="ModelLoader"/>. Must be called on the thread that owns the
		/// <see cref="IGraphicsApi"/> passed to <see cref="BeginLoadAsync"/> (i.e. the main/render
		/// thread), only after <see cref="PrepareTask"/> has completed successfully. Заливает ВСЮ
		/// модель одним вызовом - в интерактивном рендер-лупе предпочитайте покадровый
		/// <see cref="FinalizeChunk"/>, иначе upload-хип раздувается на весь размер модели.
		/// </summary>
		public ModelLoader FinalizeOnMainThread() => FinalizeChunk(long.MaxValue, long.MaxValue);

		/// <summary>
		/// Покадровая версия <see cref="FinalizeOnMainThread"/>: создаёт GPU-ресурсы, пока суммарная
		/// оценка залитых байт не превысит <paramref name="budgetBytes"/>, и возвращает null - «зайди
		/// на следующем кадре» (между вызовами обязан пройти Present, освобождающий страницы
		/// upload-хипа). Когда всё создано - возвращает готовый <see cref="ModelLoader"/>. Те же
		/// требования, что у FinalizeOnMainThread: главный поток, PrepareTask завершился успешно.
		/// Внимание: бросить запрос между вызовами (не дойдя до результата) - значит утечь уже
		/// созданными GPU-ресурсами: у ModelLoader нет Release, недостроенный экземпляр никому не
		/// возвращается.
		/// </summary>
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

			// Финализация размазана по кадрам, поэтому её время копится по кусочкам - иначе цифра
			// показывала бы длину последнего чанка, а не стоимость фазы.
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

					// Кэш PSO - на диск ровно здесь: загрузка только что создала все конвейеры модели,
					// а следующий запуск иначе скомпилирует их заново (см. IGraphicsApi.SavePipelineCache).
					_graphicsApi.SavePipelineCache();
					return ready;
				}

				uploadedBytes += _finalizeSteps.Current;

				// Бюджет ВРЕМЕНИ, а не только байт. Байтовый бюджет считает ЗАЛИВКУ, а самое дорогое
				// в финализации байт не заливает вовсе: компиляция вариантов пиксельного шейдера
				// (секунда на вариант) и создание материалов. В режиме стриминга текстур оценка
				// материала - жалкие 4 КБ, так что все 50+ материалов Sponza со всеми компиляциями
				// проходили за ОДИН вызов, то есть за один кадр: окно редактора висело "Not
				// Responding" на всё время загрузки.
				if (swFinalize.ElapsedMilliseconds >= budgetMs)
				{
					break;
				}
			}

			_finalizeMs += swFinalize.ElapsedMilliseconds;

			return null;
		}

		/// <summary>Сколько миллисекунд одному вызову <see cref="FinalizeChunk"/> позволено занимать
		/// поток рендера. Один шаг итератора прервать нельзя (компиляция шейдера идёт целиком), так
		/// что реальный кадр может выйти длиннее - но следующий шаг уже уедет в следующий кадр, и UI
		/// остаётся живым.</summary>
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