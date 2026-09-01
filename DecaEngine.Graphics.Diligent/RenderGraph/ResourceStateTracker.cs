using System.Collections.Generic;
using Diligent;

namespace DecaEngine.Graphics.Diligent.RenderGraph
{
    public class ResourceStateTracker
    {
        // A single tracker instance may be shared between the immediate context and
        // several deferred contexts that record in parallel (one per render-graph
        // dependency-level pass). Passes within the same level never touch the same
        // resource (guaranteed by the graph's dependency analysis), but the backing
        // Dictionary/List are not safe for concurrent access from multiple threads
        // regardless of key overlap, so all mutation goes through this lock.
        private readonly object _lock = new();

        private readonly Dictionary<IDeviceObject, global::Diligent.ResourceState> _resourceStates = new();
        private readonly List<StateTransitionDesc> _pendingTransitions = new();
        private readonly Dictionary<int, StateTransitionDesc[]> _arrayCache = new();

        public void AddTransition(IDeviceObject resource, global::Diligent.ResourceState newState)
        {
            if (resource == null) return;

            lock (_lock)
            {
                if (_resourceStates.TryGetValue(resource, out var currentState))
                {
                    // Allow UAV-to-UAV transition as it acts as a UAV barrier in Diligent
                    if (currentState == newState && newState != global::Diligent.ResourceState.UnorderedAccess) return;

                    _pendingTransitions.Add(new StateTransitionDesc
                    {
                        Resource = resource,
                        OldState = currentState,
                        NewState = newState,
                        Flags = StateTransitionFlags.UpdateState
                    });
                }
                else
                {
                    _pendingTransitions.Add(new StateTransitionDesc
                    {
                        Resource = resource,
                        OldState = global::Diligent.ResourceState.Unknown,
                        NewState = newState,
                        Flags = StateTransitionFlags.UpdateState
                    });
                }
                _resourceStates[resource] = newState;
            }
        }

        public void SetState(IDeviceObject resource, global::Diligent.ResourceState state)
        {
            if (resource == null) return;
            lock (_lock)
            {
                _resourceStates[resource] = state;
            }
        }

        public void Flush(IDeviceContext context)
        {
            lock (_lock)
            {
                int count = _pendingTransitions.Count;
                if (count == 0) return;

                if (!_arrayCache.TryGetValue(count, out var transitions))
                {
                    transitions = new StateTransitionDesc[count];
                    _arrayCache[count] = transitions;
                }

                for (int i = 0; i < count; i++)
                {
                    transitions[i] = _pendingTransitions[i];
                }

                context.TransitionResourceStates(transitions);
                _pendingTransitions.Clear();
            }
        }

        public void ResetTransitions()
        {
            lock (_lock)
            {
                _pendingTransitions.Clear();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _resourceStates.Clear();
                _pendingTransitions.Clear();
            }
        }
    }
}