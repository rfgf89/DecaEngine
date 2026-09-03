// DecaOzzShim - DecaEngine <-> ozz-animation bridge.
//
// The boundary is coarse (sample / blend / local-to-model): SoA poses never cross it.
//
// Matrix layout: ozz::math::Float4x4 columns hold the same bytes as a row-major
// System.Numerics.Matrix4x4, so matrices are memcpy'd, never transposed.
//
// ozz reorders joints breadth-first when building a skeleton. Each joint is passed in
// named "<source index>|<name>" so the exact remap is read back, never guessed.

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include "ozz/animation/offline/animation_builder.h"
#include "ozz/animation/offline/raw_animation.h"
#include "ozz/animation/offline/raw_skeleton.h"
#include "ozz/animation/offline/skeleton_builder.h"
#include "ozz/animation/runtime/animation.h"
#include "ozz/animation/runtime/blending_job.h"
#include "ozz/animation/runtime/ik_aim_job.h"
#include "ozz/animation/runtime/ik_two_bone_job.h"
#include "ozz/animation/runtime/local_to_model_job.h"
#include "ozz/animation/runtime/sampling_job.h"
#include "ozz/animation/runtime/skeleton.h"
#include "ozz/base/maths/simd_math.h"
// The IK job headers only forward-declare SimdQuaternion; corrections need the definition.
#include "ozz/base/maths/simd_quaternion.h"
#include "ozz/base/maths/soa_transform.h"
#include "ozz/base/memory/unique_ptr.h"
#include "ozz/base/span.h"

#define DECA_API extern "C" __declspec(dllexport)

namespace {

// Mirrors DecaEngine.Graphics.Transform. The managed boundary is AoS only.
struct DecaOzzTransform {
	float translation[3];
	float rotation[4]; // xyzw
	float scale[3];
};

struct DecaOzzJointDesc {
	const char* name;
	int32_t parent; // -1 for a root; the array must be topologically ordered
	DecaOzzTransform bind;
};

// One track key: translation and scale read xyz, rotation reads xyzw.
struct DecaOzzKey {
	float time;
	float value[4];
};

struct Skeleton {
	ozz::unique_ptr<ozz::animation::Skeleton> skeleton;

