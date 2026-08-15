using Friflo.Engine.ECS;

namespace DecaEngine.Core.Assets
{
	/// <summary>
	/// Serializable reference to a project asset file, stored as a path relative to the project's
	/// "Assets" directory (always forward-slash separated, so it stays portable across machines and
	/// platforms and survives being committed to source control alongside prefab JSON).
	/// </summary>
	/// <remarks>
	/// Intended to be used as a component/script field instead of a raw <see cref="string"/> path:
	/// the editor's ComponentFieldEditor recognizes this type and renders a dedicated widget
	/// (current file name + a drag&amp;drop target) instead of a plain text box, so users can drag
	/// an asset straight from the editor's Asset Browser window onto the field. It is a plain struct
	/// with a single public string field, so Friflo's Fliox JSON serializer round-trips it like any
	/// other component value type - no custom converter needed. At runtime, engine/game code
	/// resolves <see cref="Path"/> against the project's assets root via its own asset-loading APIs
	/// (this type intentionally has no I/O of its own).
	/// </remarks>
	public struct AssetRef : IEquatable<AssetRef>, IComponent
	{
		/// <summary>
		/// ImGui drag&amp;drop payload type identifier shared between the editor's Asset Browser
		/// window (drag source) and ComponentFieldEditor (drop target). ImGui payload type strings
		/// must be at most 32 bytes - keep this short.
		/// </summary>
		public const string DragDropPayloadType = "DECA_ASSET_PATH";

		/// <summary>Path to the asset, relative to the project's Assets directory (e.g. "Models/cube.gltf"). Empty when unassigned.</summary>
		public string Path;

		public AssetRef(string? path)
		{
			Path = path ?? string.Empty;
		}

		public readonly bool IsEmpty => string.IsNullOrEmpty(Path);

		public static implicit operator AssetRef(string? path) => new(path);
		public static implicit operator string(AssetRef assetRef) => assetRef.Path;

		public readonly bool Equals(AssetRef other) => string.Equals(Path, other.Path, StringComparison.Ordinal);
		public readonly override bool Equals(object? obj) => obj is AssetRef other && Equals(other);
		public readonly override int GetHashCode() => Path is null ? 0 : Path.GetHashCode(StringComparison.Ordinal);
		public readonly override string ToString() => Path ?? string.Empty;
	}
}



