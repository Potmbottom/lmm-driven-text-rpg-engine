using Godot;
using System;
using System.Collections.Generic;

namespace RPG.AI
{
    public enum PromptType
    {
        NextTool, // Выбор инструмента
        LocationGeneration, // Структура локации: {0}=IndicesJson, {1}=NextId, {2}=UserRequest
        ObjectsGeneration, // Наполнение объектами: {0}=LocationJson, {1}=ContextJson
        Simulation,
        Narrative,
        // --- NEW ---
        QueryOrchestrator,
        QuerySelector,
        QueryFinalizer,
        SimulationContext,
        Translator
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

        /// <summary>
        /// Возвращает текст промпта. Если переданы args, применяет string.Format.
        /// </summary>
        public string GetPrompt(PromptType key, params object[] args)
        {
            string template = GetRawPrompt(key);

            if (args != null && args.Length > 0)
            {
                try
                {
                    // Заменяем {0}, {1} и т.д. на аргументы
                    return string.Format(template, args);
                }
                catch (FormatException ex)
                {
                    GD.PrintErr($"PromptLibrary: Failed to format prompt '{key}'. Error: {ex.Message}");
                    return template; // Возвращаем как есть в случае ошибки
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

            string path = $"{PROMPT_DIR}{key}.txt";
            if (FileAccess.FileExists(path))
            {
                using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
                string content = file.GetAsText();
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