	// ozz index -> source index and back; readback uses the first, uploads the second.
	std::vector<int32_t> to_source;
	std::vector<int32_t> from_source;
};

struct Animation {
	ozz::unique_ptr<ozz::animation::Animation> animation;
};

// The sampling context is per-pose, not per-call: it holds ozz's decompressed key cursors.
struct Pose {
	const Skeleton* owner = nullptr;
	ozz::vector<ozz::math::SoaTransform> locals;
	ozz::vector<ozz::math::Float4x4> models;
	ozz::animation::SamplingJob::Context context;
};

ozz::math::Transform ToOzz(const DecaOzzTransform& source) {
	ozz::math::Transform result;
	result.translation = ozz::math::Float3(source.translation[0], source.translation[1], source.translation[2]);
	result.rotation = ozz::math::Quaternion(source.rotation[0], source.rotation[1], source.rotation[2],
											source.rotation[3]);
	result.scale = ozz::math::Float3(source.scale[0], source.scale[1], source.scale[2]);
	return result;
}

// Recursive by necessity: RawSkeleton::Joint holds children by value, so growing that
// vector invalidates pointers to already-filled children.
void FillRawJoint(ozz::animation::offline::RawSkeleton::Joint& destination, int32_t index,
				  const std::vector<std::vector<int32_t>>& children, const DecaOzzJointDesc* joints) {
	// Source-index prefix carries the exact remap across ozz's reorder (see file header).
	char prefix[16];
	std::snprintf(prefix, sizeof(prefix), "%d|", index);
	destination.name = ozz::string(prefix) + ozz::string(joints[index].name ? joints[index].name : "");
	destination.transform = ToOzz(joints[index].bind);

	const std::vector<int32_t>& kids = children[index];
	destination.children.resize(kids.size());
	for (size_t i = 0; i < kids.size(); ++i) {
		FillRawJoint(destination.children[i], kids[i], children, joints);
	}
}

// A single-lane correction can only reach an SoA block by transposing it to AoS and back
// (same trick as ozz's MultiplySoATransformQuaternion sample).
void MultiplySoaRotation(ozz::math::SoaTransform& block, int lane, const ozz::math::SimdQuaternion& correction) {
	ozz::math::SimdQuaternion aos[4];
	ozz::math::Transpose4x4(&block.rotation.x, &aos->xyzw);

	aos[lane] = aos[lane] * correction;

	ozz::math::Transpose4x4(&aos->xyzw, &block.rotation.x);
}

// Rest pose of one joint (ozz index) in AoS form.
ozz::math::Transform RestPoseOf(const ozz::animation::Skeleton& skeleton, int joint) {
	const auto rest = skeleton.joint_rest_poses();
	const ozz::math::SoaTransform& block = rest[joint / 4];
	const int lane = joint % 4;

	float translation[3][4], rotation[4][4], scale[3][4];
	ozz::math::StorePtrU(block.translation.x, translation[0]);
	ozz::math::StorePtrU(block.translation.y, translation[1]);
	ozz::math::StorePtrU(block.translation.z, translation[2]);
	ozz::math::StorePtrU(block.rotation.x, rotation[0]);
	ozz::math::StorePtrU(block.rotation.y, rotation[1]);
	ozz::math::StorePtrU(block.rotation.z, rotation[2]);
	ozz::math::StorePtrU(block.rotation.w, rotation[3]);
	ozz::math::StorePtrU(block.scale.x, scale[0]);
	ozz::math::StorePtrU(block.scale.y, scale[1]);
	ozz::math::StorePtrU(block.scale.z, scale[2]);

	ozz::math::Transform result;
	result.translation = ozz::math::Float3(translation[0][lane], translation[1][lane], translation[2][lane]);
	result.rotation =
		ozz::math::Quaternion(rotation[0][lane], rotation[1][lane], rotation[2][lane], rotation[3][lane]);
	result.scale = ozz::math::Float3(scale[0][lane], scale[1][lane], scale[2][lane]);
	return result;
}

// Seeds empty channels with one bind-pose key.
//
// Required: ozz reads a keyless channel as IDENTITY, while glTF (and our C# sampler) read it
// as "value from the node's pose". Without this a rotation-only bone loses its translation
// and collapses onto its parent's origin.
void SeedEmptyChannels(ozz::animation::offline::RawAnimation& raw, const ozz::animation::Skeleton& skeleton) {
	for (size_t i = 0; i < raw.tracks.size(); ++i) {
		auto& track = raw.tracks[i];
		if (!track.translations.empty() && !track.rotations.empty() && !track.scales.empty()) {
			continue;
		}

		const ozz::math::Transform rest = RestPoseOf(skeleton, static_cast<int>(i));

		if (track.translations.empty()) {
			track.translations.push_back({0.0f, rest.translation});
		}
		if (track.rotations.empty()) {
			track.rotations.push_back({0.0f, rest.rotation});
		}
		if (track.scales.empty()) {
			track.scales.push_back({0.0f, rest.scale});
		}
	}
}

int32_t ParseSourceIndex(const char* name) {
	if (name == nullptr) {
		return -1;
	}

	const char* separator = std::strchr(name, '|');
	if (separator == nullptr) {
		return -1;
	}

	return static_cast<int32_t>(std::strtol(name, nullptr, 10));
}

} // namespace

// --- Skeleton -----------------------------------------------------------------------------------

