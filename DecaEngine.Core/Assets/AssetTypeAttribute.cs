namespace DecaEngine.Core.Assets
{
	/// <summary>Restricts which asset extensions an <see cref="AssetRef"/> field accepts on drop;
	/// matched case-insensitively, leading dot optional.</summary>
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
