using System.Text;

using MockGraph.Server.Serialization;

namespace MockGraph.Server.OData;

/// <summary>
/// The slice of Entra-style query options served on /users and /groups: <c>$filter</c> (eq and startsWith on a few
/// string properties, combined with and/or), <c>$orderby</c> on displayName (and userPrincipalName for users) and
/// <c>$count</c>. Strings compare case-insensitively. Anything else is a 400.
/// </summary>
public static class DirectoryQuery
{
    public static readonly string[] Options = ["$filter", "$count", "$orderby"];

    public static (IReadOnlyList<T> Items, long? Count) Refine<T>(
        HttpRequest request, ODataQuery query, IReadOnlyList<T> all, string entity,
        IReadOnlyCollection<string> filterable, IReadOnlyCollection<string> sortable, Func<T, string, string?> property)
    {
        var filterText = query.Extra("$filter");
        var orderText = query.Extra("$orderby");
        var eventual = request.Headers["ConsistencyLevel"].ToString().Equals("eventual", StringComparison.OrdinalIgnoreCase);
        var countRequested = query.Extra("$count") switch
        {
            null => false,
            var v when v.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            var v when v.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            var v => throw GraphException.BadRequest($"Invalid value '{v}' for $count. Expected true or false."),
        };

        // Graph's "advanced query" mode is ConsistencyLevel: eventual plus $count=true; $filter with $orderby needs it.
        var advanced = eventual && countRequested;
        if (filterText is not null && orderText is not null && !advanced)
        {
            throw new GraphException(
                StatusCodes.Status400BadRequest,
                "Request_UnsupportedQuery",
                "Using $filter together with $orderby requires the ConsistencyLevel: eventual header and $count=true.");
        }

        IEnumerable<T> result = all;
        if (filterText is not null)
        {
            var node = new FilterParser(filterText, entity, filterable).Parse();
            result = result.Where(item => node.Matches(p => property(item, p)));
        }

        var list = result.ToList();
        if (orderText is not null)
        {
            var (name, descending) = ParseOrderBy(orderText, entity, sortable);
            list = descending
                ? [.. list.OrderByDescending(i => property(i, name) ?? "", StringComparer.OrdinalIgnoreCase)]
                : [.. list.OrderBy(i => property(i, name) ?? "", StringComparer.OrdinalIgnoreCase)];
        }

        // Without the ConsistencyLevel header Graph silently ignores $count=true; so do we.
        return (list, advanced ? list.Count : null);
    }

    private static (string Name, bool Descending) ParseOrderBy(string text, string entity, IReadOnlyCollection<string> sortable)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = sortable.FirstOrDefault(p => parts.Length is 1 or 2 && p.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        var direction = parts.Length == 2 ? parts[1].ToLowerInvariant() : "asc";
        return name is null || direction is not ("asc" or "desc")
            ? throw GraphException.BadRequest($"$orderby '{text}' is not supported for {entity}. Supported: {string.Join(", ", sortable)} [asc|desc].")
            : (name, direction == "desc");
    }

    internal abstract record Node
    {
        public abstract bool Matches(Func<string, string?> property);
    }