/// Builds the runtime skeleton. joints must be topologically ordered (parent before child),
/// matching the requirement on C#-side PreparedSkeleton. Returns a handle or nullptr.
DECA_API void* DecaOzz_BuildSkeleton(const DecaOzzJointDesc* joints, int32_t jointCount) {
	if (joints == nullptr || jointCount <= 0) {
		return nullptr;
	}

	std::vector<std::vector<int32_t>> children(static_cast<size_t>(jointCount));
	std::vector<int32_t> roots;

	for (int32_t i = 0; i < jointCount; ++i) {
		const int32_t parent = joints[i].parent;
		if (parent < 0) {
			roots.push_back(i);
		} else if (parent < i) {
			children[static_cast<size_t>(parent)].push_back(i);
		} else {
			// Parent after child: not topological, would silently break the hierarchy.
			return nullptr;
		}
	}

	ozz::animation::offline::RawSkeleton raw;
	raw.roots.resize(roots.size());
	for (size_t i = 0; i < roots.size(); ++i) {
		FillRawJoint(raw.roots[i], roots[i], children, joints);
	}

	if (!raw.Validate()) {
		return nullptr;
	}

	ozz::animation::offline::SkeletonBuilder builder;
	auto built = builder(raw);
	if (!built) {
		return nullptr;
	}

	auto* result = new Skeleton();
	result->skeleton = std::move(built);

	const int num = result->skeleton->num_joints();
	result->to_source.assign(static_cast<size_t>(num), -1);
	result->from_source.assign(static_cast<size_t>(jointCount), -1);

	auto names = result->skeleton->joint_names();
	for (int i = 0; i < num; ++i) {
		const int32_t source = ParseSourceIndex(names[i]);
		if (source < 0 || source >= jointCount) {
			delete result;
			return nullptr;
		}

		result->to_source[static_cast<size_t>(i)] = source;
		result->from_source[static_cast<size_t>(source)] = i;
	}

	return result;
}

DECA_API void DecaOzz_ReleaseSkeleton(void* handle) { delete static_cast<Skeleton*>(handle); }

DECA_API int32_t DecaOzz_SkeletonJointCount(void* handle) {
	auto* skeleton = static_cast<Skeleton*>(handle);
	return skeleton != nullptr ? skeleton->skeleton->num_joints() : 0;
}

/// "ozz index -> source index" table: C# reorders inverse bind matrices and skin bone
/// indices by it, otherwise the palette is shifted by bone (see file header).
DECA_API int32_t DecaOzz_SkeletonRemap(void* handle, int32_t* out, int32_t capacity) {
	auto* skeleton = static_cast<Skeleton*>(handle);
	if (skeleton == nullptr || out == nullptr) {
		return 0;
	}

	const int32_t count = static_cast<int32_t>(skeleton->to_source.size());
	if (capacity < count) {
		return 0;
	}

	std::memcpy(out, skeleton->to_source.data(), sizeof(int32_t) * static_cast<size_t>(count));
	return count;
}

// --- Clip ---------------------------------------------------------------------------------------

/// Builds the runtime clip. Tracks come in SOURCE joint order; the shim remaps them.
/// Each channel is a (keys, count) pair; count 0 means unanimated - joint stays in bind pose.
DECA_API void* DecaOzz_BuildAnimation(void* skeletonHandle, const char* name, float duration,
									  int32_t trackCount, const DecaOzzKey* const* translationKeys,
									  const int32_t* translationCounts, const DecaOzzKey* const* rotationKeys,
									  const int32_t* rotationCounts, const DecaOzzKey* const* scaleKeys,
									  const int32_t* scaleCounts) {
	auto* skeleton = static_cast<Skeleton*>(skeletonHandle);
	if (skeleton == nullptr || duration <= 0.0f || trackCount <= 0) {
		return nullptr;
	}

	const int num = skeleton->skeleton->num_joints();

	ozz::animation::offline::RawAnimation raw;
	raw.name = name != nullptr ? name : "";
	raw.duration = duration;
	raw.tracks.resize(static_cast<size_t>(num));

	for (int32_t source = 0; source < trackCount; ++source) {
		if (source >= static_cast<int32_t>(skeleton->from_source.size())) {
			break;
		}

		const int32_t target = skeleton->from_source[static_cast<size_t>(source)];
		if (target < 0) {
			continue;
		}

		auto& track = raw.tracks[static_cast<size_t>(target)];

		const int32_t translationCount = translationCounts != nullptr ? translationCounts[source] : 0;
		for (int32_t k = 0; k < translationCount; ++k) {
			const DecaOzzKey& key = translationKeys[source][k];
			track.translations.push_back(
				{key.time, ozz::math::Float3(key.value[0], key.value[1], key.value[2])});
		}

		const int32_t rotationCount = rotationCounts != nullptr ? rotationCounts[source] : 0;
		for (int32_t k = 0; k < rotationCount; ++k) {
			const DecaOzzKey& key = rotationKeys[source][k];
			track.rotations.push_back(
				{key.time, ozz::math::Quaternion(key.value[0], key.value[1], key.value[2], key.value[3])});
		}

		const int32_t scaleCount = scaleCounts != nullptr ? scaleCounts[source] : 0;
		for (int32_t k = 0; k < scaleCount; ++k) {
			const DecaOzzKey& key = scaleKeys[source][k];
			track.scales.push_back({key.time, ozz::math::Float3(key.value[0], key.value[1], key.value[2])});
		}
	}

	SeedEmptyChannels(raw, *skeleton->skeleton);

	if (!raw.Validate()) {
		return nullptr;
	}

	ozz::animation::offline::AnimationBuilder builder;
	auto built = builder(raw);
	if (!built) {
		return nullptr;
	}

	auto* result = new Animation();
	result->animation = std::move(built);
	return result;
}

