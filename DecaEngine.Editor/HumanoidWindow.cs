using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Graphics;
using Engine.ImGui.Core;
using Hexa.NET.ImGui;
using DecaEngine.Animation;

namespace DecaEngine.Editor;

/// <summary>
/// Окно разметки humanoid: соответствие слотов скелета человека костям конкретного рига (см.
/// <see cref="HumanoidAvatar"/>).
///
/// Работает с ВЫДЕЛЕННОЙ в сцене сущностью, а не с отдельно открытой моделью: разметку делают, глядя
/// на персонажа, и подсветка выбранной кости во вьюпорте - половина смысла этого окна. Отдельный
/// режим «открыть модель для разметки» означал бы второй вьюпорт и вторую копию сцены ради того же
/// самого.
///
/// Правки живут в РАБОЧЕЙ КОПИИ и уезжают в сцену только по «Сохранить». Это не осторожность:
/// разметка - свойство рига, её подхватывают все персонажи этой модели разом, и живая правка
/// перестраивала бы им ноги и рэгдоллы на каждый клик по выпадающему списку.
/// </summary>
public class HumanoidWindow : ImGuiDockingWindow
{
	private readonly PrefabSceneViewport _viewport;

	private HumanoidAvatar _avatar = new();
	private string _modelPath = string.Empty;
	private PreparedSkeleton? _skeleton;

	private List<HumanoidIssue> _issues = new();
	private bool _dirty;
	private string _status = string.Empty;

	public HumanoidWindow(string title, PrefabSceneViewport viewport, ImGuiRender imGuiRender)
		: base(title, imGuiRender)
	{
		_viewport = viewport;
	}

	protected override void OnRender(uint dockId)
	{
		var (skeleton, modelPath, name) = _viewport.SelectedSkinnedModel;

		// Подсветка гасится в начале кадра и зажигается наведением на слот ниже: иначе она осталась
		// бы висеть на кости, с которой мышь давно ушла.
		_viewport.HighlightJoint = string.Empty;

		if (skeleton == null || string.IsNullOrEmpty(modelPath))
		{
			_skeleton = null;
			ImGui.TextDisabled("Выделите в сцене сущность со скиннед-моделью.");
			ImGui.TextWrapped("Разметка делается по конкретному ригу, и без модели размечать нечего.");
			return;
		}

		if (!ReferenceEquals(skeleton, _skeleton) ||
			!string.Equals(modelPath, _modelPath, StringComparison.OrdinalIgnoreCase))
		{
			Load(skeleton, modelPath);
		}

		ImGui.Text($"Модель: {name}");
		ImGui.Text($"Костей в риге: {skeleton.JointCount}");

		if (_viewport.IsAvatarAuto(modelPath))
		{
			// Различать сохранённую разметку и догадку обязательно: по автоматической уже работают
			// foot IK и рэгдолл, и человек вправе знать, что за них решил автомат.
			ImGui.TextColored(new Vector4(1f, 0.75f, 0.25f, 1f),
				"Разметка автоматическая - файл аватара ещё не сохранён.");
		}

		// Состояние референсной позы - рядом с моделью, а не в подсказке: без неё ретаргетинг
		// работать не будет вовсе, и это должно быть видно до того, как за него возьмутся.
		if (!_avatar.HasReferencePose)
		{
			ImGui.TextColored(new Vector4(1f, 0.75f, 0.25f, 1f),
				"Референсная поза не снята - ретаргетинг по этому аватару невозможен.");
		}
		else
		{
			var pose = HumanoidReferencePose.Evaluate(_avatar, skeleton);
			var color = pose.LooksLikeTPose
				? new Vector4(0.4f, 0.9f, 0.45f, 1f)
				: new Vector4(1f, 0.75f, 0.25f, 1f);

			ImGui.TextColored(color, $"Референсная поза: {_avatar.ReferenceLocals.Count} костей, " +
				$"отклонение от T до {pose.Worst:0.#}°" +
				$"{(pose.Complete ? "" : " (цепочки размечены не полностью)")}");
		}

		DrawToolbar();
		ImGui.Separator();
		DrawSlots(skeleton);
		ImGui.Separator();
		DrawIssues();
	}

