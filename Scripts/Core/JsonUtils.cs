using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace RPG.Core
{
    public static class JsonUtils
    {
        private static readonly JsonSerializerOptions _options;

        static JsonUtils()
        {
            _options = new JsonSerializerOptions
            {
                // Форматировать с отступами (удобно для отладки и чтения файла)
                WriteIndented = true, 

                // Не экранировать кириллицу (чтобы текст оставался читаемым)
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic),

                // Игнорировать свойства со значением null (экономия токенов и места)
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

                // Разрешить комментарии в JSON (иногда полезно при ручной правке)
                ReadCommentHandling = JsonCommentHandling.Skip,
                
                // Регистронезависимость при десериализации (LMM может ошибиться с регистром)
                PropertyNameCaseInsensitive = true,
                
                // Разрешить запятые в конце списков (LMM часто грешат этим)
                AllowTrailingCommas = true
            };
        }

        // Сериализация объекта в строку
        public static string Serialize<T>(T obj)
        {
            return JsonSerializer.Serialize(obj, _options);
        }

        // Десериализация строки в объект
        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return default;
            return JsonSerializer.Deserialize<T>(json, _options);
        }
        
        // Попытка десериализации (безопасная)
        public static bool TryDeserialize<T>(string json, out T result)
        {
            try
            {
                result = Deserialize<T>(json);
                return result != null;
            }
            catch
            {
                result = default;
                return false;
            }
        }
    }
}