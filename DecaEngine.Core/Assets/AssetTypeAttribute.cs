namespace DecaEngine.Core.Assets
{
	/// <summary>
	/// Restricts which asset file extensions an <see cref="AssetRef"/> component field accepts when
	/// dropped from AssetBrowserWindow. Without this attribute, ComponentFieldEditor accepts any
	/// dragged asset path. Extensions are matched case-insensitively and may be given with or
	/// without the leading dot (e.g. "png" or ".png").
	/// </summary>
	[AttributeUsage(AttributeTargets.Field)]
	public sealed class AssetTypeAttribute : Attribute
	{
		public string[] Extensions { get; }

		public AssetTypeAttribute(params string[] extensions)
		{
			Extensions = Array.ConvertAll(extensions, static e => e.StartsWith('.') ? e : $".{e}");
		}

		public bool Accepts(string path)
		{
			if (Extensions.Length == 0)
			{
				return true;
			}

			var ext = System.IO.Path.GetExtension(path);
			foreach (var allowed in Extensions)
			{
				if (string.Equals(ext, allowed, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}
	}
}
