using Friflo.Engine.ECS;

namespace DecaEngine.Core.Assets
{
	/// <summary>Serializable asset reference: forward-slash path relative to the project's Assets directory.</summary>
	public struct AssetRef : IEquatable<AssetRef>, IComponent
	{
		/// <summary>ImGui drag-and-drop payload id; ImGui caps payload type strings at 32 bytes.</summary>
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



