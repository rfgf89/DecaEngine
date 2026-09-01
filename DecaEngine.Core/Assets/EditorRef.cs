using System.Text.Json;
using System.Text.Json.Serialization;

namespace DecaEngine.Core.Assets
{
	/// <summary>
	/// Serializable reference to a file bundled in the editor's own "EditorAssets" folder (shaders,
	/// placeholder models, icons - shipped alongside the editor executable), as opposed to
	/// <see cref="AssetRef"/> which points into the current project's "Assets" directory. Stored as a
	/// path relative to "EditorAssets/" (always forward-slash separated).
	/// </summary>
	[JsonConverter(typeof(EditorRefJsonConverter))]
	public struct EditorRef : IEquatable<EditorRef>
	{
		/// <summary>Path to the asset, relative to the "EditorAssets" folder (e.g. "shader/UnlitInstancedVS.hlsl"). Empty when unassigned.</summary>
		public string Path;

		public EditorRef(string? path)
		{
			Path = path ?? string.Empty;
		}

		public readonly bool IsEmpty => string.IsNullOrEmpty(Path);

		public static implicit operator EditorRef(string? path) => new(path);
		public static implicit operator string(EditorRef editorRef) => editorRef.Path;

		public readonly bool Equals(EditorRef other) => string.Equals(Path, other.Path, StringComparison.Ordinal);
		public override readonly bool Equals(object? obj) => obj is EditorRef other && Equals(other);
		public override readonly int GetHashCode() => Path is null ? 0 : Path.GetHashCode(StringComparison.Ordinal);
		public override readonly string ToString() => Path ?? string.Empty;

		/// <summary>Absolute path, resolved against the "EditorAssets" folder shipped alongside the editor executable.</summary>
		public readonly string ResolvedFullPath => System.IO.Path.Combine(Environment.CurrentDirectory, "EditorAssets", Path);

		/// <summary>
		/// Splits the path into the (factoryPath, fileName) pair expected by
		/// <see cref="DecaEngine.Graphics.IGraphicsApi.CreateShader(string, string, string, DecaEngine.Graphics.ShaderObjectType)"/>,
		/// e.g. "shader/UnlitInstancedVS.hlsl" -&gt; ("EditorAssets/shader", "UnlitInstancedVS.hlsl").
		/// </summary>
		public readonly (string factoryPath, string fileName) ToShaderFactoryParts()
		{
			var dir = System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? string.Empty;
			var factoryPath = string.IsNullOrEmpty(dir) ? "EditorAssets" : $"EditorAssets/{dir}";
			return (factoryPath, System.IO.Path.GetFileName(Path));
		}
	}

	internal sealed class EditorRefJsonConverter : JsonConverter<EditorRef>
	{
		public override EditorRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
			=> new(reader.GetString());

		public override void Write(Utf8JsonWriter writer, EditorRef value, JsonSerializerOptions options)
			=> writer.WriteStringValue(value.Path);
	}
}
