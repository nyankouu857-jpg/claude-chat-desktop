using System.Text.RegularExpressions;

namespace ClaudeChat.Native.Views;

public enum TokenKind
{
    Plain,
    Keyword,
    String,
    Comment,
    Number,
}

// highlight.js のような言語別の文法までは持たず、主要言語で共通する字句だけ色分けする
public static partial class CodeHighlighter
{
    static readonly HashSet<string> HashCommentLanguages =
        ["python", "py", "bash", "sh", "shell", "zsh", "ruby", "rb", "yaml", "yml", "toml", "powershell", "ps1", "r", "perl", "dockerfile", "makefile"];

    static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "async", "await", "break", "case", "catch", "class", "const", "continue", "def", "default",
        "del", "do", "elif", "else", "enum", "except", "export", "extends", "false", "False", "final", "finally", "fn",
        "for", "foreach", "from", "func", "function", "go", "if", "impl", "implements", "import", "in", "interface",
        "is", "lambda", "let", "match", "mod", "mut", "namespace", "new", "nil", "None", "not", "null", "of", "or",
        "and", "package", "pass", "private", "protected", "pub", "public", "raise", "readonly", "return", "self",
        "static", "struct", "super", "switch", "this", "throw", "throws", "trait", "true", "True", "try", "type",
        "typeof", "use", "using", "var", "void", "where", "while", "with", "yield", "int", "string", "bool", "float",
        "double", "long", "char", "byte", "select", "insert", "update", "delete", "join", "on", "group", "order", "by",
        "SELECT", "INSERT", "UPDATE", "DELETE", "FROM", "WHERE", "JOIN", "ON", "GROUP", "ORDER", "BY", "AND", "OR",
    ];

    [GeneratedRegex("""(?<comment>//[^\n]*|/\*[\s\S]*?\*/)|(?<string>"(?:\\.|[^"\\\n])*"|'(?:\\.|[^'\\\n])*'|`(?:\\.|[^`\\])*`)|(?<number>\b\d+(?:\.\d+)?\b)|(?<word>[A-Za-z_][A-Za-z0-9_]*)""")]
    private static partial Regex SlashCommentLexer();

    [GeneratedRegex("""(?<comment>#[^\n]*)|(?<string>"(?:\\.|[^"\\\n])*"|'(?:\\.|[^'\\\n])*')|(?<number>\b\d+(?:\.\d+)?\b)|(?<word>[A-Za-z_][A-Za-z0-9_]*)""")]
    private static partial Regex HashCommentLexer();

    public static IEnumerable<(string Text, TokenKind Kind)> Tokenize(string code, string language)
    {
        var lexer = HashCommentLanguages.Contains(language.ToLowerInvariant()) ? HashCommentLexer() : SlashCommentLexer();
        var position = 0;
        foreach (Match match in lexer.Matches(code))
        {
            var kind = match.Groups["comment"].Success ? TokenKind.Comment
                : match.Groups["string"].Success ? TokenKind.String
                : match.Groups["number"].Success ? TokenKind.Number
                : Keywords.Contains(match.Value) ? TokenKind.Keyword
                : TokenKind.Plain;
            if (kind == TokenKind.Plain) continue;
            if (match.Index > position) yield return (code[position..match.Index], TokenKind.Plain);
            yield return (match.Value, kind);
            position = match.Index + match.Length;
        }
        if (position < code.Length) yield return (code[position..], TokenKind.Plain);
    }
}
