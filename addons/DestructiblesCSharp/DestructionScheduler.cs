using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Destructibles;

// One queue per scene tree, so several explosions share the same frame budget.
[Tool]
public partial class DestructionScheduler : Node
{
	private sealed record Work(Action Run, int Limit, double Milliseconds,
		CancellationToken Cancellation, TaskCompletionSource Completion);
	private readonly Queue<Work> _work = new();
	private static readonly ConditionalWeakTable<SceneTree, DestructionScheduler> Instances = new();
	private SceneTree _tree;

	public static Task Run(SceneTree tree, Action action, CancellationToken cancellation,
		int shardsPerFrame = 4, double milliseconds = 2)
	{
		cancellation.ThrowIfCancellationRequested();
		if (!Instances.TryGetValue(tree, out var scheduler) || !IsInstanceValid(scheduler))
		{
			Instances.Remove(tree);
			scheduler = new DestructionScheduler { Name = "DestructionScheduler", _tree = tree };
			Instances.Add(tree, scheduler);
			var pending = scheduler;
			// Calls from _Ready or a collision signal must not change busy parents.
			Callable.From(() => tree.Root.AddChild(pending)).CallDeferred();
		}
		var completion = new TaskCompletionSource();
		scheduler._work.Enqueue(new Work(action, Math.Max(1, shardsPerFrame),
			Math.Max(0.1, milliseconds), cancellation, completion));
		scheduler.SetProcess(true);
		return completion.Task;
	}

	public override void _Process(double delta)
	{
		var limit = int.MaxValue;
		var milliseconds = double.MaxValue;
		foreach (var work in _work)
		{
			limit = Math.Min(limit, work.Limit);
			milliseconds = Math.Min(milliseconds, work.Milliseconds);
		}
		var started = Stopwatch.GetTimestamp();
		for (var count = 0; count < limit && _work.Count > 0; count++)
		{
			var work = _work.Dequeue();
			if (work.Cancellation.IsCancellationRequested)
			{
				work.Completion.TrySetCanceled(work.Cancellation);
				count--; // Cancelled jobs consume no generation budget.
			}
			else
			{
				try { work.Run(); work.Completion.TrySetResult(); }
				catch (Exception error) { work.Completion.TrySetException(error); }
			}
			// A single convex hull/upload cannot be interrupted; check after every shard.
			if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= milliseconds)
				break;
		}
		SetProcess(_work.Count > 0);
	}

	public override void _ExitTree()
	{
		Instances.Remove(_tree);
		while (_work.TryDequeue(out var work))
			work.Completion.TrySetCanceled();
	}
}
