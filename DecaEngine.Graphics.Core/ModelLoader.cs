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

}
