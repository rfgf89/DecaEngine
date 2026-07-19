namespace DecaEngine.Editor;

public static class EditorMain
{
	private static EditorManager EditorManager;

	private static void Main(string[] args)
	{
		EditorManager = new EditorManager();
		EditorManager.Initialize();
	}
}