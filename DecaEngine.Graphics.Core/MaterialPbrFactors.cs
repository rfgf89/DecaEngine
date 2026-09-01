using System;
using System.Numerics;

namespace DecaEngine.Graphics;

public enum MaterialAlphaMode : byte
{
	Opaque = 0,
	Mask = 1,
	Blend = 2,
}

public struct MaterialPbrFactors
{
	public Vector4 BaseColorFactor;
	public float MetallicFactor;
	public float RoughnessFactor;

	/// <summary>Среднее ЛИНЕЙНОЕ альбедо материала (среднее по base color текстуре × фактор; без
	/// текстуры - просто фактор). Цвет отскока для CPU-бейка probe-GI (см. DecaEngine.Editor.ProbeGiBaker):
	/// трассировщику нужен один цвет на материал, а не сэмплинг текстур.</summary>
	public Vector3 AverageBaseColor;

	/// <summary>Средняя альфа base color текстуры × фактор (1 без текстуры). Probe-GI бейкер по
	/// ней отличает «дырявые» материалы (листва: альфа мала - не блокируют лучи) от сплошных,
	/// даже если экспортер пометил их MASK/BLEND (камень: альфа ~1).</summary>
	public float AverageAlpha;

	/// <summary>Whether the material has a base-color texture bound as _MainTex - lets a shader
	/// decide between sampling it and using <see cref="BaseColorFactor"/> alone (an unbound
	/// _MainTex cannot be detected from HLSL).</summary>
	public bool HasBaseColorTexture;

	/// <summary>Whether the material has a metallic-roughness texture bound as _MetallicRoughnessTex
	/// (glTF packing: G = roughness, B = metallic; <see cref="MetallicFactor"/>/<see cref="RoughnessFactor"/>
	/// are multipliers over it). Same unbound-texture rationale as <see cref="HasBaseColorTexture"/>.</summary>
	public bool HasMetallicRoughnessTexture;

	/// <summary>KHR_materials_transmission scalar factor (0 = opaque, 1 = fully transmissive glass);
	/// the preview approximates it without a refraction pass - see UnlitInstancedPS.hlsl.</summary>
	public float TransmissionFactor;

	/// <summary>KHR_materials_ior (glTF default 1.5 - every construction site must set it, this is a
	/// struct so an unset field is 0, which would zero out fresnel F0 in the shader).</summary>
	public float Ior;

	/// <summary>KHR_materials_dispersion strength (20 / Abbe number, 0 = off); the preview fakes it
	/// with per-channel refraction of the backdrop - see UnlitInstancedPS.hlsl.</summary>
	public float Dispersion;

	/// <summary>KHR_materials_volume, precomputed for the shader: rgb = attenuationColor, w =
	/// thicknessFactor / attenuationDistance (Beer-Lambert exponent; 0 = no volume attenuation).
	/// Учитывает масштаб узла - см. ScaleVolumeAttenuation.</summary>
	public Vector4 VolumeAttenuation;

	/// <summary>Толщина стекла в МИРОВЫХ единицах (thicknessFactor × масштаб узла) - геометрическая
	/// длина преломлённого луча внутри объёма для расчёта смещения рефракции в шейдере.</summary>
	public float ThicknessWorld;

	/// <summary>Код топологии примитивов этого материала (MeshTopology*-константы ModelLoader) -
	/// не-треугольным нужен PSO с соответствующей PrimitiveTopology, см. RegisterModelResources.</summary>
	public int Topology;

	/// <summary>glTF normalScale (множитель xy-каналов нормал-мапы; 1 = как есть). Материалы без
	/// нормал-мапы получают "плоский" филлер (0,0,1), так что отдельный has-флаг не нужен.</summary>
	public float NormalScale;

	/// <summary>glTF occlusionStrength (вес запечённого AO из _OcclusionTex; 1 = как в текстуре).
	/// Материалы без AO-текстуры получают белый филлер (R=1 = не заслонено).</summary>
	public float OcclusionStrength;

