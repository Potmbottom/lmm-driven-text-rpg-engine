using System;

namespace RPG.Core
{
    public interface ITool
    {
        string ToolName { get; }
        
        void Call(string parameters);

        // События
        event Action<string> OnUpdate;
        event Action<string> OnComplete;
        event Action<string> OnFail;
    }
}