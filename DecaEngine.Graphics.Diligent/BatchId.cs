namespace DecaEngine.Graphics.Diligent;

public struct BatchId(int batchId) : IEquatable<BatchId>
{
	public int batchId = batchId;

	public bool Equals(BatchId other)
	{
		return batchId == other.batchId;
	}

	public override bool Equals(object? obj)
	{
		return obj is BatchId other && Equals(other);
	}

	public override int GetHashCode()
	{
		return batchId;
	}
}