DECA_API void DecaOzz_ReleaseAnimation(void* handle) { delete static_cast<Animation*>(handle); }

DECA_API float DecaOzz_AnimationDuration(void* handle) {
	auto* animation = static_cast<Animation*>(handle);
	return animation != nullptr ? animation->animation->duration() : 0.0f;
}

// --- Pose ---------------------------------------------------------------------------------------

DECA_API void* DecaOzz_CreatePose(void* skeletonHandle) {
	auto* skeleton = static_cast<Skeleton*>(skeletonHandle);
	if (skeleton == nullptr) {
		return nullptr;
	}

	auto* pose = new Pose();
	pose->owner = skeleton;
	pose->locals.resize(static_cast<size_t>(skeleton->skeleton->num_soa_joints()));
	pose->models.resize(static_cast<size_t>(skeleton->skeleton->num_joints()));
	pose->context.Resize(skeleton->skeleton->num_joints());

	// Start in bind pose: the pose may legally be read before the first sample.
	const auto rest = skeleton->skeleton->joint_rest_poses();
	std::memcpy(pose->locals.data(), rest.data(), rest.size_bytes());

	return pose;
}

DECA_API void DecaOzz_ReleasePose(void* handle) { delete static_cast<Pose*>(handle); }

/// Samples a clip into the pose's local TRS. ratio is normalized time [0..1] (ozz
/// convention), not seconds: the caller owns looping and divides by duration itself.
DECA_API int32_t DecaOzz_SamplePose(void* poseHandle, void* animationHandle, float ratio) {
	auto* pose = static_cast<Pose*>(poseHandle);
	auto* animation = static_cast<Animation*>(animationHandle);
	if (pose == nullptr || animation == nullptr) {
		return 0;
	}

	ozz::animation::SamplingJob job;
	job.animation = animation->animation.get();
	job.context = &pose->context;
	job.ratio = ratio;
	job.output = ozz::make_span(pose->locals);

	return job.Run() ? 1 : 0;
}

/// Blends layer poses into the destination. Weights are deliberately not normalized: ozz
/// makes up the difference from the rest pose, and normalizing breaks additive layers.
DECA_API int32_t DecaOzz_BlendPoses(void* destinationHandle, void* const* layerHandles, const float* weights,
									int32_t layerCount) {
	auto* destination = static_cast<Pose*>(destinationHandle);
	if (destination == nullptr || layerHandles == nullptr || weights == nullptr || layerCount <= 0) {
		return 0;
	}

	std::vector<ozz::animation::BlendingJob::Layer> layers(static_cast<size_t>(layerCount));
	for (int32_t i = 0; i < layerCount; ++i) {
		auto* layer = static_cast<Pose*>(layerHandles[i]);
		if (layer == nullptr || layer->owner != destination->owner) {
			// A layer from another skeleton would read foreign memory by foreign indices.
			return 0;
		}

		layers[static_cast<size_t>(i)].transform = ozz::make_span(layer->locals);
		layers[static_cast<size_t>(i)].weight = weights[i];
	}

	ozz::animation::BlendingJob job;
	job.layers = ozz::make_span(layers);
	job.rest_pose = destination->owner->skeleton->joint_rest_poses();
	job.output = ozz::make_span(destination->locals);

	return job.Run() ? 1 : 0;
}

