using Godot;
using RPG.AI.Core;
using RPG.AI.Providers;

namespace RPG.AI
{
    public partial class LmmFactory : Node
    {
        public static LmmFactory Instance { get; private set; }
        
        [ExportCategory("API Settings")]
        [Export] private string GeminiApiKey = "AIzaSyCucoc-aayWMcKymnA4CMx_zWhtJqXJmyE"; 
        
        // По умолчанию localhost для ПК.
        // Для телефона нужно будет вписать сюда IP из Tailscale (например, http://100.x.y.z:1234)
        [Export] private string LocalLmmUrl = "http://localhost:1234";

        private const string MODEL_FAST = "gemini-3-flash-preview"; // Быстро, дешево (для логики)
        private const string MODEL_SMART = "gemini-3-pro-preview";  // Умно, медленнее (для текста)

        public override void _Ready()
        {
            Instance = this;
        }

        public ILmmProvider GetProvider(LmmModelType type)
        {
            switch (type)
            {
                case LmmModelType.Local:
                    // Передаем URL из настройки
                    return new LocalLmmProvider(LocalLmmUrl);

                case LmmModelType.Smart:
                    return new GeminiProvider(GeminiApiKey, MODEL_SMART);
                
                case LmmModelType.Fast:
                default:
                    return new GeminiProvider(GeminiApiKey, MODEL_FAST);
            }
        }
    }
}