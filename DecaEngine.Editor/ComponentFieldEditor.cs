using System.Numerics;
using System.Reflection;
using DecaEngine.Core.Assets;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor
{
	/// <summary>
	/// ????????????? (reflection-based) ???????? ????? ??????????? ??? ??????????. ??????
	/// ?????????? ???????? ??? ?????? ImGui-????????, ???????? ??????? ?? CLR-???? ????:
	/// <c>bool</c> ? ???????/toggle, ????? ? Drag*, <c>enum</c> ? Combo (??? ????? ????????? ???
	/// <see cref="FlagsAttribute"/>), <see cref="Vector2"/>/<see cref="Vector3"/>/
	/// <see cref="Vector4"/>/<see cref="Quaternion"/> ? DragFloatN, ????????? struct/??????? ?
	/// ????????? ?????????? (TreeNodeEx), ??? ????????? - ??? read-only ?????.
	/// </summary>
	/// <remarks>
	/// ??? ??? ???????? ??? ?????? ?????????? ????? ??????????? ?? ????? ??????????:
	/// <list type="number">
	/// <item>??????? ???????? ?????????? ???????? ??? ? ???? boxed <see cref="object"/> ?????
	/// <see cref="EntityComponent.Value"/> (??. <see cref="Entity.Components"/>).</item>
	/// <item>???? ????? boxed-??????? ????????????? ????? FieldInfo.SetValue ?????
	/// ? ????? boxed-?????????? (??? ??????????? ? ?????????? ?????: boxed struct - ??? ?????????
	/// ?????? ? ????, ? SetValue ????? ???????? ? ??? ??????, ??? ?????????????? ???????????).</item>
	/// <item>???? ???-?? ?????????? - ???? boxed-?????? ??????? ???????????? ??????? ? ????????
	/// ????? <see cref="EntityUtils.AddEntityComponentValue"/> (???? ?? ??????? ?????? ????
	/// ?????????? ?? ????? ?????????? - ????????? <see cref="ComponentType"/> + <see cref="object"/>).</item>
	/// </list>
	/// </remarks>
	public static class ComponentFieldEditor
	{
		/// <summary>
		/// ?????? ??? ?????????? ???????? (????? ????????????? ? <paramref name="exclude"/> - ??????
		/// ??? ??, ??? ??? ?????????? ????????? ?????????????????? UI, ??. Transform-????? ?
		/// <see cref="InspectorWindow"/>), ?????? - ??? ?????????
		/// ????????????? ???? ? ???-???? "Remove Component". ?????????? true, ???? ???-?? ??????????
		/// (???????? ???? ??????????????? ???? ????????? ??????).
		/// </summary>
		public static bool DrawComponents(Entity entity, ISet<Type>? exclude = null, string filter = "")
		{
			bool anyChanged = false;

			// ????????????? ? ??????: ?? ????? ???????? ????? ???? ????????? ???????? ??????????,
			// ? EntityComponents - ????? span/enumerator ?????? ???????? ????????? ????????.
			var components = new List<EntityComponent>(entity.Components);

			foreach (var ec in components)
			{
				var clrType = ec.Type.Type;
				if (clrType != null && exclude != null && exclude.Contains(clrType))
				{
					continue;
				}

				var displayName = ec.Type.Name ?? clrType?.Name ?? "Component";
				if (!string.IsNullOrEmpty(filter) && !displayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				ImGui.PushID(ec.Type.StructIndex);

				bool removeRequested = false;
				bool open = ImGui.CollapsingHeader(displayName, ImGuiTreeNodeFlags.DefaultOpen);

				if (ImGui.BeginPopupContextItem("##ComponentCtx"))
				{
					if (ImGui.MenuItem("Remove Component", string.Empty))
					{
						removeRequested = true;
					}
					ImGui.EndPopup();
				}

				if (open && !removeRequested && clrType != null)
				{
					ImGui.Indent();
					// EntityComponent.Value ??????? Obsolete ? ?????? ???????????????
					// Entity.GetComponent<T>() - ?? ? ???? reflection-based ????????? ??? T
					// ??????? ?????????? (?? ???????????? ?????? ? ???????? ????? ComponentType),
					// ??? ??? ?????????????? API ????? ????????????? ??????????.
#pragma warning disable CS0618
					object boxed = ec.Value;
#pragma warning restore CS0618
					if (DrawObjectFields(boxed, clrType))
					{
						EntityUtils.AddEntityComponentValue(entity, ec.Type, boxed);
						anyChanged = true;
					}
					ImGui.Unindent();
				}
				else if (open && clrType == null)
				{
					ImGui.Indent();
					ImGui.TextDisabled("Unresolved component type - no CLR type available.");
					ImGui.Unindent();
				}

				ImGui.PopID();

				if (removeRequested)
				{
					EntityUtils.RemoveEntityComponent(entity, ec.Type);
					anyChanged = true;
				}
			}

			return anyChanged;
		}

		/// <summary>?????? ????????? ???? <paramref name="type"/> ????? ?????? ??? ????????????? boxed-??????????.</summary>
		private static bool DrawObjectFields(object boxed, Type type)
		{
			bool changed = false;
			foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
			{
				object? value = field.GetValue(boxed);
				if (DrawField(field.Name, field.FieldType, ref value, field.GetCustomAttribute<AssetTypeAttribute>()))
				{
					field.SetValue(boxed, value);
					changed = true;
				}
			}
			return changed;
		}

		/// <summary>?????? ???? ?????? ??? ???????? ???? <paramref name="type"/>; ?????????? true, ???? ???????? ??????????.</summary>
		private static bool DrawField(string label, Type type, ref object? value, AssetTypeAttribute? assetType = null)
		{
			if (type.IsPointer || type == typeof(IntPtr) || type == typeof(UIntPtr))
			{
				ImGui.TextDisabled($"{label}: <native pointer>");
				return false;
			}

			if (type == typeof(bool))
			{
				bool v = value is bool b && b;
				if (ImGui.Checkbox(label, ref v))
				{
					value = v;
					return true;
				}
				return false;
			}

			if (type == typeof(int))
			{
				int v = value is int i ? i : 0;
				if (ImGui.DragInt(label, ref v))
				{
					value = v;
					return true;
				}
				return false;
			}

			if (type == typeof(uint))
			{
				int v = value is uint u ? unchecked((int)u) : 0;
				if (ImGui.DragInt(label, ref v))
				{
					value = unchecked((uint)Math.Max(0, v));
					return true;
				}
				return false;
			}

			if (type == typeof(float))
			{
				float v = value is float f ? f : 0f;
				if (ImGui.DragFloat(label, ref v, 0.05f))
				{
					value = v;
					return true;
				}
				return false;
			}

			if (type == typeof(double))
			{
				float v = value is double d ? (float)d : 0f;
				if (ImGui.DragFloat(label, ref v, 0.05f))
				{
					value = (double)v;
					return true;
				}
				return false;
			}

			if (type == typeof(string))
			{
				string v = value as string ?? string.Empty;
				if (ImGui.InputText(label, ref v, 512))
				{
					value = v;
					return true;
				}
				return false;
			}

			if (type == typeof(Vector2))
			{
				Vector2 v = value is Vector2 v2 ? v2 : default;
				if (ImGui.DragFloat2(label, ref v, 0.05f))
				{
					value = v;
					return true;
				}
				return false;
			}

			if (type == typeof(Vector3))
			{
				Vector3 v = value is Vector3 v3 ? v3 : default;
				if (ImGui.DragFloat3(label, ref v, 0.05f))
				{
					value = v;
					return true;
				}
				return false;
			}

			if (type == typeof(Vector4))
			{
				Vector4 v = value is Vector4 v4 ? v4 : default;
				if (ImGui.DragFloat4(label, ref v, 0.05f))
				{
					value = v;
					return true;
				}
				return false;
			}

			if (type == typeof(Quaternion))
			{
				Quaternion q = value is Quaternion qq ? qq : Quaternion.Identity;
				Vector4 v = new(q.X, q.Y, q.Z, q.W);
				if (ImGui.DragFloat4(label, ref v, 0.01f))
				{
					value = new Quaternion(v.X, v.Y, v.Z, v.W);
					return true;
				}
				return false;
			}

			if (type == typeof(AssetRef))
			{
				return DrawAssetRef(label, ref value, assetType);
			}

			if (type.IsEnum)
			{
				return DrawEnum(label, type, ref value);
			}

			if (type.IsArray)
			{
				if (value is not Array array)
				{
					ImGui.TextDisabled($"{label}: null");
					return false;
				}
				return DrawArray(label, type, array, assetType);
			}

			// ???????????? ????????? struct (????????, ????????? ???????????? ???? ?????? ?????
			// CameraData) - ?????????? ?????? ??? ???? ?? ????????? ?????????.
			if (type.IsValueType && !type.IsPrimitive && value != null)
			{
				bool changed = false;
				if (ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
				{
					changed = DrawObjectFields(value, type);
					ImGui.TreePop();
				}
				return changed;
			}

			ImGui.TextDisabled($"{label}: {value} ({type.Name})");
			return false;
		}

		/// <summary>
		/// Draws an <see cref="AssetRef"/> field as a button showing the current asset's file name
		/// (or "(None)"), which also acts as an ImGui drag&amp;drop target: dropping an asset dragged
		/// from AssetBrowserWindow assigns its project-relative path here.
		/// </summary>
		private static bool DrawAssetRef(string label, ref object? value, AssetTypeAttribute? assetType = null)
		{
			var current = value is AssetRef assetRef ? assetRef : default;
			bool changed = false;
			bool hasValue = !current.IsEmpty;

			var frameHeight = ImGui.GetFrameHeight();
			var clearWidth = hasValue ? frameHeight + ImGui.GetStyle().ItemSpacing.X : 0f;
			var slotWidth = MathF.Max(80f, ImGui.GetContentRegionAvail().X - clearWidth);
			var slotSize = new Vector2(slotWidth, frameHeight);

			// Draw label, then the slot, on the same line, without using PushID to avoid stack
			// overflow in nested structures.
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(label);
			ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);

			var slotMin = ImGui.GetCursorScreenPos();
			var slotMax = slotMin + slotSize;

			// Invisible button reserves the layout space and gives us hover/drag-target hit
			// testing; the actual visuals (background, icon, text, border) are hand-drawn below
			// so the field can look like a distinct "asset slot" instead of a plain text button,
			// which was easy to mistake for a disabled/inert control.
			ImGui.InvisibleButton("##AssetRefSlot", slotSize);
			bool hovered = ImGui.IsItemHovered();

			var drawList = ImGui.GetWindowDrawList();
			var rounding = ImGui.GetStyle().FrameRounding;
			drawList.AddRectFilled(slotMin, slotMax, ImGui.GetColorU32(hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg), rounding);

			// File-type icon matching AssetBrowserWindow's icon for the assigned asset (or, when
			// empty, for the type this field accepts) so the slot visually communicates what kind
			// of asset it holds/expects instead of just showing a bare file name.
			const float iconPadding = 2f;
			var iconMin = slotMin + new Vector2(iconPadding, iconPadding);
			var iconMax = new Vector2(iconMin.X + frameHeight - iconPadding * 2f, slotMax.Y - iconPadding);
			var iconKind = hasValue
				? AssetBrowserWindow.GetFileIconKind(current.Path)
				: assetType is { Extensions.Length: > 0 }
					? AssetBrowserWindow.GetFileIconKind(assetType.Extensions[0])
					: AssetBrowserWindow.FileIconKind.Generic;
			AssetBrowserWindow.DrawFileIcon(drawList, iconMin, iconMax, iconKind, 1f);

			var displayText = hasValue ? Path.GetFileName(current.Path) : "Drop asset here...";
			var textColor = ImGui.GetColorU32(hasValue ? ImGuiCol.Text : ImGuiCol.TextDisabled);
			var textMinX = iconMax.X + iconPadding * 2f;
			var textPos = new Vector2(textMinX, slotMin.Y + (frameHeight - ImGui.GetTextLineHeight()) * 0.5f);
			drawList.PushClipRect(new Vector2(textMinX, slotMin.Y), slotMax, true);
			drawList.AddText(textPos, textColor, displayText);
			drawList.PopClipRect();

			// Border drawn noticeably thicker/brighter than a normal frame so the slot reads as a
			// dedicated asset drop target rather than a plain button - brighter still on hover.
			var borderColor = ImGui.GetColorU32(EditorPalette.WithAlpha(EditorPalette.Selection, hovered ? 0.9f : 0.55f));
			drawList.AddRect(slotMin, slotMax, borderColor, rounding, ImDrawFlags.None, hovered ? 2f : 1.5f);

			if (hovered)
			{
				var allowed = assetType is { Extensions.Length: > 0 } ? string.Join(", ", assetType.Extensions) : null;
				if (hasValue && allowed != null)
				{
					ImGui.SetTooltip($"{current.Path}\nAccepts: {allowed}");
				}
				else if (hasValue)
				{
					ImGui.SetTooltip(current.Path);
				}
				else if (allowed != null)
				{
					ImGui.SetTooltip($"Accepts: {allowed}");
				}
			}

			// Handle drag-drop - this is the critical part
			if (ImGui.BeginDragDropTarget())
			{
				var payload = ImGui.AcceptDragDropPayload(DecaEngine.Core.Assets.AssetRef.DragDropPayloadType);
				if (!payload.IsNull && payload.DataSize > 0)
				{
					unsafe
					{
						var bytes = new ReadOnlySpan<byte>(payload.Data, payload.DataSize);
						string droppedPath = System.Text.Encoding.UTF8.GetString(bytes);
						if (!string.IsNullOrEmpty(droppedPath) && (assetType?.Accepts(droppedPath) ?? true))
						{
							current = new DecaEngine.Core.Assets.AssetRef(droppedPath);
							changed = true;
						}
					}
				}
				ImGui.EndDragDropTarget();
			}

			// Clear button - only if there's a value
			if (hasValue)
			{
				ImGui.SameLine(0f, ImGui.GetStyle().ItemSpacing.X);
				if (ImGui.Button("x##AssetRefClear"))
				{
					current = default;
					changed = true;
				}
			}

			if (changed)
			{
				value = current;
			}
			return changed;
		}

		private static bool DrawEnum(string label, Type type, ref object? value)
		{
			var current = value ?? Activator.CreateInstance(type)!;

			if (type.GetCustomAttribute<FlagsAttribute>() != null)
			{
				bool changed = false;
				long currentBits = Convert.ToInt64(current);

				if (ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
				{
					foreach (var name in Enum.GetNames(type))
					{
						long bits = Convert.ToInt64(Enum.Parse(type, name));
						if (bits == 0)
						{
							continue; // "None"/??????? ???????? - ?????? ???????????
						}

						bool set = (currentBits & bits) == bits;
						if (ImGui.Checkbox(name, ref set))
						{
							currentBits = set ? currentBits | bits : currentBits & ~bits;
							changed = true;
						}
					}
					ImGui.TreePop();
				}

				if (changed)
				{
					value = Enum.ToObject(type, currentBits);
				}
				return changed;
			}

			var names = Enum.GetNames(type);
			int index = Array.IndexOf(names, current.ToString());
			if (index < 0)
			{
				index = 0;
			}
			string itemsSeparated = string.Join('\0', names) + '\0';

			if (ImGui.Combo(label, ref index, itemsSeparated))
			{
				value = Enum.Parse(type, names[index]);
				return true;
			}
			return false;
		}

		private static bool DrawArray(string label, Type type, Array array, AssetTypeAttribute? assetType = null)
		{
			var elemType = type.GetElementType()!;
			bool changed = false;

			if (ImGui.TreeNodeEx($"{label} [{array.Length}]", ImGuiTreeNodeFlags.DefaultOpen))
			{
				for (int i = 0; i < array.Length; i++)
				{
					object? elem = array.GetValue(i);
					ImGui.PushID(i);
					if (DrawField($"[{i}]", elemType, ref elem, assetType))
					{
						array.SetValue(elem, i);
						changed = true;
					}
					ImGui.PopID();
				}
				ImGui.TreePop();
			}

			return changed;
		}
	}
}