/// Same blend, with per-joint layer weights and additive layers. A non-zero additiveFlags
/// entry routes the layer to additive_layers, where it is applied ON TOP of the result and
/// must therefore hold a DELTA (see AdditiveAnimationBuilder); nullptr means all normal.
/// jointWeights per layer is either nullptr (weight 1 everywhere) or an array in SOURCE
/// joint order. The destination may alias a layer: BlendingJob has no cross-joint deps.
DECA_API int32_t DecaOzz_BlendPosesLayered(void* destinationHandle, void* const* layerHandles,
										   const float* weights, const float* const* jointWeights,
										   const int32_t* additiveFlags, int32_t layerCount) {
	auto* destination = static_cast<Pose*>(destinationHandle);
	if (destination == nullptr || layerHandles == nullptr || weights == nullptr || layerCount <= 0) {
		return 0;
	}

	const auto& remap = destination->owner->to_source;
	const int32_t jointCount = static_cast<int32_t>(remap.size());
	const int32_t soaCount = (jointCount + 3) / 4;

	std::vector<ozz::animation::BlendingJob::Layer> layers;
	std::vector<ozz::animation::BlendingJob::Layer> additive;
	std::vector<std::vector<ozz::math::SimdFloat4>> masks(static_cast<size_t>(layerCount));

	for (int32_t i = 0; i < layerCount; ++i) {
		auto* layer = static_cast<Pose*>(layerHandles[i]);
		if (layer == nullptr || layer->owner != destination->owner) {
			return 0;
		}

		ozz::animation::BlendingJob::Layer entry;
		entry.transform = ozz::make_span(layer->locals);
		entry.weight = weights[i];

		if (jointWeights != nullptr && jointWeights[i] != nullptr) {
			auto& mask = masks[static_cast<size_t>(i)];
			mask.resize(static_cast<size_t>(soaCount));

			for (int32_t soa = 0; soa < soaCount; ++soa) {
				float lanes[4] = {0.f, 0.f, 0.f, 0.f};
				for (int32_t lane = 0; lane < 4; ++lane) {
					const int32_t joint = soa * 4 + lane;
					if (joint < jointCount) {
						lanes[lane] = jointWeights[i][remap[static_cast<size_t>(joint)]];
					}
				}

				mask[static_cast<size_t>(soa)] =
					ozz::math::simd_float4::Load(lanes[0], lanes[1], lanes[2], lanes[3]);
			}

			entry.joint_weights = ozz::make_span(mask);
		}

		if (additiveFlags != nullptr && additiveFlags[i] != 0) {
			additive.push_back(entry);
		} else {
			layers.push_back(entry);
		}
	}

	ozz::animation::BlendingJob job;
	job.layers = ozz::make_span(layers);
	job.additive_layers = ozz::make_span(additive);
	job.rest_pose = destination->owner->skeleton->joint_rest_poses();
	job.output = ozz::make_span(destination->locals);

	return job.Run() ? 1 : 0;
}

