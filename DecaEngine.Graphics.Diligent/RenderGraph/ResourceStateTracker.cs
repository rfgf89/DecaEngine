using System.Collections.Generic;
using Diligent;

namespace DecaEngine.Graphics.Diligent.RenderGraph
{
    public class ResourceStateTracker
    {
        private readonly Dictionary<IDeviceObject, ResourceState> _resourceStates = new();
        private readonly List<StateTransitionDesc> _pendingTransitions = new();
        private readonly Dictionary<int, StateTransitionDesc[]> _arrayCache = new();

        public void AddTransition(IDeviceObject resource, ResourceState newState)
        {
            if (resource == null) return;

            if (_resourceStates.TryGetValue(resource, out var currentState))
            {
                // Allow UAV-to-UAV transition as it acts as a UAV barrier in Diligent
                if (currentState == newState && newState != ResourceState.UnorderedAccess) return;

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
                    OldState = ResourceState.Unknown,
                    NewState = newState,
                    Flags = StateTransitionFlags.UpdateState
                });
            }
            _resourceStates[resource] = newState;
        }

        public void SetState(IDeviceObject resource, ResourceState state)
        {
            if (resource == null) return;
            _resourceStates[resource] = state;
        }

        public void Flush(IDeviceContext context)
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

        public void ResetTransitions()
        {
            _pendingTransitions.Clear();
        }

        public void Clear()
        {
            _resourceStates.Clear();
            _pendingTransitions.Clear();
            _arrayCache.Clear();
        }
    }
}