	private void Load(PreparedSkeleton skeleton, string modelPath)
	{
		_skeleton = skeleton;
		_modelPath = modelPath;

		// Рабочая копия берётся из файла, а если его нет - из автомата: ровно то же, чем пользуется
		// сцена (см. PrefabSceneViewport.AvatarFor), иначе окно показывало бы не ту разметку, по
		// которой персонаж уже двигается.
		_avatar = (HumanoidAvatarAsset.Load(modelPath) ?? HumanoidAutoMap.Build(skeleton)).Clone();
		_dirty = false;
		_status = string.Empty;

		Revalidate();
	}

	private void DrawToolbar()
	{
		if (ImGui.Button("Разметить автоматически"))
		{
			_avatar = HumanoidAutoMap.Build(_skeleton!);
			_dirty = true;
			_status = "Разметка пересобрана автоматом.";
			Revalidate();
		}

		ImGui.SameLine();

		if (ImGui.Button("Поменять стороны"))
		{
			SwapSides();
			_dirty = true;
			_status = "Левая и правая стороны поменяны местами.";
			Revalidate();
		}

		// Кнопка не лишняя: сторону автомат берёт из имени кости, а когда имена молчат - из знака X,
		// то есть из соглашения «персонаж смотрит вдоль +Z». Модель, экспортированная развёрнутой,
		// этому соглашению не следует, и определить это можно только глазами.
		ImGui.SameLine();

		// Референсная поза - опора ретаргетинга (см. HumanoidReferencePose). Снимается из bind-позы:
		// поза, которую редактор показывает на паузе, - это уже результат клипа, и снять её значило
		// бы записать в референс кадр анимации.
		if (ImGui.Button("Снять T-позу"))
		{
			HumanoidReferencePose.CaptureFromBind(_avatar, _skeleton!);
			_dirty = true;

			var captured = HumanoidReferencePose.Evaluate(_avatar, _skeleton!);
			_status = $"Референсная поза снята из bind: отклонение до {captured.Worst:0.#}° " +
				$"({(captured.LooksLikeTPose ? "похоже на T-позу" : "на T-позу НЕ похоже")}).";
		}

		ImGui.SameLine();

		if (ImGui.Button("Очистить"))
		{
			_avatar.Clear();
			_dirty = true;
			_status = "Разметка очищена.";
			Revalidate();
		}

		ImGui.SameLine();

		if (ImGui.Button("Перечитать"))
		{
			Load(_skeleton!, _modelPath);
			_status = "Разметка перечитана с диска.";
		}

		ImGui.SameLine();

		bool fatal = _issues.Exists(issue => issue.Fatal);

		if (fatal)
		{
			ImGui.BeginDisabled();
		}

		if (ImGui.Button("Сохранить"))
		{
			HumanoidAvatarAsset.Save(_avatar, _modelPath);

			// Сброс кеша сцены - обязателен: без него персонажи продолжат жить по разметке, которую
			// только что заменили, и правка выглядела бы как «сохранилось, но ничего не изменилось».
			_viewport.InvalidateAvatar(_modelPath);

			_dirty = false;
			_status = $"Сохранено: {HumanoidAvatarAsset.PathFor(_modelPath)}";
		}

		if (fatal)
		{
			ImGui.EndDisabled();

			ImGui.SameLine();
			ImGui.TextColored(new Vector4(1f, 0.4f, 0.35f, 1f), "есть ошибки - сохранение запрещено");
		}
		else if (_dirty)
		{
			ImGui.SameLine();
			ImGui.TextColored(new Vector4(1f, 0.75f, 0.25f, 1f), "есть несохранённые правки");
		}

		if (_status.Length > 0)
		{
			ImGui.TextDisabled(_status);
		}
	}

