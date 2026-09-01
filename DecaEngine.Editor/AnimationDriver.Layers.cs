using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Animation;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Physics;
using DecaEngine.Scene;
using Friflo.Engine.ECS;

// В Friflo есть свой Transform-компонент, а поза скелета оперирует TRS движка - без явного алиаса
// имя разрешается неоднозначно.
using Transform = DecaEngine.Core.Transform;

namespace DecaEngine.Editor;

/// <summary>Слои поверх базового клипа: overlay, аддитивный клип и look-at. Часть <see cref="AnimationDriver"/> - файл на тему; состояние
/// персонажа (Character) и кадровый Update живут в основном файле.</summary>
public sealed partial class AnimationDriver
{
	/// <summary>
	/// Частичный бленд: поддерево от корневой кости играет свой клип поверх базовой позы (ozz
	/// partial_blend). Идёт ПОСЛЕ базы (клип или локомоушен) и ДО look-at/foot IK: те правят уже
	/// смешанную позу. Веса - ПОСУСТАВНЫЕ и комплементарные (сумма на каждом суставе единица),
	/// поэтому вне поддерева база проходит нетронутой побитово, а rest-поза ozz не подмешивается.
	/// </summary>
	private void ApplyOverlayClip(Entity entity, Character character, float deltaSeconds)
	{
		if (character.Pose == null || !entity.HasComponent<OverlayClipComponent>())
		{
			return;
		}

		var settings = entity.GetComponent<OverlayClipComponent>();
		float weight = Math.Clamp(settings.Weight, 0f, 1f);

		if (!settings.Enabled || weight <= 0f || string.IsNullOrEmpty(settings.ClipName))
		{
			return;
		}

		if (!string.Equals(settings.ClipName, character.OverlayClipName, StringComparison.Ordinal))
		{
			character.OverlayClipName = settings.ClipName;
			character.OverlayClip = FindClip(character, settings.ClipName);
			character.OverlayTime = 0f;
		}

		if (character.OverlayClip == null)
		{
			return;
		}

		var clip = GetOzzClip(character, character.OverlayClip);
		if (clip == null || clip.Duration <= 0f)
		{
			return;
		}

		character.OverlayPose ??= OzzPose.Create(character.Ozz);
		if (character.OverlayPose == null)
		{
			return;
		}

		// Корень поддерева: авторское имя старше разметки, как везде. Слот по умолчанию - грудь;
		// у четвероногого она несёт передние лапы, и для «оглядывается» автор ставит шею.
		int root = character.Skeleton.FindJoint(
			JointOf(character, settings.RootJoint, HumanoidBone.Chest));
		if (root < 0)
		{
			return;
		}

		EnsureOverlayMasks(character, root, weight);

		if (deltaSeconds > 0f)
		{
			character.OverlayTime += deltaSeconds * MathF.Max(settings.Speed, 0f);
			character.OverlayTime = settings.Loop
				? character.OverlayTime % clip.Duration
				: MathF.Min(character.OverlayTime, clip.Duration);
		}

		// Приёмник бленда намеренно совпадает со слоем базы: ozz пишет выход посуставно после
		// чтения слоёв того же сустава (см. OzzPose.Blend), и отдельная копия позы не нужна.
		bool ok =
			character.OverlayPose.Sample(clip, character.OverlayTime) &&
			character.Pose.Blend([character.Pose, character.OverlayPose], [1f, 1f],
				[character.OverlayMaskBase, character.OverlayMaskLayer]) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals);

