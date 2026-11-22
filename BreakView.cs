namespace Afterbreak;

internal enum BreakLayout { TimerOnly, Compact, SavedBoard, SampleBoard }

internal readonly record struct BreakView(BreakLayout Layout, BoardTask? Task = null, IReadOnlyList<BoardTask>? Tasks = null);
