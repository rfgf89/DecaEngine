using System;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics.Diligent;
using Engine.ImGui.Core;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor
{
	public class DebugWindow : ImGuiDockingWindow
	{
		private bool _isBatchDebug;
		private Vector3 _tempEuler = Vector3.Zero;
		private int _lastLightEntityId = -1;

		private EntityStore _ecsWorld;
		private DiligentBatchRenderer _batchRenderer;

		public float FramePerSecond { get; set; }

		public DebugWindow(string name, EntityStore ecsWorld, DiligentBatchRenderer batchRenderer, ImGuiRender imGuiRender) : base(name, imGuiRender)
		{
			_ecsWorld = ecsWorld;
			_batchRenderer = batchRenderer;
		}

		protected override unsafe void OnRender(uint dockId)
		{
			ImGui.Text("Fps : " + FramePerSecond);
			ImGui.Separator();

			ImGui.Checkbox("Debug Batch Renderer", ref _isBatchDebug);

			var cameras = _ecsWorld.Query<CameraComponent>();
			cameras.ForEachEntity((ref CameraComponent camera, Entity entity) =>
			{
				bool frustumCulling = (camera.data.cullFlags & CullFlags.Frustum) != 0;
				if (ImGui.Checkbox("Frustum Culling", ref frustumCulling))
				{
					camera.data.cullFlags = frustumCulling ? (camera.data.cullFlags | CullFlags.Frustum) : (camera.data.cullFlags & ~CullFlags.Frustum);
				}

				bool lodSelection = (camera.data.cullFlags & CullFlags.Lod) != 0;
				if (ImGui.Checkbox("LOD Selection", ref lodSelection))
				{
					camera.data.cullFlags = lodSelection ? (camera.data.cullFlags | CullFlags.Lod) : (camera.data.cullFlags & ~CullFlags.Lod);
				}
			});

			if (_isBatchDebug)
			{
				var info = _batchRenderer.ReadInfo();
				ImGui.Text($"Total Batches: {_batchRenderer.GetBatches().Count}, Draw Calls: {info.pipelineStateCount}");

				// Debug Light Data
				if (ImGui.TreeNode("Light Debug"))
				{
					var lights = _ecsWorld.Query<LightComponent, SunComponent>();
					lights.ForEachEntity((ref LightComponent light, ref SunComponent sun, Entity lightEntity) =>
					{
						if (_lastLightEntityId != lightEntity.Id)
						{
							_lastLightEntityId = lightEntity.Id;
							_tempEuler = MathUtils.ToEulerAngles(lightEntity.Rotation.value) * (180f / (float)Math.PI);
						}

						ImGui.Text($"Entity: {lightEntity.Id}");

						var pos = lightEntity.Position.value;
						if (ImGui.DragFloat3("Position", ref pos))
							lightEntity.Position = new Position(pos.X, pos.Y, pos.Z);

						if (ImGui.DragFloat3("Rotation (Euler)", ref _tempEuler))
						{
							var rad = _tempEuler * ((float)Math.PI / 180f);
							lightEntity.Rotation = new Rotation { value = Quaternion.CreateFromYawPitchRoll(rad.Y, rad.X, rad.Z) };
						}

						ImGui.DragFloat("Intensity", ref light.Intensity, 0.05f, 0f, 100f);
						ImGui.ColorEdit3("Color", ref light.Color);
						ImGui.DragFloat("Shadow Strength", ref light.ShadowStrength, 0.001f, 0f, 10f);

						var lightDirection = Vector3.Transform(Vector3.UnitZ, lightEntity.Rotation.value);
						ImGui.Text($"Direction: {lightDirection}");
					});
					ImGui.TreePop();
				}

				var indirectArgs = _batchRenderer.ReadIndirectArgsForDebugging();
				var drawRanges = _batchRenderer.GetDebugDrawRanges();

				long totalVisibleIndices = 0;

				if (ImGui.TreeNode("Material Draw Ranges"))
				{
					foreach (var kvp in drawRanges)
					{
						var materialId = kvp.Key;
						var range = kvp.Value;
						ImGui.Text($"Material {materialId}: {range.DrawCount} batches, starting at index {range.FirstDrawIndex}");
					}
					ImGui.TreePop();
				}

				if (ImGui.TreeNode("Batch Details"))
				{
					var batches = _batchRenderer.GetBatches();
					int count = Math.Min(indirectArgs.Length, batches.Count);

					if (count > 0)
					{
						if (ImGui.BeginTable("BatchTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
						{
							ImGui.TableSetupColumn("Batch");
							ImGui.TableSetupColumn("Mesh ID");
							ImGui.TableSetupColumn("Material ID");
							ImGui.TableSetupColumn("Visible Instances");
							ImGui.TableHeadersRow();

							var clipper = new ImGuiListClipper();
							clipper.Begin(count);

							while (clipper.Step())
							{
								for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
								{
									var batchInfo = batches[i];
									var args = indirectArgs[i];

									ImGui.TableNextRow();
									ImGui.TableSetColumnIndex(0);
									ImGui.Text($"{i}");
									ImGui.TableSetColumnIndex(1);
									ImGui.Text($"{batchInfo.Value.mesh.meshId}");
									ImGui.TableSetColumnIndex(2);
									ImGui.Text($"{batchInfo.Value.material.materialId}");
									ImGui.TableSetColumnIndex(3);
									ImGui.Text($"{args.NumInstances}");
								}
							}
							ImGui.EndTable();
						}
					}
					ImGui.TreePop();
				}

				if (indirectArgs.Length > 0)
				{
					for (int i = 0; i < indirectArgs.Length; i++)
					{
						var args = indirectArgs[i];
						totalVisibleIndices += (long)args.NumInstances * args.NumIndices;
					}
				}

				ImGui.Text($"Visible Triangles: {totalVisibleIndices / 3f / 1000000f:F2}m");
			}
		}
	}
}