DECA_API int32_t DecaOzz_BlendPosesMasked(void* destinationHandle, void* const* layerHandles,
										  const float* weights, const float* const* jointWeights,
										  int32_t layerCount) {
	auto* destination = static_cast<Pose*>(destinationHandle);
	if (destination == nullptr || layerHandles == nullptr || weights == nullptr || layerCount <= 0) {
		return 0;
	}

	const auto& remap = destination->owner->to_source;
	const int32_t jointCount = static_cast<int32_t>(remap.size());
	const int32_t soaCount = (jointCount + 3) / 4;

	std::vector<ozz::animation::BlendingJob::Layer> layers(static_cast<size_t>(layerCount));
	std::vector<std::vector<ozz::math::SimdFloat4>> masks(static_cast<size_t>(layerCount));

	for (int32_t i = 0; i < layerCount; ++i) {
		auto* layer = static_cast<Pose*>(layerHandles[i]);
		if (layer == nullptr || layer->owner != destination->owner) {
			return 0;
		}

		layers[static_cast<size_t>(i)].transform = ozz::make_span(layer->locals);
		layers[static_cast<size_t>(i)].weight = weights[i];

		if (jointWeights != nullptr && jointWeights[i] != nullptr) {
			auto& mask = masks[static_cast<size_t>(i)];
			mask.resize(static_cast<size_t>(soaCount));

			for (int32_t soa = 0; soa < soaCount; ++soa) {
				float lanes[4] = {0.f, 0.f, 0.f, 0.f};
				for (int32_t lane = 0; lane < 4; ++lane) {
					const int32_t joint = soa * 4 + lane;
					if (joint < jointCount) {
						lanes[lane] = jointWeights[i][remap[static_cast<size_t>(joint)]];
					}
				}

				mask[static_cast<size_t>(soa)] =
					ozz::math::simd_float4::Load(lanes[0], lanes[1], lanes[2], lanes[3]);
			}

			layers[static_cast<size_t>(i)].joint_weights = ozz::make_span(mask);
		}
	}

	ozz::animation::BlendingJob job;
	job.layers = ozz::make_span(layers);
	job.rest_pose = destination->owner->skeleton->joint_rest_poses();
	job.output = ozz::make_span(destination->locals);

	return job.Run() ? 1 : 0;
}

DECA_API int32_t DecaOzz_LocalToModel(void* poseHandle) {
	auto* pose = static_cast<Pose*>(poseHandle);
	if (pose == nullptr) {
		return 0;
	}

	ozz::animation::LocalToModelJob job;
	job.skeleton = pose->owner->skeleton.get();
	job.input = ozz::make_span(pose->locals);
	job.output = ozz::make_span(pose->models);

	return job.Run() ? 1 : 0;
}

/// Reads model matrices back in SOURCE joint order. The copy is byte-wise: Float4x4 and
/// System.Numerics.Matrix4x4 share a layout (see file header).
DECA_API int32_t DecaOzz_ReadModelMatrices(void* poseHandle, float* out, int32_t jointCapacity) {
	auto* pose = static_cast<Pose*>(poseHandle);
	if (pose == nullptr || out == nullptr) {
		return 0;
	}

	const auto& remap = pose->owner->to_source;
	const int32_t count = static_cast<int32_t>(remap.size());
	if (jointCapacity < count) {
		return 0;
	}

	for (int32_t i = 0; i < count; ++i) {
		std::memcpy(out + remap[static_cast<size_t>(i)] * 16, &pose->models[static_cast<size_t>(i)],
					sizeof(ozz::math::Float4x4));
	}

	return count;
}

