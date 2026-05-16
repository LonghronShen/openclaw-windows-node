using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpenClaw
{
    /// <summary>
    /// JSON serializer/deserializer wrapper for .NET Framework 2.0.
    /// Backed by Newtonsoft.Json while preserving the existing helper API.
    /// </summary>
    public static class JsonHelper
    {
        /// <summary>
        /// Serialize a .NET object to a compact JSON string.
        /// </summary>
        public static string Serialize(object obj)
        {
            return JsonConvert.SerializeObject(obj, Formatting.None);
        }

        /// <summary>
        /// Parse a JSON string into .NET objects.
        /// Returns Dictionary&lt;string, object&gt;, List&lt;object&gt;, or a primitive.
        /// </summary>
        public static object Parse(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException("json");
            }

            using (StringReader sr = new StringReader(json))
            using (JsonTextReader reader = new JsonTextReader(sr))
            {
                // Preserve string values exactly instead of auto-converting ISO dates.
                reader.DateParseHandling = DateParseHandling.None;
                JToken token = JToken.ReadFrom(reader);
                return ConvertToken(token);
            }
        }

        private static object ConvertToken(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            switch (token.Type)
            {
                case JTokenType.Object:
                    return ConvertObject((JObject)token);
                case JTokenType.Array:
                    return ConvertArray((JArray)token);
                case JTokenType.Integer:
                    {
                        long value = token.Value<long>();
                        if (value >= int.MinValue && value <= int.MaxValue)
                        {
                            return (int)value;
                        }

                        return value;
                    }
                case JTokenType.Float:
                    return token.Value<double>();
                case JTokenType.Boolean:
                    return token.Value<bool>();
                case JTokenType.String:
                    return token.Value<string>();
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return null;
                default:
                    {
                        JValue value = token as JValue;
                        if (value != null)
                        {
                            return value.Value;
                        }

                        return token.ToString(Formatting.None);
                    }
            }
        }

        private static Dictionary<string, object> ConvertObject(JObject obj)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            foreach (JProperty property in obj.Properties())
            {
                result[property.Name] = ConvertToken(property.Value);
            }

            return result;
        }

        private static List<object> ConvertArray(JArray array)
        {
            List<object> result = new List<object>();
            foreach (JToken item in array)
            {
                result.Add(ConvertToken(item));
            }

            return result;
        }
    }
}
