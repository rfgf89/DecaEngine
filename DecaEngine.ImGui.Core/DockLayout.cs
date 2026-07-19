using System.Numerics;
using Hexa.NET.ImGui;

namespace Engine.ImGui.Core;

public class DockLayout
{
	private readonly List<DockLayoutElement> _dockLayoutElements = new();

	private Vector2 _lastWorkSize;
	private uint _centerId;

	public DockLayout(string title)
	{
		_centerId = 1;
	}

	public void AddDockLayoutElement(DockLayoutElement dockLayoutElement)
	{
		_dockLayoutElements.Add(dockLayoutElement);
	}

	public void FirstFrame(uint dockId)
	{
		var workSize = Hexa.NET.ImGui.ImGui.GetMainViewport().WorkSize;
		BuildDockLayout(workSize);
	}

	public void Render(uint dockId)
	{
		UpdateDockLayout(dockId);
		Hexa.NET.ImGui.ImGui.DockSpace(dockId);
	}

	private unsafe void BuildDockLayout(Vector2 workSize)
	{
		if (ImGuiP.DockBuilderGetNode(_centerId).IsNull)
		{
			ImGuiP.DockBuilderAddNode(_centerId, ImGuiDockNodeFlags.NoDockingOverCentralNode);
		}

		ImGuiP.DockBuilderSetNodeSize(_centerId, workSize);

		for (int i = 0; i < _dockLayoutElements.Count; i++)
		{
			var element = _dockLayoutElements[i];
			uint newDockId = 0;
			uint currentCenterId = _centerId;
			ImGuiP.DockBuilderSplitNode(currentCenterId, element.imGuiDir, element.ratio, &newDockId, &currentCenterId);
			ImGuiP.DockBuilderDockWindow(element.name, newDockId);
			element.dockId = newDockId;
			_centerId = currentCenterId;
		}

		ImGuiP.DockBuilderFinish(_centerId);
	}

	private void Builder(Vector2 workSize)
	{
		if (workSize.X <= 0 || workSize.Y <= 0)
		{
			return;
		}

		foreach (var element in _dockLayoutElements)
		{
			var node = ImGuiP.DockBuilderGetNode(element.dockId);
			if (node.IsNull)
			{
				continue;
			}

			if (element.size.X <= 0 || element.size.Y <= 0)
			{
				continue;
			}

			ImGuiP.DockBuilderSetNodePos(element.dockId, element.position * workSize);
			ImGuiP.DockBuilderSetNodeSize(element.dockId, element.size * workSize);
		}
	}

	private void SyncRatiosFromNodes(Vector2 workSize)
	{
		if (workSize.X <= 0 || workSize.Y <= 0)
		{
			return;
		}

		foreach (var element in _dockLayoutElements)
		{
			var node = ImGuiP.DockBuilderGetNode(element.dockId);
			if (node.IsNull)
			{
				continue;
			}

			element.position = node.Pos / workSize;
			element.size = node.Size / workSize;
		}
	}

	private void UpdateDockLayout(uint dockId)
	{
		var workSize = Hexa.NET.ImGui.ImGui.GetMainViewport().WorkSize;
		ImGuiP.DockBuilderSetNodePos(dockId, Hexa.NET.ImGui.ImGui.GetMainViewport().Pos);
		ImGuiP.DockBuilderSetNodeSize(dockId, workSize);

		if (Hexa.NET.ImGui.ImGui.IsMouseDown(ImGuiMouseButton.Left))
		{
			return;
		}

		if (Hexa.NET.ImGui.ImGui.IsMouseReleased(ImGuiMouseButton.Left))
		{
			SyncRatiosFromNodes(workSize);
		}

		if (workSize != _lastWorkSize)
		{
			Builder(workSize);
			_lastWorkSize = workSize;
		}
	}
}