/// Local TRS in AoS form and SOURCE order - input of the procedural layer. Unpacking stays
/// native: the SoA layout is ozz's own business.
DECA_API int32_t DecaOzz_ReadLocalTransforms(void* poseHandle, DecaOzzTransform* out, int32_t jointCapacity) {
	auto* pose = static_cast<Pose*>(poseHandle);
	if (pose == nullptr || out == nullptr) {
		return 0;
	}

	const auto& remap = pose->owner->to_source;
	const int32_t count = static_cast<int32_t>(remap.size());
	if (jointCapacity < count) {
		return 0;
	}

	for (int32_t i = 0; i < count; ++i) {
		const ozz::math::SoaTransform& soa = pose->locals[static_cast<size_t>(i / 4)];
		const int lane = i % 4;

		float translation[4][4], rotation[4][4], scale[4][4];
		ozz::math::StorePtrU(soa.translation.x, translation[0]);
		ozz::math::StorePtrU(soa.translation.y, translation[1]);
		ozz::math::StorePtrU(soa.translation.z, translation[2]);
		ozz::math::StorePtrU(soa.rotation.x, rotation[0]);
		ozz::math::StorePtrU(soa.rotation.y, rotation[1]);
		ozz::math::StorePtrU(soa.rotation.z, rotation[2]);
		ozz::math::StorePtrU(soa.rotation.w, rotation[3]);
		ozz::math::StorePtrU(soa.scale.x, scale[0]);
		ozz::math::StorePtrU(soa.scale.y, scale[1]);
		ozz::math::StorePtrU(soa.scale.z, scale[2]);

		DecaOzzTransform& destination = out[remap[static_cast<size_t>(i)]];
		destination.translation[0] = translation[0][lane];
		destination.translation[1] = translation[1][lane];
		destination.translation[2] = translation[2][lane];
		destination.rotation[0] = rotation[0][lane];
		destination.rotation[1] = rotation[1][lane];
		destination.rotation[2] = rotation[2][lane];
		destination.rotation[3] = rotation[3][lane];
		destination.scale[0] = scale[0][lane];
		destination.scale[1] = scale[1][lane];
		destination.scale[2] = scale[2][lane];
	}

	return count;
}

/// Writes procedurally edited local TRS back into the SoA pose.
DECA_API int32_t DecaOzz_WriteLocalTransforms(void* poseHandle, const DecaOzzTransform* in, int32_t jointCount) {
	auto* pose = static_cast<Pose*>(poseHandle);
	if (pose == nullptr || in == nullptr) {
		return 0;
	}

	const auto& remap = pose->owner->to_source;
	const int32_t count = static_cast<int32_t>(remap.size());
	if (jointCount < count) {
		return 0;
	}

	// Tail lanes of the last SoA block get identity: a NaN there corrupts the three live
	// bones sharing the SIMD register.
	const size_t soaCount = pose->locals.size();
	for (size_t block = 0; block < soaCount; ++block) {
		float translation[3][4] = {}, rotation[4][4] = {}, scale[3][4] = {};
		for (int lane = 0; lane < 4; ++lane) {
			const int32_t joint = static_cast<int32_t>(block) * 4 + lane;
			if (joint >= count) {
				rotation[3][lane] = 1.0f;
				scale[0][lane] = scale[1][lane] = scale[2][lane] = 1.0f;
				continue;
			}

			const DecaOzzTransform& source = in[remap[static_cast<size_t>(joint)]];
			translation[0][lane] = source.translation[0];
			translation[1][lane] = source.translation[1];
			translation[2][lane] = source.translation[2];
			rotation[0][lane] = source.rotation[0];
			rotation[1][lane] = source.rotation[1];
			rotation[2][lane] = source.rotation[2];
			rotation[3][lane] = source.rotation[3];
			scale[0][lane] = source.scale[0];
			scale[1][lane] = source.scale[1];
			scale[2][lane] = source.scale[2];
		}

		ozz::math::SoaTransform& destination = pose->locals[block];
		destination.translation.x = ozz::math::simd_float4::LoadPtrU(translation[0]);
		destination.translation.y = ozz::math::simd_float4::LoadPtrU(translation[1]);
		destination.translation.z = ozz::math::simd_float4::LoadPtrU(translation[2]);
		destination.rotation.x = ozz::math::simd_float4::LoadPtrU(rotation[0]);
		destination.rotation.y = ozz::math::simd_float4::LoadPtrU(rotation[1]);
		destination.rotation.z = ozz::math::simd_float4::LoadPtrU(rotation[2]);
		destination.rotation.w = ozz::math::simd_float4::LoadPtrU(rotation[3]);
		destination.scale.x = ozz::math::simd_float4::LoadPtrU(scale[0]);
		destination.scale.y = ozz::math::simd_float4::LoadPtrU(scale[1]);
		destination.scale.z = ozz::math::simd_float4::LoadPtrU(scale[2]);
	}

	return count;
}

