using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DecaEngine.Core.Assets;
using DecaEngine.Core.Entities;
using DecaEngine.Core.Prefabs;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor
{
	/// <summary>Reflection-based inspector for arbitrary component types: each public field is
	/// drawn with the ImGui widget matching its CLR type.</summary>
	public static class ComponentFieldEditor
	{
		// Per-type reflection metadata resolved once and reused every frame.
		private sealed class CachedTypeInfo
		{
			public readonly FieldInfo[] Fields;
			public readonly AssetTypeAttribute?[] AssetTypes;

			public CachedTypeInfo(Type type)
			{
				Fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
				AssetTypes = new AssetTypeAttribute?[Fields.Length];
				for (int i = 0; i < Fields.Length; i++)
				{
					AssetTypes[i] = Fields[i].GetCustomAttribute<AssetTypeAttribute>();
				}
			}
		}

		// Per-enum metadata resolved once, including the '\0'-separated ImGui combo string.
		private sealed class CachedEnumInfo
		{
			public readonly string[] Names;
			public readonly object[] Values;
			public readonly long[] Bits;
			public readonly object ZeroValue;
			public readonly string ComboItems;
			public readonly bool IsFlags;

			public CachedEnumInfo(Type type)
			{
				Names = Enum.GetNames(type);
				Values = new object[Names.Length];
				Bits = new long[Names.Length];
				for (int i = 0; i < Names.Length; i++)
				{
					Values[i] = Enum.Parse(type, Names[i]);
					Bits[i] = Convert.ToInt64(Values[i]);
				}
				ZeroValue = Enum.ToObject(type, 0L);
				ComboItems = string.Join('\0', Names) + '\0';
				IsFlags = type.GetCustomAttribute<FlagsAttribute>() != null;
			}
		}

		// ConditionalWeakTable, not a Dictionary: a Type key from a collectible AssemblyLoadContext
		// must not pin the unloaded context forever.
		private static readonly ConditionalWeakTable<Type, CachedTypeInfo> _typeInfoCache = new();
		private static readonly ConditionalWeakTable<Type, CachedEnumInfo> _enumInfoCache = new();

		// Reusable per-frame buffers (editor UI is single-threaded, DrawComponents is not reentrant).
		private static readonly List<EntityComponent> _componentsScratch = new();
		private static readonly List<string> _indexLabels = new();
		private static readonly Dictionary<string, (int Length, string Header)> _arrayHeaders = new(StringComparer.Ordinal);

		/// <summary>Drops all cached reflection metadata; must be called when a new component type
		/// is registered at runtime, or stale FieldInfo from a previous load is reused.</summary>
		public static void InvalidateCaches()
		{
			_typeInfoCache.Clear();
			_enumInfoCache.Clear();
			_arrayHeaders.Clear();
		}

		private static CachedTypeInfo GetTypeInfo(Type type)
		{
			if (!_typeInfoCache.TryGetValue(type, out var info))
			{
				info = new CachedTypeInfo(type);
				_typeInfoCache.AddOrUpdate(type, info);
			}
			return info;
		}

		private static CachedEnumInfo GetEnumInfo(Type type)
		{
			if (!_enumInfoCache.TryGetValue(type, out var info))
			{
				info = new CachedEnumInfo(type);
				_enumInfoCache.AddOrUpdate(type, info);
			}
			return info;
		}

		private static string GetIndexLabel(int index)
		{
			while (_indexLabels.Count <= index)
			{
				_indexLabels.Add($"[{_indexLabels.Count}]");
			}
			return _indexLabels[index];
		}

		/// <summary>Draws every component of the entity except those in <paramref name="exclude"/>;
		/// returns true if anything changed.</summary>
		public static bool DrawComponents(Entity entity, ISet<Type>? exclude = null, string filter = "")
		{
			bool anyChanged = false;

			// Snapshot first: drawing may add or remove components, invalidating the enumerator.
			_componentsScratch.Clear();
			foreach (var c in entity.Components)
			{
				_componentsScratch.Add(c);
			}

			foreach (var ec in _componentsScratch)
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

				ImGui.PushStyleColor(ImGuiCol.Header, EditorPalette.WithAlpha(EditorPalette.Selection, 0.16f));
				ImGui.PushStyleColor(ImGuiCol.HeaderHovered, EditorPalette.WithAlpha(EditorPalette.Selection, 0.28f));
				ImGui.PushStyleColor(ImGuiCol.HeaderActive, EditorPalette.WithAlpha(EditorPalette.Selection, 0.40f));
				bool open = ImGui.CollapsingHeader(displayName, ImGuiTreeNodeFlags.DefaultOpen);
				ImGui.PopStyleColor(3);

				if (ImGui.BeginPopupContextItem("##ComponentCtx"))
				{
					// Copied as a JSON fragment in the same format as components in .prefab.json.
					if (ImGui.MenuItem("Copy Component"))
					{
						CopyComponentToClipboard(entity, ec.Type);
					}

					ImGui.BeginDisabled(_componentClipboardKey is null);
					if (ImGui.MenuItem(_componentClipboardKey is null
							? "Paste Component"
							: $"Paste Component ({_componentClipboardKey})"))
					{
						// Pastes by the clipboard's KEY, not by the component whose menu was
						// clicked, so the target need not already have that component.
						anyChanged |= TryPasteComponent(entity);
					}
					ImGui.EndDisabled();

					ImGui.Separator();

					if (ImGui.MenuItem("Remove Component", string.Empty))
					{
						removeRequested = true;
					}
					ImGui.EndPopup();
				}

				if (open && !removeRequested && clrType != null)
				{
					var spineDrawList = ImGui.GetWindowDrawList();
					var spineTop = ImGui.GetCursorScreenPos();

					ImGui.Indent();
					// EntityComponent.Value is Obsolete in favour of Entity.GetComponent<T>(), but
					// a reflection-based editor has no static T, so the boxed accessor is required.
#pragma warning disable CS0618
					object boxed = ec.Value;
#pragma warning restore CS0618
					if (DrawObjectFields(boxed, clrType, entity.Store))
					{
						EntityUtils.AddEntityComponentValue(entity, ec.Type, boxed);
						anyChanged = true;
					}
					ImGui.Unindent();

					var spineBottom = ImGui.GetCursorScreenPos();
					float spineX = spineTop.X + 4f;
					spineDrawList.AddLine(new Vector2(spineX, spineTop.Y), new Vector2(spineX, spineBottom.Y),
						ImGui.GetColorU32(EditorPalette.WithAlpha(EditorPalette.Selection, 0.45f)), 2f);
					ImGui.Spacing();
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

		// Internal component clipboard: Friflo serialization key plus the value JSON. The system
		// clipboard is filled in parallel, but Paste always sources from this pair.
		private static string? _componentClipboardKey;
		private static string? _componentClipboardJson;

		/// <summary>Key of the component in the internal clipboard, null when empty.</summary>
		public static string? ComponentClipboardKey => _componentClipboardKey;

		private static void CopyComponentToClipboard(Entity entity, ComponentType type)
		{
			var key = type.ComponentKey;
			if (key is null)
			{
				return;
			}

			// Goes through the entity's full components JSON rather than serializing the boxed
			// value by hand: the fragment must be byte-identical to what would land in the file.
			using var doc = JsonDocument.Parse(PrefabAsset.GetComponentsJson(entity));
			if (!doc.RootElement.TryGetProperty(key, out var element))
			{
				return;
			}

			_componentClipboardKey = key;
			_componentClipboardJson = element.GetRawText();
			ImGui.SetClipboardText($"{{\"{key}\": {_componentClipboardJson}}}");
		}

		/// <summary>Applies the clipboard component to the entity, overwriting or adding it; other
		/// components are left untouched.</summary>
		public static bool TryPasteComponent(Entity entity)
		{
			if (_componentClipboardKey is null || _componentClipboardJson is null)
			{
				return false;
			}

			using var current = JsonDocument.Parse(PrefabAsset.GetComponentsJson(entity));
			using var fragment = JsonDocument.Parse(_componentClipboardJson);

			using var stream = new MemoryStream();
			using (var writer = new Utf8JsonWriter(stream))
			{
				writer.WriteStartObject();
				foreach (var property in current.RootElement.EnumerateObject())
				{
					if (!property.NameEquals(_componentClipboardKey))
					{
						property.WriteTo(writer);
					}
				}

				writer.WritePropertyName(_componentClipboardKey);
				fragment.RootElement.WriteTo(writer);
				writer.WriteEndObject();
			}

			var merged = Encoding.UTF8.GetString(stream.ToArray());
			if (!PrefabAsset.TryApplyComponentsJson(entity, merged, out var error))
			{
				DecaEngine.Core.Diagnostics.EngineLog.Add(DecaEngine.Core.Diagnostics.LogLevel.Error,
					$"Paste Component ({_componentClipboardKey}): {error}");
				return false;
			}

			return true;
		}

		private static bool DrawObjectFields(object boxed, Type type, EntityStore? store)
		{
			bool changed = false;
			var info = GetTypeInfo(type);
			var fields = info.Fields;
			for (int i = 0; i < fields.Length; i++)
			{
				var field = fields[i];
				object? value = field.GetValue(boxed);
				if (DrawField(field.Name, field.FieldType, ref value, store, info.AssetTypes[i]))
				{
					field.SetValue(boxed, value);
					changed = true;
				}
			}
			return changed;
		}

		private static bool DrawField(string label, Type type, ref object? value, EntityStore? store, AssetTypeAttribute? assetType = null)
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

			if (type == typeof(EntityRef))
			{
				return DrawEntityRef(label, ref value, store);
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
				return DrawArray(label, type, array, store, assetType);
			}

			if (type.IsValueType && !type.IsPrimitive && value != null)
			{
				bool changed = false;
				if (ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
				{
					changed = DrawObjectFields(value, type, store);
					ImGui.TreePop();
				}
				return changed;
			}

			ImGui.TextDisabled($"{label}: {value} ({type.Name})");
			return false;
		}

		// Draws an AssetRef as a slot that doubles as an ImGui drag&drop target.
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

			// Invisible button reserves layout space and hit testing; visuals are hand-drawn below.
			ImGui.InvisibleButton("##AssetRefSlot", slotSize);
			bool hovered = ImGui.IsItemHovered();

			var drawList = ImGui.GetWindowDrawList();
			var rounding = ImGui.GetStyle().FrameRounding;
			drawList.AddRectFilled(slotMin, slotMax, ImGui.GetColorU32(hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg), rounding);

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

		// Draws an EntityRef as a button that doubles as an ImGui drag&drop target.
		private static bool DrawEntityRef(string label, ref object? value, EntityStore? store)
		{
			var current = value is EntityRef entityRef ? entityRef : default;
			bool changed = false;
			bool hasValue = !current.IsEmpty;

			var frameHeight = ImGui.GetFrameHeight();
			var clearWidth = hasValue ? frameHeight + ImGui.GetStyle().ItemSpacing.X : 0f;
			var slotWidth = MathF.Max(80f, ImGui.GetContentRegionAvail().X - clearWidth);
			var slotSize = new Vector2(slotWidth, frameHeight);

			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(label);
			ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);

			var slotMin = ImGui.GetCursorScreenPos();
			var slotMax = slotMin + slotSize;

			ImGui.InvisibleButton("##EntityRefSlot", slotSize);
			bool hovered = ImGui.IsItemHovered();

			var drawList = ImGui.GetWindowDrawList();
			var rounding = ImGui.GetStyle().FrameRounding;
			drawList.AddRectFilled(slotMin, slotMax, ImGui.GetColorU32(hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg), rounding);

			string displayText;
			if (hasValue)
			{
				var target = store != null ? current.Resolve(store) : default;
				displayText = !target.IsNull
					? (target.HasName && !string.IsNullOrEmpty(target.Name.value) ? target.Name.value : $"Entity {current.Pid}")
					: $"<missing: {current.Pid}>";
			}
			else
			{
				displayText = "Drop entity here...";
			}
			var textColor = ImGui.GetColorU32(hasValue ? ImGuiCol.Text : ImGuiCol.TextDisabled);
			var textPos = new Vector2(slotMin.X + 4f, slotMin.Y + (frameHeight - ImGui.GetTextLineHeight()) * 0.5f);
			drawList.PushClipRect(slotMin, slotMax, true);
			drawList.AddText(textPos, textColor, displayText);
			drawList.PopClipRect();

			var borderColor = ImGui.GetColorU32(EditorPalette.WithAlpha(EditorPalette.Selection, hovered ? 0.9f : 0.55f));
			drawList.AddRect(slotMin, slotMax, borderColor, rounding, ImDrawFlags.None, hovered ? 2f : 1.5f);

			if (hovered && hasValue)
			{
				ImGui.SetTooltip(displayText);
			}

			if (ImGui.BeginDragDropTarget())
			{
				var payload = ImGui.AcceptDragDropPayload(EntityRef.DragDropPayloadType);
				if (!payload.IsNull && payload.DataSize == sizeof(long))
				{
					unsafe
					{
						long droppedPid = *(long*)payload.Data;
						current = new EntityRef(droppedPid);
						changed = true;
					}
				}
				ImGui.EndDragDropTarget();
			}

			if (hasValue)
			{
				ImGui.SameLine(0f, ImGui.GetStyle().ItemSpacing.X);
				if (ImGui.Button("x##EntityRefClear"))
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
			var cache = GetEnumInfo(type);
			var current = value ?? cache.ZeroValue;

			if (cache.IsFlags)
			{
				bool changed = false;
				long currentBits = Convert.ToInt64(current);

				if (ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
				{
					for (int i = 0; i < cache.Names.Length; i++)
					{
						long bits = cache.Bits[i];
						if (bits == 0)
						{
							continue; // "None"/zero values have no checkbox
						}

						bool set = (currentBits & bits) == bits;
						if (ImGui.Checkbox(cache.Names[i], ref set))
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

			int index = Array.IndexOf(cache.Names, current.ToString());
			if (index < 0)
			{
				index = 0;
			}

			if (ImGui.Combo(label, ref index, cache.ComboItems))
			{
				value = cache.Values[index];
				return true;
			}
			return false;
		}

		private static bool DrawArray(string label, Type type, Array array, EntityStore? store, AssetTypeAttribute? assetType = null)
		{
			var elemType = type.GetElementType()!;
			bool changed = false;

			// Header string is rebuilt only when the array length for this label changes.
			if (!_arrayHeaders.TryGetValue(label, out var header) || header.Length != array.Length)
			{
				header = (array.Length, $"{label} [{array.Length}]");
				_arrayHeaders[label] = header;
			}

			if (ImGui.TreeNodeEx(header.Header, ImGuiTreeNodeFlags.DefaultOpen))
			{
				for (int i = 0; i < array.Length; i++)
				{
					object? elem = array.GetValue(i);
					ImGui.PushID(i);
					if (DrawField(GetIndexLabel(i), elemType, ref elem, store, assetType))
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

