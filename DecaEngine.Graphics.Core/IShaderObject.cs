namespace DecaEngine.Core;

public enum ShaderObjectType
{
	Unknown = 0,
	Vertex = 1,
	Pixel = 2,
	Geometry = 4,
	Hull = 8,
	Domain = 16, // 0x00000010
	Compute = 32, // 0x00000020
	Amplification = 64, // 0x00000040
	Mesh = 128, // 0x00000080
	RayGen = 256, // 0x00000100
	RayMiss = 512, // 0x00000200
	RayClosestHit = 1024, // 0x00000400
	RayAnyHit = 2048, // 0x00000800
	RayIntersection = 4096, // 0x00001000
	Callable = 8192, // 0x00002000
	Tile = 16384, // 0x00004000
	Last = Tile, // 0x00004000
	VsPs = Pixel | Vertex, // 0x00000003
	AllGraphics = VsPs | Domain | Hull | Geometry, // 0x0000001F
	AllMesh = Mesh | Amplification | Pixel, // 0x000000C2
	AllRayTracing = Callable | RayIntersection | RayAnyHit | RayClosestHit | RayMiss | RayGen, // 0x00003F00
	All = AllRayTracing | AllMesh | Last | Compute | Domain | Hull | Geometry | Vertex, // 0x00007FFF
}


public interface IShaderObject : IReleaseObject
{
	public ShaderObjectType Type { get; }
	public string Name { get; }
	public string FilePath { get; }
	public void Compile();
}