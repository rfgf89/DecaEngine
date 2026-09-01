using System.Text;

namespace DecaEngine.Core.Diagnostics;

public enum LogLevel
{
	Info,
	Warning,
	Error
}

/// <summary>Кто написал строку: сам движок/редактор, сборка пользовательского проекта или
/// нативный слой (Diligent, шимы). Разделение нужно окну консоли для фильтра - искать ошибку
/// своего скрипта среди тысяч строк драйвера иначе невозможно.</summary>
public enum LogSource
{
	Editor,
	Project,
	Native
}

public readonly struct LogEntry
{
	public readonly DateTime Time;
	public readonly LogLevel Level;
	public readonly LogSource Source;
	public readonly string Message;

	public LogEntry(DateTime time, LogLevel level, LogSource source, string message)
	{
		Time = time;
		Level = level;
		Source = source;
		Message = message;
	}
}

/// <summary>
/// Сток логов движка: кольцевой буфер записей плюс перехват stdout/stderr.
///
/// Лежит в Core, а не в редакторе, хотя читает его именно окно консоли. Причина в
/// <see cref="Install"/>: он подменяет Console.Out и Console.Error, то есть перехватывает вывод
/// ВСЕГО процесса - загрузчика моделей, бейкера probe GI, физики, пользовательской сборки. Пиши
/// он из редактора, каждый модуль движка, которому есть что сказать, был бы обязан знать про
/// редактор - а это ровно та зависимость, которой быть не должно.
///
/// Окно консоли (ConsoleWindow) остаётся в редакторе и работает поверх <see cref="Snapshot"/> и
/// <see cref="EntryAdded"/>.
/// </summary>
public static class EngineLog
{
	private const int MaxEntries = 5000;

	private static readonly object _sync = new();
	private static readonly List<LogEntry> _entries = new();
	private static bool _installed;
	private static int? _projectThreadId;

	public static event Action? EntryAdded;

	public static void Install()
	{
		if (_installed)
		{
			return;
		}

		_installed = true;

		Console.SetOut(new CapturingTextWriter(Console.Out, LogLevel.Info));
		Console.SetError(new CapturingTextWriter(Console.Error, LogLevel.Error));

		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
			Add(LogLevel.Error, e.ExceptionObject?.ToString() ?? "Unhandled exception");
	}

	/// <summary>Поток, на котором крутится сборка пользовательского проекта: всё, что напишет он,
	/// помечается как <see cref="LogSource.Project"/>.</summary>
	public static void SetProjectThreadId(int? threadId)
	{
		_projectThreadId = threadId;
	}

	public static void Add(LogLevel level, string message)
	{
		if (string.IsNullOrEmpty(message))
		{
			return;
		}

		if (level == LogLevel.Info && message.Contains("warn", StringComparison.OrdinalIgnoreCase))
		{
			level = LogLevel.Warning;
		}

		var source = _projectThreadId.HasValue && _projectThreadId.Value == Environment.CurrentManagedThreadId
			? LogSource.Project
			: LogSource.Editor;

		AddInternal(level, source, message);
	}

	public static void AddNative(LogLevel level, string message)
	{
		if (string.IsNullOrEmpty(message))
		{
			return;
		}

		AddInternal(level, LogSource.Native, message);
	}

	private static void AddInternal(LogLevel level, LogSource source, string message)
	{
		lock (_sync)
		{
			_entries.Add(new LogEntry(DateTime.Now, level, source, message));
			if (_entries.Count > MaxEntries)
			{
				_entries.RemoveRange(0, _entries.Count - MaxEntries);
			}
		}

		EntryAdded?.Invoke();
	}

	public static List<LogEntry> Snapshot()
	{
		lock (_sync)
		{
			return new List<LogEntry>(_entries);
		}
	}

	public static void Clear()
	{
		lock (_sync)
		{
			_entries.Clear();
		}

		EntryAdded?.Invoke();
	}

	private sealed class CapturingTextWriter : TextWriter
	{
		private readonly TextWriter _inner;
		private readonly LogLevel _level;
		private readonly StringBuilder _lineBuffer = new();

		public CapturingTextWriter(TextWriter inner, LogLevel level)
		{
			_inner = inner;
			_level = level;
		}

		public override Encoding Encoding => _inner.Encoding;

		public override void Write(char value)
		{
			_inner.Write(value);

			if (value == '\n')
			{
				FlushLine();
			}
			else if (value != '\r')
			{
				lock (_lineBuffer)
				{
					_lineBuffer.Append(value);
				}
			}
		}

		public override void Write(string? value)
		{
			if (value is null)
			{
				return;
			}

			_inner.Write(value);

			foreach (var ch in value)
			{
				if (ch == '\n')
				{
					FlushLine();
				}
				else if (ch != '\r')
				{
					lock (_lineBuffer)
					{
						_lineBuffer.Append(ch);
					}
				}
			}
		}

		public override void WriteLine(string? value)
		{
			_inner.WriteLine(value);

			lock (_lineBuffer)
			{
				_lineBuffer.Append(value);
			}

			FlushLine();
		}

		private void FlushLine()
		{
			string message;
			lock (_lineBuffer)
			{
				message = _lineBuffer.ToString();
				_lineBuffer.Clear();
			}

			Add(_level, message);
		}
	}
}
