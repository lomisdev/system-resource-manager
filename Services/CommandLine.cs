// Services/CommandLine.cs
using System;
using System.Collections.Generic;
using System.Text;

namespace System_Resource_Manager.Services;

public static class CommandLine
{
    // Whitespace separates arguments; 'single' or "double" quotes group words. No escapes, no expansion.
    public static IReadOnlyList<string> Split(string input)
    {
        var args = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        bool inToken = false;      // tracks "" so an empty quoted argument still counts

        foreach (var c in input)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else sb.Append(c);
            }
            else if (c is '"' or '\'') { quote = c; inToken = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken) { args.Add(sb.ToString()); sb.Clear(); inToken = false; }
            }
            else { sb.Append(c); inToken = true; }
        }

        if (quote != '\0') throw new ArgumentException("Unterminated quote.");
        if (inToken) args.Add(sb.ToString());
        return args;
    }
}