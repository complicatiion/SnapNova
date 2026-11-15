using System.Text.RegularExpressions;
using SnapNova.Core.Ocr;

namespace SnapNova.Core.Redact;

public enum RedactKind
{
    Email,
    IpAddress,
    MacAddress,
    Url,
    Phone,
    Iban,
    CreditCard,
    Guid,
    Token,
    UserPath,
    Custom
}

/// <summary>A suggestion of Smart Redact: what was found and where (pixels of the original image).</summary>
public sealed record RedactCandidate(RedactKind Kind, string Text, float X, float Y, float Width, float Height);

/// <summary>
/// Finds sensitive data in recognized text with patterns (e-mail, IP, MAC, URL/domain, phone, IBAN with check digits,
/// card numbers with Luhn check, GUID, tokens, user paths) and a word list (own name, company, user/domain/computer name).
/// Names in general cannot be found reliably offline – that is what the word list is for. Results are suggestions only.
/// </summary>
public static partial class RedactDetector
{
    private static readonly (RedactKind Kind, Regex Pattern)[] Patterns =
    [
        (RedactKind.Email, EmailRegex()),
        (RedactKind.Url, UrlRegex()),
        (RedactKind.IpAddress, Ipv4Regex()),
        (RedactKind.IpAddress, Ipv6Regex()),
        (RedactKind.MacAddress, MacRegex()),
        (RedactKind.Iban, IbanRegex()),
        (RedactKind.CreditCard, CardRegex()),
        (RedactKind.Phone, PhoneRegex()),
        (RedactKind.Guid, GuidRegex()),
        (RedactKind.UserPath, UserPathRegex()),
        (RedactKind.Token, TokenRegex())
    ];

    public static IReadOnlyList<RedactCandidate> Find(OcrResult ocr, IEnumerable<string> customWords)
    {
        var words = customWords.Where(w => w.Trim().Length >= 2).Select(w => w.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<RedactCandidate>();
        foreach (var line in ocr.Lines)
        {
            if (line.Words.Count == 0)
            {
                continue;
            }

            // Character offsets of every word in the line text.
            var text = line.Text;
            var starts = new int[line.Words.Count];
            for (int i = 0, pos = 0; i < line.Words.Count; i++)
            {
                starts[i] = pos;
                pos += line.Words[i].Text.Length + 1;
            }

            var taken = new List<(int Start, int End)>();
            foreach (var (kind, pattern) in Patterns)
            {
                foreach (Match m in pattern.Matches(text))
                {
                    if (!Valid(kind, m.Value) || taken.Any(t => m.Index < t.End && m.Index + m.Length > t.Start))
                    {
                        continue;
                    }

                    taken.Add((m.Index, m.Index + m.Length));
                    Add(result, kind, m.Value, line, starts, m.Index, m.Length);
                }
            }

            foreach (var word in words)
            {
                var index = 0;
                while ((index = text.IndexOf(word, index, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    if (!taken.Any(t => index < t.End && index + word.Length > t.Start))
                    {
                        Add(result, RedactKind.Custom, text.Substring(index, word.Length), line, starts, index, word.Length);
                    }

                    index += word.Length;
                }
            }
        }

        return result;
    }

    private static void Add(List<RedactCandidate> result, RedactKind kind, string value, OcrLine line, int[] starts, int index, int length)
    {
        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
        for (var i = 0; i < line.Words.Count; i++)
        {
            var w = line.Words[i];
            var start = starts[i];
            var end = start + w.Text.Length;
            if (end <= index || start >= index + length)
            {
                continue;
            }

            // Partial words (e.g. "Mail:max@x.de"): narrow the box proportionally to the characters.
            var from = Math.Max(index, start) - start;
            var to = Math.Min(index + length, end) - start;
            var charWidth = w.Width / Math.Max(1, w.Text.Length);
            left = Math.Min(left, w.X + from * charWidth);
            right = Math.Max(right, w.X + to * charWidth);
            top = Math.Min(top, w.Y);
            bottom = Math.Max(bottom, w.Y + w.Height);
        }

        if (right > left && bottom > top)
        {
            // A little margin so no anti-aliased pixel of the text survives.
            var pad = (bottom - top) * 0.15f;
            result.Add(new RedactCandidate(kind, value, left - pad, top - pad, right - left + pad * 2, bottom - top + pad * 2));
        }
    }

    private static bool Valid(RedactKind kind, string value) => kind switch
    {
        RedactKind.Iban => IbanValid(value),
        RedactKind.CreditCard => Luhn(value),
        RedactKind.IpAddress when value.Contains('.') => value.Split('.').All(p => int.TryParse(p, out var n) && n is >= 0 and <= 255),
        RedactKind.Phone => value.Count(char.IsDigit) >= 7,
        RedactKind.Token => value.Any(char.IsDigit) && value.Any(char.IsLetter),
        _ => true
    };

    internal static bool Luhn(string value)
    {
        var digits = value.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i];
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return sum % 10 == 0;
    }

    internal static bool IbanValid(string value)
    {
        var iban = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (iban.Length is < 15 or > 34)
        {
            return false;
        }

        var rearranged = iban[4..] + iban[..4];
        var remainder = 0;
        foreach (var c in rearranged)
        {
            var n = char.IsDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = n > 9 ? (remainder * 100 + n) % 97 : (remainder * 10 + n) % 97;
        }

        return remainder == 1;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b(?:https?://|www\.)[^\s]+|\b[a-z0-9\-]+(?:\.[a-z0-9\-]+)*\.(?:de|com|net|org|io|eu|local|lan|intern|corp|at|ch)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}(?:/\d{1,2})?\b")]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"\b(?:[0-9a-fA-F]{1,4}:){2,7}[0-9a-fA-F]{1,4}\b")]
    private static partial Regex Ipv6Regex();

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}\b")]
    private static partial Regex MacRegex();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){3,7}(?: ?[A-Z0-9]{1,3})?\b")]
    private static partial Regex IbanRegex();

    [GeneratedRegex(@"\b(?:\d[ \-]?){13,19}\b")]
    private static partial Regex CardRegex();

    [GeneratedRegex(@"(?:\+|\b00)\d{1,3}[\s/\-]?\(?\d{2,5}\)?[\s/\-]?\d{3,}[\s/\-]?\d*|\b0\d{2,5}[\s/\-]\d{3,}(?:[\s\-]\d+)?")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"[A-Za-z]:\\Users\\[^\\\s]+|/home/[^/\s]+|\\\\[A-Za-z0-9.\-]+\\[^\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex UserPathRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9_\-+/=]{24,}\b")]
    private static partial Regex TokenRegex();
}
