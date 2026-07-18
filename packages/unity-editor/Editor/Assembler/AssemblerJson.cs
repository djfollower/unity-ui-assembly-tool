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

    /// The encode half of JsonParser above - needed because ReviewWindow has
    /// to write a schema-valid match-result.json back to disk after a human
    /// review edit, not just read one. Same hand-rolled convention (no
    /// Newtonsoft dependency), 2-space pretty-printed to match cli.ts's own
    /// JSON.stringify(x, null, 2) output so on-disk diffs stay readable.
    internal static class JsonWriter
    {
        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value, int indent)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case double d:
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case Dictionary<string, object> obj:
                    WriteObject(sb, obj, indent);
                    break;
                case List<object> arr:
                    WriteArray(sb, arr, indent);
                    break;
                default:
                    throw new FormatException($"JsonWriter: unsupported value type {value.GetType()}");
            }
        }

        private static void WriteObject(StringBuilder sb, Dictionary<string, object> obj, int indent)
        {
            if (obj.Count == 0)
            {
                sb.Append("{}");
                return;
            }
            sb.Append("{\n");
            var i = 0;
            foreach (var kvp in obj)
            {
                Indent(sb, indent + 1);
                WriteString(sb, kvp.Key);
                sb.Append(": ");
                WriteValue(sb, kvp.Value, indent + 1);
                if (++i < obj.Count) sb.Append(',');
                sb.Append('\n');
            }
            Indent(sb, indent);
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, List<object> arr, int indent)
        {
            if (arr.Count == 0)
            {
                sb.Append("[]");
                return;
            }
            sb.Append("[\n");
            for (var i = 0; i < arr.Count; i++)
            {
                Indent(sb, indent + 1);
                WriteValue(sb, arr[i], indent + 1);
                if (i < arr.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            Indent(sb, indent);
            sb.Append(']');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static void Indent(StringBuilder sb, int level)
        {
            sb.Append(' ', level * 2);
        }
    }

    /// One element from element-tree.json - only the fields NodeBuilder
    /// actually consumes, plus FigmaNodeId (irrelevant to assembly itself,
    /// carried through purely so the review window can join a match-result
    /// row back to its Stage 1 preview thumbnail).
    public class ElementData
    {
        public string Id;
        public string Type;
        public string FigmaNodeId; // joins against element-thumbnails.json for the review window
        public RectData Rect;
        public string TextContent; // null if absent
        public bool Container; // true: build children as real nested GameObjects, not flattened
        // true: matched as ONE unit (see match.ts's isCompositeGroup) - its
        // `Children` are real sub-layer geometry kept for provenance, but
        // NOT built individually; NodeBuilder.BuildRecursive checks this
        // before Container/Children.Count, since a composite element can
        // (and typically does) still carry children.
        public bool Composite;
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
        // NodeBuilder never reads either of these - round-tripped verbatim
        // (parsed Dictionary<string,object> / null) purely so the review
        // window can write schema-valid JSON back to disk after an edit
        // ("signals" is required, additionalProperties: false on the whole
        // entry). Understood to go stale after a human accept/reject/
        // reassign - see ReviewWindow's own comment on that tradeoff.
        public object RawSignals;
        public object RawResize;
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
        // Absolute path - LoadCatalog resolves it (on-disk it's relative to
        // catalog.json's own directory, see catalog-entry.schema.json)
        // before returning it, so nothing downstream needs to know where
        // catalog.json physically lives. Review-window preview only.
        public string ThumbnailPath;
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
                    FigmaNodeId = obj.TryGetValue("figma_node_id", out var fid) ? fid as string : null,
                    Rect = new RectData(
                        (float)(double)rect["x"],
                        (float)(double)rect["y"],
                        (float)(double)rect["w"],
                        (float)(double)rect["h"]),
                    TextContent = obj.TryGetValue("text_content", out var text) ? (string)text : null,
                    Container = obj.TryGetValue("container", out var containerVal) && containerVal is bool containerBool && containerBool,
                    Composite = obj.TryGetValue("composite", out var compositeVal) && compositeVal is bool compositeBool && compositeBool,
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
                    RawSignals = obj.TryGetValue("signals", out var signals) ? signals : null,
                    RawResize = obj.TryGetValue("resize", out var resize) ? resize : null,
                });
            }
            return result;
        }

        public static List<CatalogEntryData> LoadCatalog(string path)
        {
            var root = (List<object>)JsonParser.Parse(File.ReadAllText(path));
            // thumbnail_path on disk is relative to catalog.json's own
            // directory (not the repo root or the Unity project) - resolved
            // to absolute here, once, at this load chokepoint, same
            // convention as the Node side's loadCatalog().
            var catalogDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
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
                    // top-level sibling of "render", required
                    ThumbnailPath = Path.GetFullPath(Path.Combine(catalogDir, (string)obj["thumbnail_path"])),
                });
            }
            return result;
        }

        // element-thumbnails.json: a flat { figmaNodeId: base64DataUri }
        // sidecar written by `ui-assembler reduce-from-selection`, not part
        // of any schema (deliberately - review-UI concern, not something
        // match.ts/NodeBuilder.cs need to know about).
        public static Dictionary<string, string> LoadElementThumbnails(string path)
        {
            var root = (Dictionary<string, object>)JsonParser.Parse(File.ReadAllText(path));
            var result = new Dictionary<string, string>();
            foreach (var kvp in root) result[kvp.Key] = kvp.Value as string;
            return result;
        }

        // Mirrors LoadElementThumbnails exactly - same flat figma_node_id ->
        // base64 data URI shape (see cli.ts's element-fallback-captures.json
        // sidecar), same "caller checks File.Exists first" convention (a
        // plugin export from before this feature, or one where no Combine
        // group got a real anchor, produces an empty sidecar with no error).
        public static Dictionary<string, string> LoadElementFallbackCaptures(string path)
        {
            var root = (Dictionary<string, object>)JsonParser.Parse(File.ReadAllText(path));
            var result = new Dictionary<string, string>();
            foreach (var kvp in root) result[kvp.Key] = kvp.Value as string;
            return result;
        }

        // Writes back match-result.json after a human review edit
        // (accept/reject/reassign) - schema-valid (signals required on
        // every entry, resize omitted rather than null since the schema
        // marks it optional, not nullable).
        public static void WriteMatchResults(List<MatchResultEntry> entries, string path)
        {
            var arr = new List<object>();
            foreach (var e in entries)
            {
                var obj = new Dictionary<string, object>
                {
                    ["element_id"] = e.ElementId,
                    ["status"] = e.Status,
                    ["matched_asset_id"] = e.MatchedAssetId,
                    ["signals"] = e.RawSignals ?? new Dictionary<string, object>
                    {
                        ["visual"] = 0.0,
                        ["structural"] = 0.0,
                        ["agree"] = false,
                        ["margin"] = 0.0,
                    },
                };
                if (e.RawResize != null) obj["resize"] = e.RawResize;
                arr.Add(obj);
            }
            File.WriteAllText(path, JsonWriter.Write(arr));
        }
    }
}