// --- IK -----------------------------------------------------------------------------------------

/// Two-bone IK (leg, arm): rotates start and mid so that end reaches target. Joint indices
/// are SOURCE indices. Needs up-to-date model matrices and writes the local pose, so the
/// call order is Sample -> LocalToModel -> TwoBoneIk -> LocalToModel.
DECA_API int32_t DecaOzz_TwoBoneIk(void* poseHandle, int32_t startJoint, int32_t midJoint, int32_t endJoint,
								   const float* target, const float* poleVector, const float* midAxis,
								   float weight, float soften, float twistAngle) {
	auto* pose = static_cast<Pose*>(poseHandle);
	if (pose == nullptr || target == nullptr || poleVector == nullptr || midAxis == nullptr) {
		return 0;
	}

	const auto& from = pose->owner->from_source;
	if (startJoint < 0 || midJoint < 0 || endJoint < 0 || startJoint >= static_cast<int32_t>(from.size()) ||
		midJoint >= static_cast<int32_t>(from.size()) || endJoint >= static_cast<int32_t>(from.size())) {
		return 0;
	}

	const int32_t start = from[static_cast<size_t>(startJoint)];
	const int32_t mid = from[static_cast<size_t>(midJoint)];
	const int32_t end = from[static_cast<size_t>(endJoint)];
	if (start < 0 || mid < 0 || end < 0) {
		return 0;
	}

	ozz::math::SimdQuaternion startCorrection, midCorrection;

	ozz::animation::IKTwoBoneJob job;
	job.target = ozz::math::simd_float4::Load3PtrU(target);
	job.pole_vector = ozz::math::simd_float4::Load3PtrU(poleVector);
	job.mid_axis = ozz::math::simd_float4::Load3PtrU(midAxis);
	job.weight = weight;
	job.soften = soften;
	job.twist_angle = twistAngle;
	job.start_joint = &pose->models[static_cast<size_t>(start)];
	job.mid_joint = &pose->models[static_cast<size_t>(mid)];
	job.end_joint = &pose->models[static_cast<size_t>(end)];
	job.start_joint_correction = &startCorrection;
	job.mid_joint_correction = &midCorrection;

	if (!job.Run()) {
		return 0;
	}

	// Corrections are model-space deltas applied to local rotations: the local pose, not the
	// model matrices, is the source of truth for later stages.
	MultiplySoaRotation(pose->locals[static_cast<size_t>(start / 4)], start & 3, startCorrection);
	MultiplySoaRotation(pose->locals[static_cast<size_t>(mid / 4)], mid & 3, midCorrection);
	return 1;
}

/// Aim IK: rotates one joint so that its forward axis points at the target.
DECA_API int32_t DecaOzz_AimIk(void* poseHandle, int32_t joint, const float* target, const float* forward,
							   const float* up, const float* poleVector, float weight) {
	auto* pose = static_cast<Pose*>(poseHandle);
	if (pose == nullptr || target == nullptr || forward == nullptr || up == nullptr || poleVector == nullptr) {
		return 0;
	}

	const auto& from = pose->owner->from_source;
	if (joint < 0 || joint >= static_cast<int32_t>(from.size())) {
		return 0;
	}

	const int32_t target_joint = from[static_cast<size_t>(joint)];
	if (target_joint < 0) {
		return 0;
	}

	ozz::math::SimdQuaternion correction;

	ozz::animation::IKAimJob job;
	job.target = ozz::math::simd_float4::Load3PtrU(target);
	job.forward = ozz::math::simd_float4::Load3PtrU(forward);
	job.up = ozz::math::simd_float4::Load3PtrU(up);
	job.pole_vector = ozz::math::simd_float4::Load3PtrU(poleVector);
	job.weight = weight;
	job.joint = &pose->models[static_cast<size_t>(target_joint)];
	job.joint_correction = &correction;

	if (!job.Run()) {
		return 0;
	}

	MultiplySoaRotation(pose->locals[static_cast<size_t>(target_joint / 4)], target_joint & 3, correction);
	return 1;
}
