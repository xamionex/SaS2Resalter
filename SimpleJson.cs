using System;
using System.Collections.Generic;

namespace SaS2Resalter;

public static class SimpleJson
{
    /// Parses a JSON object like { "weapon": { "x":0.0, "y":0.0, "b":0.0 }, ... }
    /// Returns a dictionary mapping weapon name to float[3] {x,y,b}.
    public static Dictionary<string, float[]> ParseWeaponSlots(string json)
    {
        var result = new Dictionary<string, float[]>();
        var pos = 0;

        SkipWhitespace(json, ref pos);
        Expect(json, ref pos, '{');

        while (pos < json.Length)
        {
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length || json[pos] == '}')
                break;

            var weapon = ReadString(json, ref pos);
            SkipWhitespace(json, ref pos);
            Expect(json, ref pos, ':');
            SkipWhitespace(json, ref pos);

            Expect(json, ref pos, '{');
            var slots = new float[3];
            while (pos < json.Length)
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] == '}')
                    break;

                var slotKey = ReadString(json, ref pos);
                SkipWhitespace(json, ref pos);
                Expect(json, ref pos, ':');
                SkipWhitespace(json, ref pos);
                var value = ReadNumber(json, ref pos);

                switch (slotKey)
                {
                    case "x": slots[0] = value; break;
                    case "y": slots[1] = value; break;
                    case "b": slots[2] = value; break;
                }

                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',')
                    pos++;
            }

            Expect(json, ref pos, '}');

            result[weapon] = slots;

            SkipWhitespace(json, ref pos);
            if (pos < json.Length && json[pos] == ',')
                pos++;
        }

        Expect(json, ref pos, '}');
        return result;
    }

    private static void SkipWhitespace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos]))
            pos++;
    }

    private static void Expect(string s, ref int pos, char expected)
    {
        if (pos >= s.Length || s[pos] != expected)
            throw new Exception($"Expected '{expected}' at position {pos}");
        pos++;
    }

    private static string ReadString(string s, ref int pos)
    {
        SkipWhitespace(s, ref pos);
        Expect(s, ref pos, '"');
        var start = pos;
        while (pos < s.Length && s[pos] != '"')
            pos++;
        if (pos >= s.Length)
            throw new Exception("Unterminated string");
        var value = s.Substring(start, pos - start);
        pos++; // closing quote
        return value;
    }

    private static float ReadNumber(string s, ref int pos)
    {
        SkipWhitespace(s, ref pos);
        var start = pos;
        if (pos < s.Length && s[pos] == '-')
            pos++;
        var hasDot = false;
        while (pos < s.Length && (char.IsDigit(s[pos]) || (s[pos] == '.' && !hasDot)))
        {
            if (s[pos] == '.') hasDot = true;
            pos++;
        }

        if (pos == start || (pos > start && s[start] == '.' && pos == start + 1))
            throw new Exception($"Invalid number at position {start}");
        return float.Parse(s.Substring(start, pos - start),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}