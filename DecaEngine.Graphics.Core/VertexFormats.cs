using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Animation;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

public struct InstanceData
{
	public Transform transform;
	public int meshId;
	public int materialId;
}

/// <summary>
/// То, что meshopt-проходы (<see cref="MeshUtility.OptimizeMeshData{T}"/>,
/// <see cref="MeshUtility.GenerateLodGroupData{T}"/>) обязаны уметь достать из вершины: позицию для
/// упрощения и UV как атрибут его метрики. Существует ради скиннед-мешей: их вершина - это
/// <see cref="Vertex"/> ПЛЮС <see cref="SkinVertex"/>, и прогонять её через meshopt нужно ЦЕЛИКОМ.
///
/// Иначе не обойтись: и склейка дублей, и упрощение переставляют, схлопывают и выбрасывают вершины,
/// возвращая таблицы перестановок не для всех проходов (OptimizeVertexFetch переупорядочивает
/// буфер, ничего не возвращая). Скин-стрим, лежащий параллельным массивом, после такого прохода
/// разъезжается с геометрией - веса достаются чужим вершинам, и персонаж рвётся в клочья. Склейка
/// заодно начинает учитывать веса: две вершины с одинаковыми позицией/нормалью/UV, но разными
/// костями больше не сливаются в одну.
/// </summary>
public interface IMeshVertex
{
	Vector3 Position { get; }
	Vector2 TexCoord { get; }
}

/// <summary>Геометрия + скиннинг одной вершины единым blittable-блоком - только для прогона через
/// meshopt (см. <see cref="IMeshVertex"/>). В .dmdl и в GPU едет разложенным на два стрима.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SkinnedVertex : IMeshVertex
{
	public Vertex Geometry;
	public SkinVertex Skin;

	public readonly Vector3 Position => Geometry.Position;
	public readonly Vector2 TexCoord => Geometry.TexCoord;
}

public struct Vertex : IMeshVertex
{
	public Vector3 Position;
	public Vector2 TexCoord;
	public Vector3 Normal;

	readonly Vector3 IMeshVertex.Position => Position;
	readonly Vector2 IMeshVertex.TexCoord => TexCoord;

	/// <summary>
	/// Per-vertex tangent, xyz = направление роста U на поверхности (мировое после VS), w = знак
	/// битангента (±1): B = cross(N, T) * w в шейдере (см. UnlitInstancedPS.hlsl,
	/// FEATURE_NORMAL_MAPS). Источник - авторский glTF TANGENT, когда он есть (знак w инвертируется
	/// при зеркалировании Z: зеркало меняет ориентацию базиса), иначе - <see
	/// cref="MeshUtility.GenerateTangents"/>, вычисляющий и направление, и знак прямо в
	/// пространстве движка. Без верного w зеркальные UV-развёртки (атласы, симметричные модели)
	/// получают перевёрнутый Y нормал-мапы - рельеф инвертируется.
	/// </summary>
	public Vector4 Tangent;

	/// <summary>glTF COLOR_0 (линейный, по спеке умножается на base color); белый (1,1,1,1) для
	/// мешей без атрибута - ноль по умолчанию структуры красил бы всё в чёрное.</summary>
	public Vector4 Color;

	/// <summary>glTF TEXCOORD_1 - второй UV-канал; по спеке им чаще всего пользуется
	/// occlusionTexture с AO-картой, запечённой под отдельную уникальную развёртку (см.
	/// <see cref="MaterialPbrFactors.OcclusionUvSet"/>). Ноль для мешей без атрибута.</summary>
	public Vector2 TexCoord1;
}

/// <summary>
/// glTF PBR metallic-roughness scalars of one material (see <see cref="ModelLoader.MaterialPbr"/>).
/// Defaults follow the glTF spec (baseColor white, metallic 1, roughness 1) unless the material
/// explicitly authored other values.
/// </summary>
/// <summary>Режим прозрачности материала из glTF (alphaMode). Отдельно от порога отсечения -
/// см. <see cref="MaterialPbrFactors.AlphaMode"/>.</summary>
