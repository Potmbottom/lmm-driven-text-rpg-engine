using Godot;
using RPG.AI.Core;
using RPG.AI.Providers;

namespace RPG.AI
{
    public partial class LmmFactory : Node
    {
        public static LmmFactory Instance { get; private set; }
        
        private string GeminiApiKey = "AIzaSyCucoc-aayWMcKymnA4CMx_zWhtJqXJmyE"; 
        
        private const string MODEL_FAST = "gemini-3-flash-preview"; // Быстро, дешево (для логики)
        private const string MODEL_SMART = "gemini-3-pro-preview";  // Умно, медленнее (для текста)

        public override void _Ready()
        {
            Instance = this;
        }

        public ILmmProvider GetProvider(LmmModelType type)
        {
            string modelName = type == LmmModelType.Smart ? MODEL_SMART : MODEL_FAST;
            return new GeminiProvider(GeminiApiKey, modelName);
        }
    }
}