    private sealed record Comparison(string Property, bool Prefix, string Value) : Node
    {
        public override bool Matches(Func<string, string?> property) => property(Property) is { } actual
            && (Prefix
                ? actual.StartsWith(Value, StringComparison.OrdinalIgnoreCase)
                : actual.Equals(Value, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record Logical(bool IsAnd, Node Left, Node Right) : Node
    {
        public override bool Matches(Func<string, string?> property) => IsAnd
            ? Left.Matches(property) && Right.Matches(property)
            : Left.Matches(property) || Right.Matches(property);
    }

    /// <summary>Recursive descent over: or := and ('or' and)*; and := term ('and' term)*; term := '(' or ')' | prop 'eq' 'text' | startswith(prop,'text').</summary>
    private sealed class FilterParser
    {
        private readonly List<string> _tokens = [];
        private readonly string _entity;
        private readonly IReadOnlyCollection<string> _props;
        private readonly string _text;
        private int _pos;

        public FilterParser(string text, string entity, IReadOnlyCollection<string> props)
        {
            (_text, _entity, _props) = (text, entity, props);
            Tokenize();
        }

        public Node Parse()
        {
            if (_tokens.Count == 0)
            {
                throw Unsupported("$filter must not be empty.");
            }

            var node = ParseOr();
            return _pos == _tokens.Count ? node : throw Unsupported($"Unexpected '{_tokens[_pos]}' in $filter.");
        }

        private static bool IsString(string token) => token.Length > 0 && token[0] == '\'';

        private GraphException Unsupported(string detail) => GraphException.BadRequest(
            $"{detail} Supported on {_entity}: eq and startsWith on {string.Join(", ", _props)}, combined with and/or.");

        private void Tokenize()
        {
            for (var i = 0; i < _text.Length;)
            {
                var c = _text[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (c is '(' or ')' or ',')
                {
                    _tokens.Add(c.ToString());
                    i++;
                }
                else if (c == '\'')
                {
                    var value = new StringBuilder("'");
                    i++;
                    while (true)
                    {
                        if (i >= _text.Length)
                        {
                            throw Unsupported("Unterminated string literal in $filter.");
                        }

                        if (_text[i] == '\'' && i + 1 < _text.Length && _text[i + 1] == '\'')
                        {
                            value.Append('\'');
                            i += 2;
                        }
                        else if (_text[i] == '\'')
                        {
                            i++;
                            break;
                        }
                        else
                        {
                            value.Append(_text[i++]);
                        }
                    }

                    _tokens.Add(value.ToString());
                }
                else if (char.IsLetterOrDigit(c) || c is '_' or '.' or '/' or '$')
                {
                    var start = i;
                    while (i < _text.Length && (char.IsLetterOrDigit(_text[i]) || _text[i] is '_' or '.' or '/' or '$' or '-'))
                    {
                        i++;
                    }

                    _tokens.Add(_text[start..i]);
                }
                else
                {
                    throw Unsupported($"Unsupported character '{c}' in $filter.");
                }
            }
        }

        private bool Peek(string word) => _pos < _tokens.Count && _tokens[_pos].Equals(word, StringComparison.OrdinalIgnoreCase);

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (Peek("or"))
            {
                _pos++;
                left = new Logical(false, left, ParseAnd());
            }

            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseTerm();
            while (Peek("and"))
            {
                _pos++;
                left = new Logical(true, left, ParseTerm());
            }

            return left;
        }

        private Node ParseTerm()
        {
            var token = Next();
            if (token == "(")
            {
                var inner = ParseOr();
                Expect(")");
                return inner;
            }

            if (token.Equals("startswith", StringComparison.OrdinalIgnoreCase))
            {
                Expect("(");
                var property = Property(Next());
                Expect(",");
                var value = Literal(Next());
                Expect(")");
                return new Comparison(property, true, value);
            }

            if (token.Equals("not", StringComparison.OrdinalIgnoreCase) || token.Equals("endswith", StringComparison.OrdinalIgnoreCase)
                || token.Equals("contains", StringComparison.OrdinalIgnoreCase))
            {
                throw Unsupported($"'{token}' is not supported in $filter.");
            }

            var name = Property(token);
            var op = Next();
            if (!op.Equals("eq", StringComparison.OrdinalIgnoreCase))
            {
                throw Unsupported($"Operator '{op}' is not supported in $filter.");
            }

            return new Comparison(name, false, Literal(Next()));
        }

        private string Next() => _pos < _tokens.Count ? _tokens[_pos++] : throw Unsupported("Incomplete $filter expression.");

        private void Expect(string token)
        {
            var actual = Next();
            if (actual != token)
            {
                throw Unsupported($"Expected '{token}' but found '{actual}' in $filter.");
            }
        }

        private string Property(string token) =>
            _props.FirstOrDefault(p => p.Equals(token, StringComparison.OrdinalIgnoreCase))
            ?? throw Unsupported($"Property '{token}' is not supported in $filter.");

        private string Literal(string token) => IsString(token)
            ? token[1..]
            : throw Unsupported($"Expected a quoted string but found '{token}' in $filter.");
    }
}