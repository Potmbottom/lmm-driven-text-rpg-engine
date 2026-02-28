using Godot;
using System;
using System.Collections.Generic;

namespace RPG.AI
{
    public enum PromptType
    {
        QueryOrchestrator,
        QuerySelector,
        QueryFinalizer,
        
        Simulation,
        SimulationContext,
        SimulationQueryParser,
        
        GenerationRules,
        GenerationObject,
        GenerationObjects,
        GenerationLocation,
        GenerationQueryParser,
        GenerationKeys,

        NextTool,
        Translator,
        Narrative,
        UnsafeNarrative,
        HistoryCompressor
    }

    public partial class PromptLibrary : Node
    {
        public static PromptLibrary Instance { get; private set; }

        private Dictionary<PromptType, string> _promptCache = new();
        private const string PROMPT_DIR = "res://Prompts/";

        public override void _Ready()
        {
            Instance = this;
            EnsureDirectoryExists();
        }
        
        public string GetPrompt(PromptType key, params object[] args)
        {
            var template = GetRawPrompt(key);

            if (args != null && args.Length > 0)
            {
                try
                {
                    return string.Format(template, args);
                }
                catch (FormatException ex)
                {
                    GD.PrintErr($"PromptLibrary: Failed to format prompt '{key}'. Error: {ex.Message}");
                    return template;
                }
            }

            return template;
        }

        private string GetRawPrompt(PromptType key)
        {
            if (_promptCache.TryGetValue(key, out var prompt))
            {
                return prompt;
            }

            var path = $"{PROMPT_DIR}{key}.txt";
            if (FileAccess.FileExists(path))
            {
                using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
                var content = file.GetAsText();
                _promptCache[key] = content;
                return content;
            }
            else
            {
                GD.PrintErr($"PromptLibrary: File not found at {path}");
                return $"Error: Prompt file for {key} missing.";
            }
        }

        public void ReloadAll()
        {
            _promptCache.Clear();
            GD.Print("PromptLibrary: Cache cleared.");
        }

        private void EnsureDirectoryExists()
        {
            if (!DirAccess.DirExistsAbsolute(PROMPT_DIR))
            {
                DirAccess.MakeDirAbsolute(PROMPT_DIR);
            }
        }
    }
}