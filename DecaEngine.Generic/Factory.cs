namespace DecaEngine.Generic;

public interface IFactory : IFactoryObject
{
	public T Create<T>() where T : IFactoryObject, new();
	public void Destroy<T>(T obj) where T : IFactoryObject, new();
}

public interface IFactoryObject
{

}