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

	/// <summary>Mean linear albedo (base color texture average x factor); bounce color for the probe-GI bake.</summary>
	public Vector3 AverageBaseColor;

	/// <summary>Mean base color alpha x factor; probe-GI uses it to tell see-through foliage from solid geometry.</summary>
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

	/// <summary>KHR_materials_volume packed for the shader: rgb = attenuationColor, w = Beer-Lambert
	/// exponent thicknessFactor / attenuationDistance, node scale applied (0 = no attenuation).</summary>
	public Vector4 VolumeAttenuation;

	/// <summary>Glass thickness in world units (thicknessFactor x node scale).</summary>
	public float ThicknessWorld;

	/// <summary>Primitive topology code (ModelLoader MeshTopology* constants); non-triangles need their own PSO.</summary>
	public int Topology;

	/// <summary>glTF normalScale; materials without a normal map get a flat (0,0,1) filler, so no has-flag.</summary>
	public float NormalScale;

	/// <summary>glTF occlusionStrength; materials without an AO texture get a white (unoccluded) filler.</summary>
	public float OcclusionStrength;

	/// <summary>Alpha-clip threshold for the shader: pixels whose base-color alpha falls below it
	/// are discarded; 0 disables clipping. glTF alphaMode MASK maps to the authored alphaCutoff,
	/// BLEND to 0.5 (the preview has no blending, so mostly-transparent texels vanish and
	/// mostly-opaque ones render solid - e.g. Intel Sponza's dirt_decal overlays at alpha 0.35
	/// would otherwise cover the walls as fully opaque grime), OPAQUE to 0.</summary>
	public float AlphaCutoff;

	/// <summary>glTF alpha mode, kept separate from <see cref="AlphaCutoff"/>: BLEND decals must cast
	/// no shadow at all, while MASK foliage still casts an alpha-clipped one.</summary>
	public MaterialAlphaMode AlphaMode;

	/// <summary>Fraction of base-color texels with intermediate alpha; separates cutout from soft
	/// overlays, which glTF both tags BLEND. -1 = unknown.</summary>
	public float SoftAlphaFraction;

	/// <summary>KHR_texture_transform as a row-major 2x2 UV matrix (u' = X*u + Y*v, v' = Z*u + W*v,
	/// then + <see cref="UvOffset"/>), taken from the baseColor channel. Valid only when <see cref="HasUvTransform"/>.</summary>
	public Vector4 UvTransform;

	/// <summary>KHR_texture_transform offset, added after <see cref="UvTransform"/>.</summary>
	public Vector2 UvOffset;

	/// <summary>Whether KHR_texture_transform is present; a zeroed cbuffer must stay an identity transform.</summary>
	public bool HasUvTransform;

	/// <summary>occlusionTexture UV channel index (glTF texCoord; only 0 and 1 are supported).</summary>
	public int OcclusionUvSet;

	/// <summary>KHR_materials_sheen packed for the shader: rgb = linear sheenColorFactor (zero = off),
	/// w = sheenRoughnessFactor.</summary>
	public Vector4 SheenColorRoughness;

	/// <summary>KHR_materials_specular packed for the shader: rgb = specularColorFactor (may exceed 1),
	/// w = specularFactor. Every construction site must set (1,1,1,1) or a zero w kills the specular.</summary>
	public Vector4 SpecularColorFactor;

	/// <summary>Linear emission: glTF emissiveFactor x KHR_materials_emissive_strength, collapsed at import.</summary>
	public Vector3 EmissiveFactor;

	/// <summary>Whether an emissive texture is bound as _EmissiveTex (sRGB, multiplies <see cref="EmissiveFactor"/>);
	/// source of the HAS_EMISSIVE_TEXTURE keyword.</summary>
	public bool HasEmissiveTexture;

	/// <summary>A BLEND material needing real alpha blending rather than a 0.5 cutout. Must stay a
	/// single property: divergent copies give a blending PSO with alpha 1, i.e. an opaque overlay.
	/// The threshold is softer than the shadow-caster one (0.1 vs 0.25) on purpose.</summary>
	public bool IsSoftBlend =>
		AlphaMode == MaterialAlphaMode.Blend && SoftAlphaFraction > 0.1f && TransmissionFactor <= 0f;
}

