using Godot;
using System;
using RPG.Tools;

namespace RPG.Core
{
    public partial class InputHandler : Node
    {
        [Export] public ToolController Controller;
        [Export] public MapGenerator MapGen;

        public void ProcessInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return;

            if (input.StartsWith("/"))
            {
                HandleCommand(input);
            }
            else
            {
                Controller.StartTurn(input);
            }
        }

        private void HandleCommand(string command)
        {
            var cmd = command.ToLower().Trim();

            switch (cmd)
            {
                case "/accept":
                    if (Controller.HasPendingChanges)
                        Controller.CommitTurn();
                    else
                        GD.Print("No pending changes to accept.");
                    break;

                case "/reject":
                    Controller.DiscardTurn();
                    GD.Print("Changes discarded.");
                    break;
                
                case "/map":
                    _ = new MapCommand(MapGen).ExecuteAsync();
                    break;
                case "/undo":
                    StateManager.Instance.RollbackOneVersion();
                    break;

                default:
                    GD.Print($"Unknown command: {cmd}");
                    break;
            }
        }
    }
}