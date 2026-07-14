using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace UiAssemblerSlice.Editor.Assembler
{
    /// T3.2 support: NodeBuilder needs to read three JSON files
    /// (element-tree.json, match-result.json, catalog.json) that are all
    /// either top-level arrays or carry nullable/optional fields -
    /// JsonUtility can't parse either shape. Hand-rolled rather than adding
    /// Newtonsoft Json.NET as a new Unity package dependency (confirmed
    /// absent from the Melon project's manifest.json) - matches
    /// RunCatalogBuild.cs's existing precedent of a hand-rolled JSON
    /// encoder for the same reason, just the decoding half.
    public static class JsonParser
    {
        public static object Parse(string json)
        {
            var index = 0;
            var value = ParseValue(json, ref index);
            SkipWhitespace(json, ref index);
            if (index != json.Length)
            {
                throw new FormatException($"JsonParser: unexpected trailing content at index {index}");
            }
            return value;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("JsonParser: unexpected end of input");
            switch (s[i])
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't':
                    Expect(s, ref i, "true");
                    return true;
                case 'f':
                    Expect(s, ref i, "false");
                    return false;
                case 'n':
                    Expect(s, ref i, "null");
                    return null;
                default:
                    return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var result = new Dictionary<string, object>();
            i++; // consume '{'
            SkipWhitespace(s, ref i);
            if (Peek(s, i) == '}')
            {
                i++;
                return result;
            }
            while (true)
            {
                SkipWhitespace(s, ref i);
                var key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (Peek(s, i) != ':') throw new FormatException($"JsonParser: expected ':' at index {i}");
                i++;
                var value = ParseValue(s, ref i);
                result[key] = value;
                SkipWhitespace(s, ref i);
                var c = Peek(s, i);
                if (c == ',')
                {
                    i++;
                    continue;
                }
                if (c == '}')
                {
                    i++;
                    break;
                }
                throw new FormatException($"JsonParser: expected ',' or '}}' at index {i}");
            }
            return result;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var result = new List<object>();
            i++; // consume '['
            SkipWhitespace(s, ref i);
            if (Peek(s, i) == ']')
            {
                i++;
                return result;
            }
            while (true)
            {
                var value = ParseValue(s, ref i);
                result.Add(value);
                SkipWhitespace(s, ref i);
                var c = Peek(s, i);
                if (c == ',')
                {
                    i++;
                    continue;
                }
                if (c == ']')
                {
                    i++;
                    break;
                }
                throw new FormatException($"JsonParser: expected ',' or ']' at index {i}");
            }
            return result;
        }

        private static string ParseString(string s, ref int i)
        {
            if (Peek(s, i) != '"') throw new FormatException($"JsonParser: expected '\"' at index {i}");
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("JsonParser: unterminated string");
                var c = s[i++];
                if (c == '"') break;
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (i >= s.Length) throw new FormatException("JsonParser: unterminated escape sequence");
                var escape = s[i++];
                switch (escape)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        var hex = s.Substring(i, 4);
                        sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default:
                        throw new FormatException($"JsonParser: invalid escape '\\{escape}' at index {i}");
                }
            }
            return sb.ToString();
        }

        private static double ParseNumber(string s, ref int i)
        {
            var start = i;
            if (Peek(s, i) == '-') i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (Peek(s, i) == '.')
            {
                i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
            }
            if (Peek(s, i) == 'e' || Peek(s, i) == 'E')
            {
                i++;
                if (Peek(s, i) == '+' || Peek(s, i) == '-') i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
            }
            if (i == start) throw new FormatException($"JsonParser: invalid number at index {i}");
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || s.Substring(i, literal.Length) != literal)
            {
                throw new FormatException($"JsonParser: expected '{literal}' at index {i}");
            }
            i += literal.Length;
        }

        private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }
    }

    /// One element from element-tree.json - only the fields NodeBuilder
    /// actually consumes (visual_description, figma_node_id etc. are
    /// irrelevant to assembly).
    public class ElementData
    {
        public string Id;
        public string Type;
        public RectData Rect;
        public string TextContent; // null if absent
        public List<ElementData> Children = new List<ElementData>();
    }

    public readonly struct RectData
    {
        public readonly float X, Y, W, H;
        public RectData(float x, float y, float w, float h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }
    }

    public class ElementTreeData
    {
        public string FrameId;
        public Vector2Data CanvasReference;
        public string CanvasMatchMode;
        public float CanvasMatchValue;
        public List<ElementData> Elements = new List<ElementData>();
    }

    public readonly struct Vector2Data
    {
        public readonly float W, H;
        public Vector2Data(float w, float h)
        {
            W = w;
            H = h;
        }
    }

    /// One entry from match-result.json - signals/resize aren't needed to
    /// build (the element's own rect already carries the target size;
    /// ppu_multiplier for 9-slice correctness comes from catalog.json).
    public class MatchResultEntry
    {
        public string ElementId;
        public string Status; // matched | uncertain | missing
        public string MatchedAssetId; // null when status is missing
    }

    /// One entry from catalog.json - ppu/native_size/thumbnail aren't needed
    /// (baked into the Sprite asset itself or only needed upstream for
    /// rendering the catalog thumbnail/matcher comparisons). Border IS
    /// needed here (unlike the original design) - see NodeBuilder's
    /// RenderSlicedToTexture: UI.Image's own built-in Sliced-border
    /// rendering turned out to be broken for this project's sprites (Tight
    /// mesh type + packed/compressed atlas), so NodeBuilder pre-composites
    /// Sliced sprites itself using the same literal texture-pixel border
    /// convention RenderedThumbnail.cs/render-candidate.ts already use,
    /// rather than trusting Unity's own Image/Sliced code path.
    public class CatalogEntryData
    {
        public string Id;
        public string Path;
        public string Type; // sprite | prefab
        public string ImageType; // Simple | Sliced | Tiled | Filled
        public float PpuMultiplier;
        public float[] Border; // [left, bottom, right, top], texture pixels; null for Simple entries
        public string TintHex; // null if untinted
    }

    public static class AssemblerJson
    {
        public static ElementTreeData LoadElementTree(string path)
        {
            var root = (Dictionary<string, object>)JsonParser.Parse(File.ReadAllText(path));
            var canvasReference = (Dictionary<string, object>)root["canvas_reference"];
            return new ElementTreeData
            {
                FrameId = (string)root["frame_id"],
                CanvasReference = new Vector2Data(
                    (float)(double)canvasReference["w"],
                    (float)(double)canvasReference["h"]),
                CanvasMatchMode = (string)root["canvas_match_mode"],
                CanvasMatchValue = (float)(double)root["canvas_match_value"],
                Elements = ParseElements((List<object>)root["elements"]),
            };
        }

        private static List<ElementData> ParseElements(List<object> raw)
        {
            var result = new List<ElementData>();
            foreach (var item in raw)
            {
                var obj = (Dictionary<string, object>)item;
                var rect = (Dictionary<string, object>)obj["rect"];
                result.Add(new ElementData
                {
                    Id = (string)obj["id"],
                    Type = (string)obj["type"],
                    Rect = new RectData(
                        (float)(double)rect["x"],
                        (float)(double)rect["y"],
                        (float)(double)rect["w"],
                        (float)(double)rect["h"]),
                    TextContent = obj.TryGetValue("text_content", out var text) ? (string)text : null,
                    Children = ParseElements((List<object>)obj["children"]),
                });
            }
            return result;
        }

        public static List<MatchResultEntry> LoadMatchResults(string path)
        {
            var root = (List<object>)JsonParser.Parse(File.ReadAllText(path));
            var result = new List<MatchResultEntry>();
            foreach (var item in root)
            {
                var obj = (Dictionary<string, object>)item;
                result.Add(new MatchResultEntry
                {
                    ElementId = (string)obj["element_id"],
                    Status = (string)obj["status"],
                    MatchedAssetId = obj.TryGetValue("matched_asset_id", out var id) ? id as string : null,
                });
            }
            return result;
        }

        public static List<CatalogEntryData> LoadCatalog(string path)
        {
            var root = (List<object>)JsonParser.Parse(File.ReadAllText(path));
            var result = new List<CatalogEntryData>();
            foreach (var item in root)
            {
                var obj = (Dictionary<string, object>)item;
                var render = (Dictionary<string, object>)obj["render"];
                result.Add(new CatalogEntryData
                {
                    Id = (string)obj["id"],
                    Path = (string)obj["path"],
                    Type = (string)obj["type"],
                    ImageType = (string)render["image_type"],
                    PpuMultiplier = (float)(double)render["ppu_multiplier"],
                    Border = ((List<object>)render["border"]).Select(b => (float)(double)b).ToArray(),
                    TintHex = render.TryGetValue("tint", out var tint) ? tint as string : null,
                });
            }
            return result;
        }
    }
}
