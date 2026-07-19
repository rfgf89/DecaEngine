using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DiligentEngineNET.Samples.Utils
{
	/// <summary>
	/// Simple Profiler class
	/// Use if you need to measure some calls
	/// <example>
	///	static Profiler RenderProfiler = new("Render");
	/// void Render() {
	///		RenderProfiler.Begin();
	///		...
	///		RenderProfiler.End();
	/// }
	/// </example>
	/// </summary>
	/// <param name="profileName"></param>
	public class Profiler(string profileName)
	{
		private Stopwatch stopwatch = new ();
		private long _ticks;
		private long _avgTime;
		private long _elapsedTime;
		private int _indentation = 0;
		private List<Profiler> _nestedProfilers = new ();
		private List<long> _times = new();

		public string Name => profileName;
		public long SpentTimeMs => _elapsedTime;

		public long AvgTimeMs => _avgTime / _ticks;
		
		public long Ticks => _ticks;
		
		public long MedianTimeMs {
			get
			{
				if(_times.Count % 2 == 0)
					return _times[_times.Count / 2];
				var nextIdx = (int)Math.Round(_times.Count / 2.0f);
				return (_times[nextIdx - 1] + _times[nextIdx]) / 2;
			}
		}
		
		public Profiler Use(string profileName)
		{
			var res = new Profiler(profileName);
			res._indentation = _indentation + 1;
			_nestedProfilers.Add(res);
			return res;
		}

		public void Begin()
		{
			++_ticks;
			stopwatch.Start ();
		}

		public void End()
		{
			stopwatch.Stop ();
			
			_elapsedTime = stopwatch.ElapsedMilliseconds;
			_avgTime += _elapsedTime;
			_times.Add (_elapsedTime);
			
			stopwatch.Reset ();
		}

		public override string ToString()
		{
			var indentation = GetIndentation();
			var sb = new StringBuilder ();
			sb.Append(indentation);
			sb.Append("# ");
			sb.Append(Name);
			sb.AppendLine(":");

			sb.Append(indentation);
			sb.AppendLine($"   Elapsed: {SpentTimeMs}ms");

			sb.Append(indentation);
			sb.AppendLine($"   Avg: {AvgTimeMs}ms");
			
			sb.Append(indentation);
			sb.AppendLine($"   Median: {MedianTimeMs}ms");

			sb.Append(indentation);
			sb.AppendLine($"   Ticks: {Ticks}");

			_nestedProfilers.ForEach(x => sb.Append(x));
			return sb.ToString();

			string GetIndentation()
			{
				var arr = new char[_indentation];
				Array.Fill(arr, '\t');
				return new string(arr);
			}
		}
	}
}