	/// <summary>Alpha-clip threshold for the shader: pixels whose base-color alpha falls below it
	/// are discarded; 0 disables clipping. glTF alphaMode MASK maps to the authored alphaCutoff,
	/// BLEND to 0.5 (the preview has no blending, so mostly-transparent texels vanish and
	/// mostly-opaque ones render solid - e.g. Intel Sponza's dirt_decal overlays at alpha 0.35
	/// would otherwise cover the walls as fully opaque grime), OPAQUE to 0.</summary>
	public float AlphaCutoff;

	/// <summary>Режим прозрачности glTF, сохранённый ОТДЕЛЬНО от <see cref="AlphaCutoff"/>. Раньше он
	/// в него схлопывался (MASK -> авторский порог, BLEND -> 0.5, OPAQUE -> 0), и после загрузки MASK
	/// от BLEND было не отличить ничем.
	///
	/// Различие не косметическое: BLEND-накладка (декали грязи и потёков Intel Sponza) обязана не
	/// отбрасывать тень ВООБЩЕ - она лежит в миллиметрах от стены, которую украшает, и её тень
	/// дублирует её же рисунок на этой самой стене крупными тёмными кляксами. У MASK (листва,
	/// решётки) тень наоборот нужна, просто вырезанная по альфе.</summary>
	public MaterialAlphaMode AlphaMode;

	/// <summary>Доля текселей base color с промежуточной альфой - насколько альфа-канал бинарен
	/// (см. ModelLoader.ComputeSoftAlphaFraction). Разделяет вырезку (листва: почти 0) и мягкую
	/// накладку (декаль грязи: заметно больше нуля), которые glTF помечает одним и тем же BLEND.
	/// -1 = неизвестно (пикселей не было и в кеше значения нет).</summary>
	public float SoftAlphaFraction;

	/// <summary>KHR_texture_transform, предвычисленная в 2x2-матрицу UV (row-major: u' = X*u + Y*v,
	/// v' = Z*u + W*v, затем + <see cref="UvOffset"/>). Одна на материал - берётся с baseColor-канала
	/// (фоллбек normal/MR, каналы одного материала практически всегда делят одну трансформацию).
	/// Валидна только при <see cref="HasUvTransform"/>.</summary>
	public Vector4 UvTransform;

	/// <summary>KHR_texture_transform offset - слагаемое после матрицы <see cref="UvTransform"/>.</summary>
	public Vector2 UvOffset;

	/// <summary>Есть ли у материала KHR_texture_transform. Шейдер применяет матрицу только по этому
	/// флагу, так что зануленный по умолчанию cbuffer (сцены вне превью его не заполняют) остаётся
	/// тождественным преобразованием.</summary>
	public bool HasUvTransform;

	/// <summary>Индекс UV-канала occlusionTexture (glTF texCoord; поддержаны 0 и 1, см.
	/// <see cref="Vertex.TexCoord1"/>) - AO часто запечён под уникальную развёртку второго канала.</summary>
	public int OcclusionUvSet;

	/// <summary>KHR_materials_sheen, упакованный для шейдера: rgb = sheenColorFactor (линейный;
	/// ноль = расширение не авторское, велюровый лоб выключен), w = sheenRoughnessFactor.
	/// Даёт ткани "световой ворс" - ретрорефлективный ободок Charlie-лоба (велюр/бархат).</summary>
	public Vector4 SheenColorRoughness;

	/// <summary>KHR_materials_specular, упакованный для шейдера: rgb = specularColorFactor
	/// (может быть &gt;1 - по спеке умножается на F0 от IOR и клампится к 1), w = specularFactor
	/// (вес всего диэлектрического спекуляра). (1,1,1,1) = расширение не авторское, тождественно;
	/// это struct - каждая точка конструирования обязана заполнить его, иначе нулевой w глушит
	/// спекуляр в чёрный (та же ловушка, что у <see cref="Ior"/>).</summary>
	public Vector4 SpecularColorFactor;
}

