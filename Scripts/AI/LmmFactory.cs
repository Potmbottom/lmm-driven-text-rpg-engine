using Godot;
using RPG.AI.Core;
using RPG.AI.Providers;

namespace RPG.AI
{
    public partial class LmmFactory : Node
    {
        public static LmmFactory Instance { get; private set; }

        [ExportCategory("API Settings")] [Export] private string GeminiApiKey;
        [Export] private string LocalLmmUrl = "http://localhost:1234";

        private const string MODEL_FAST = "gemini-3-flash-preview";
        private const string MODEL_SMART = "gemini-3-pro-preview";
        
        private GeminiProvider _cachedSmartGeminiProvider;
        private GeminiProvider _cachedFastGeminiProvider;
        private LocalLmmProvider _cachedLocalLmmProvider;

        public override void _Ready()
        {
            Instance = this;
        }

        public ILmmProvider GetProvider(LmmModelType type)
        {
            switch (type)
            {
                case LmmModelType.Local:
                    return _cachedLocalLmmProvider ??= new LocalLmmProvider(LocalLmmUrl);

                case LmmModelType.Smart:
                    return _cachedSmartGeminiProvider ??= new GeminiProvider(GeminiApiKey, MODEL_SMART);

                case LmmModelType.Fast:
                default:
                    return _cachedFastGeminiProvider ??= new GeminiProvider(GeminiApiKey, MODEL_FAST);
            }
        }
    }
}