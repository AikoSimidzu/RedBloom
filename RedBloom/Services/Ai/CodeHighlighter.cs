using System.Text;

namespace RedBloom.Services.Ai;

/// <summary>
/// A small, language-agnostic syntax colourer for the code blocks in chat replies.
/// </summary>
/// <remarks>
/// Runs on the raw code before it becomes HTML, and everything it emits is either a fixed tag
/// this file writes or text passed through <see cref="Markdown.Escape"/> — the same rule the rest
/// of the markdown renderer follows, so a reply still cannot smuggle markup onto the page. It
/// parses no single language: it recognises the lexical shapes common to most of them — comments,
/// strings, numbers and a broad set of keywords — which colours real code well without a grammar
/// per language, and mis-colours the odd word without ever breaking the text.
/// </remarks>
public static class CodeHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "else", "elif", "elsif", "for", "foreach", "while", "do", "switch", "case",
        "default", "break", "continue", "goto", "return", "yield", "await", "async", "go",
        "defer", "function", "func", "fn", "def", "lambda", "class", "struct", "enum",
        "interface", "trait", "impl", "namespace", "module", "package", "using", "import",
        "include", "from", "export", "public", "private", "protected", "internal", "static",
        "const", "final", "abstract", "virtual", "override", "readonly", "let", "var", "val",
        "dim", "new", "delete", "typeof", "instanceof", "sizeof", "void", "int", "long",
        "short", "byte", "float", "double", "bool", "boolean", "char", "string", "str",
        "object", "auto", "unsigned", "signed", "try", "catch", "finally", "throw", "throws",
        "raise", "except", "with", "as", "in", "is", "and", "or", "not", "then", "begin",
        "end", "match", "when", "where", "select", "echo", "local", "global", "this", "self",
        "super", "base", "operator", "template", "typename", "extends", "implements", "type",
        "record", "init", "get", "set", "of", "pub", "mut", "use", "fun", "when",
    };

    private static readonly HashSet<string> Literals = new(StringComparer.Ordinal)
    {
        "true", "false", "null", "nil", "none", "undefined", "NaN",
        "True", "False", "None", "NULL",
    };

    /// <summary>Colours <paramref name="code"/> and returns page-safe HTML.</summary>
    public static string Highlight(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return string.Empty;
        }

        var html = new StringBuilder(code.Length + 64);
        var i = 0;
        var n = code.Length;

        while (i < n)
        {
            var c = code[i];

            // Block comment: /* … */, closed or not.
            if (c == '/' && i + 1 < n && code[i + 1] == '*')
            {
                var start = i;
                i += 2;
                while (i < n && !(code[i - 1] == '*' && code[i] == '/'))
                {
                    i++;
                }

                if (i < n)
                {
                    i++;
                }

                Span(html, "tok-com", code[start..i]);
                continue;
            }

            // Line comment: // … to end of line.
            if (c == '/' && i + 1 < n && code[i + 1] == '/')
            {
                var start = i;
                while (i < n && code[i] != '\n')
                {
                    i++;
                }

                Span(html, "tok-com", code[start..i]);
                continue;
            }

            // Line comment: # … — but only when it reads like one. A '#' glued to what follows
            // is almost always #include, #define or a #rrggbb colour, so those keep their colour.
            if (c == '#' && (i + 1 >= n || code[i + 1] is ' ' or '\t' or '!'))
            {
                var start = i;
                while (i < n && code[i] != '\n')
                {
                    i++;
                }

                Span(html, "tok-com", code[start..i]);
                continue;
            }

            // String / char / template literal.
            if (c is '"' or '\'' or '`')
            {
                var quote = c;
                var start = i;
                i++;

                while (i < n)
                {
                    // Backslash escapes only in the quote forms that use them; a backtick literal
                    // takes its text verbatim.
                    if (code[i] == '\\' && quote != '`' && i + 1 < n)
                    {
                        i += 2;
                        continue;
                    }

                    if (code[i] == quote)
                    {
                        i++;
                        break;
                    }

                    // An unterminated quote on a line is treated as ending there rather than
                    // swallowing the rest of the block.
                    if (code[i] == '\n' && quote != '`')
                    {
                        break;
                    }

                    i++;
                }

                Span(html, "tok-str", code[start..i]);
                continue;
            }

            // Number. Reached only at a token boundary — an identifier consumes its own trailing
            // digits below — so a digit here really does start a number.
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(code[i + 1])))
            {
                var start = i;
                i++;

                while (i < n)
                {
                    var d = code[i];

                    if (char.IsAsciiLetterOrDigit(d) || d is '_' or '.')
                    {
                        i++;
                        continue;
                    }

                    // A sign is part of the number only as an exponent: 1e-9, not 1-9.
                    if (d is '+' or '-' && code[i - 1] is 'e' or 'E')
                    {
                        i++;
                        continue;
                    }

                    break;
                }

                Span(html, "tok-num", code[start..i]);
                continue;
            }

            // Identifier: keyword, literal, a call, or plain text.
            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                i++;
                while (i < n && (char.IsAsciiLetterOrDigit(code[i]) || code[i] == '_'))
                {
                    i++;
                }

                var word = code[start..i];

                // A name directly before "(" is being called or defined — worth its own colour.
                var j = i;
                while (j < n && code[j] is ' ' or '\t')
                {
                    j++;
                }

                var isCall = j < n && code[j] == '(';

                if (Keywords.Contains(word))
                {
                    Span(html, "tok-kw", word);
                }
                else if (Literals.Contains(word))
                {
                    Span(html, "tok-lit", word);
                }
                else if (isCall)
                {
                    Span(html, "tok-fn", word);
                }
                else
                {
                    html.Append(Markdown.Escape(word));
                }

                continue;
            }

            AppendChar(html, c);
            i++;
        }

        return html.ToString();
    }

    private static void Span(StringBuilder html, string cls, string text) =>
        html.Append("<span class=\"").Append(cls).Append("\">")
            .Append(Markdown.Escape(text))
            .Append("</span>");

    private static void AppendChar(StringBuilder html, char c)
    {
        switch (c)
        {
            case '&': html.Append("&amp;"); break;
            case '<': html.Append("&lt;"); break;
            case '>': html.Append("&gt;"); break;
            case '"': html.Append("&quot;"); break;
            default: html.Append(c); break;
        }
    }
}