		if (ok)
		{
			character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		}
	}

	/// <summary>
	/// Аддитивный слой: дельта клипа (см. <see cref="AdditiveClip"/>) суммируется поверх текущей
	/// позы через additive_layers ozz. Идёт ПОСЛЕ overlay (дельта ложится и на его результат) и
	/// ДО look-at/foot IK. База не участвует в усреднении - слой чистая добавка, и вес просто
	/// масштабирует её к единичной трансформации.
	/// </summary>
	private void ApplyAdditiveClip(Entity entity, Character character, float deltaSeconds)
	{
		if (character.Pose == null || !entity.HasComponent<AdditiveClipComponent>())
		{
			return;
		}

		var settings = entity.GetComponent<AdditiveClipComponent>();
		float weight = Math.Clamp(settings.Weight, 0f, 1f);

		if (!settings.Enabled || weight <= 0f || string.IsNullOrEmpty(settings.ClipName))
		{
			return;
		}

		if (!string.Equals(settings.ClipName, character.AdditiveClipName, StringComparison.Ordinal))
		{
			character.AdditiveClipName = settings.ClipName;
			character.AdditiveSource = FindClip(character, settings.ClipName);
			character.AdditiveDelta = character.AdditiveSource != null
				? AdditiveClip.Build(character.AdditiveSource, character.Skeleton)
				: null;
			character.AdditiveTime = 0f;
		}

		if (character.AdditiveDelta == null)
		{
			return;
		}

		var clip = GetOzzClip(character, character.AdditiveDelta);
		if (clip == null || clip.Duration <= 0f)
		{
			return;
		}

		character.AdditivePose ??= OzzPose.Create(character.Ozz);
		if (character.AdditivePose == null)
		{
			return;
		}

		if (deltaSeconds > 0f)
		{
			character.AdditiveTime += deltaSeconds * MathF.Max(settings.Speed, 0f);
			character.AdditiveTime = settings.Loop
				? character.AdditiveTime % clip.Duration
				: MathF.Min(character.AdditiveTime, clip.Duration);
		}

		bool ok =
			character.AdditivePose.Sample(clip, character.AdditiveTime) &&
			character.Pose.BlendLayered([character.Pose, character.AdditivePose], [1f, weight],
				[null, null], [false, true]) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals);

		if (ok)
		{
			character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		}
	}

	/// <summary>Перестраивает посуставные веса при смене корня или веса. Принадлежность поддереву -
	/// подъёмом по родителям: скелет в два-три десятка костей, и кэшировать тут нечего.</summary>
	private static void EnsureOverlayMasks(Character character, int root, float weight)
	{
		if (character.OverlayRoot == root && character.OverlayWeight == weight &&
			character.OverlayMaskBase != null && character.OverlayMaskLayer != null)
		{
			return;
		}

		int jointCount = character.Skeleton.JointCount;
		character.OverlayMaskBase ??= new float[jointCount];
		character.OverlayMaskLayer ??= new float[jointCount];

		for (int joint = 0; joint < jointCount; joint++)
		{
			bool inSubtree = false;
			for (int j = joint; j >= 0; j = character.Skeleton.Parents[j])
			{
				if (j == root)
				{
					inSubtree = true;
					break;
				}
			}

			character.OverlayMaskBase[joint] = inSubtree ? 1f - weight : 1f;
			character.OverlayMaskLayer[joint] = inSubtree ? weight : 0f;
		}

		character.OverlayRoot = root;
		character.OverlayWeight = weight;
	}

	private static void ApplyLookAt(Entity entity, Character character)
	{
		if (character.Pose == null || !entity.HasComponent<LookAtComponent>())
		{
			return;
		}

		var lookAt = entity.GetComponent<LookAtComponent>();
		if (!lookAt.Enabled || lookAt.Weight <= 0f || string.IsNullOrEmpty(lookAt.Joint))
		{
			return;
		}

		int joint = character.Skeleton.FindJoint(lookAt.Joint);
		if (joint < 0)
		{
			return;
		}

		// Цель приходит МИРОВОЙ, а IK работает в пространстве модели. Здесь они совпадают: сущность
		// префаба ставит модель своим трансформом, а поза считается в её локальном пространстве -
		// перевод появится вместе с поддержкой смещённых персонажей.
		if (character.Pose.AimIk(joint, lookAt.Target, lookAt.Forward, lookAt.Up, lookAt.Up, lookAt.Weight) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals))
		{
			character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		}

		character.HasLookAt = true;
		character.LookAtTarget = lookAt.Target;
		character.LookAtJoint = joint;
	}

	// --- Foot IK -----------------------------------------------------------------------------------

}