	private void DrawSlots(PreparedSkeleton skeleton)
	{
		if (!ImGui.BeginChild("HumanoidSlots", new Vector2(0f, ImGui.GetContentRegionAvail().Y - 140f * _scale),
			ImGuiChildFlags.Borders))
		{
			ImGui.EndChild();
			return;
		}

		foreach (var info in HumanoidBones.All)
		{
			string current = _avatar[info.Bone];
			bool missing = info.Required && current.Length == 0;

			ImGui.PushID((int)info.Bone);

			if (missing)
			{
				ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.35f, 1f));
			}

			ImGui.Text(info.Required ? $"{info.Title} *" : info.Title);

			if (missing)
			{
				ImGui.PopStyleColor();
			}

			ImGui.SameLine(190f * _scale);
			ImGui.SetNextItemWidth(-1f);

			if (ImGui.BeginCombo("##joint", current.Length > 0 ? current : "-"))
			{
				if (ImGui.Selectable("-", current.Length == 0))
				{
					_avatar[info.Bone] = string.Empty;
					_dirty = true;
					Revalidate();
				}

				for (int joint = 0; joint < skeleton.JointCount; joint++)
				{
					string jointName = skeleton.JointNames[joint];

					if (ImGui.Selectable(jointName, string.Equals(jointName, current, StringComparison.Ordinal)))
					{
						_avatar[info.Bone] = jointName;
						_dirty = true;
						Revalidate();
					}

					// Наведение на пункт списка подсвечивает кость во вьюпорте - так слот выбирают
					// глазами, а не сверяя имена по списку.
					if (ImGui.IsItemHovered())
					{
						_viewport.HighlightJoint = jointName;
					}
				}

				ImGui.EndCombo();
			}
			else if (ImGui.IsItemHovered() && current.Length > 0)
			{
				_viewport.HighlightJoint = current;
			}

			ImGui.PopID();
		}

		ImGui.EndChild();
	}

	private void DrawIssues()
	{
		if (_issues.Count == 0)
		{
			ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.45f, 1f), "Проблем нет.");
			return;
		}

		ImGui.Text($"Проблем: {_issues.Count}");

		foreach (var issue in _issues)
		{
			var color = issue.Fatal ? new Vector4(1f, 0.4f, 0.35f, 1f) : new Vector4(1f, 0.75f, 0.25f, 1f);
			ImGui.TextColored(color, $"{HumanoidBones.Of(issue.Bone).Title}: {issue.Message}");
		}
	}

	private void Revalidate() =>
		_issues = _skeleton != null ? HumanoidValidation.Validate(_avatar, _skeleton) : new List<HumanoidIssue>();

	/// <summary>Меняет местами левые и правые слоты. Пары берутся из справочника по ПОРЯДКУ слотов
	/// (левый блок идёт непосредственно перед правым), а не по разбору имени: имена слотов - это
	/// удобство чтения, и выводить из них логику значит сломать её первым переименованием.</summary>
	private void SwapSides()
	{
		(HumanoidBone Left, HumanoidBone Right)[] pairs =
		[
			(HumanoidBone.LeftShoulder, HumanoidBone.RightShoulder),
			(HumanoidBone.LeftUpperArm, HumanoidBone.RightUpperArm),
			(HumanoidBone.LeftLowerArm, HumanoidBone.RightLowerArm),
			(HumanoidBone.LeftHand, HumanoidBone.RightHand),
			(HumanoidBone.LeftUpperLeg, HumanoidBone.RightUpperLeg),
			(HumanoidBone.LeftLowerLeg, HumanoidBone.RightLowerLeg),
			(HumanoidBone.LeftFoot, HumanoidBone.RightFoot),
			(HumanoidBone.LeftToes, HumanoidBone.RightToes),
		];

		foreach (var (left, right) in pairs)
		{
			(_avatar[left], _avatar[right]) = (_avatar[right], _avatar[left]);
		}
	}
}
