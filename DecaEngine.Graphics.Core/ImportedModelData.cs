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

//
// Модель данных CPU-фазы импорта: то, что PrepareModel достаёт из glTF и что потребляют и
// GPU-финализация загрузчика, и кулинария ассетов (CookedModelFile/ModelAssetBaker).
//
// Раньше эти типы были ВЛОЖЕНЫ в ModelLoader: любой, кому нужен был разобранный меш - включая
// пекарню ассетов, - обязан был писать PreparedModel и тем самым зависеть от всего
// загрузчика. Данные импорта - общий словарь конвейера, а не потроха одного класса.
//

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
internal sealed class MeshWorkItem
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
