namespace DecaEngine.Core;

public interface IEngineRun
{
	public void Run();

	public void Play();

	public void Pause();

	public void Quit();

	public IEngineRun GetRun();
}