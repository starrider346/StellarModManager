using System;
using System.IO;
using System.Linq;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace StellarModManager.Services;

internal static class MelonLoaderConfig
{
    private const string ConsoleTable = "console";
    private const string HideConsoleKey = "hide_console";

    internal static void SetConsoleHidden(string gamePath, bool hidden)
    {
        var directory = Path.Combine(gamePath, "UserData");
        var path = Path.Combine(directory, "Loader.cfg");
        string? original = null;
        try
        {
            original = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {}

        var updated = UpdateConsoleSetting(original ?? "", hidden);
        if (updated == original)
            return;
        if (SyntaxParser.Parse(updated, validate: true).HasErrors)
            throw new InvalidDataException("Loader.cfg has a conflicting console setting.");

        Directory.CreateDirectory(directory);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, updated);
            if (original != null && File.ReadAllText(path) != original)
                throw new IOException("Loader.cfg changed while it was being updated.");

            File.Move(temporaryPath, path, overwrite: original != null);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string UpdateConsoleSetting(string text, bool hidden)
    {
        var document = SyntaxParser.Parse(text, validate: true);
        if (document.HasErrors)
            throw new InvalidDataException("Loader.cfg contains invalid TOML.");

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var table = document.Tables.OfType<TableSyntax>().FirstOrDefault(table => IsKey(table.Name, ConsoleTable));
        var rootOptions = document.KeyValues.Where(item => KeyText(item.Key?.Key) == ConsoleTable).ToArray();
        var inline = rootOptions.FirstOrDefault(item => IsKey(item.Key, ConsoleTable))?.Value as InlineTableSyntax;
        var option = table?.Items.FirstOrDefault(item => IsKey(item.Key, HideConsoleKey)) ??
            inline?.Items.Select(item => item.KeyValue).FirstOrDefault(item => IsKey(item?.Key, HideConsoleKey)) ??
            rootOptions.FirstOrDefault(item => item.Key is { DotKeys.ChildrenCount: 1 } key &&
                KeyText(key.DotKeys.GetChild(0)?.Key) == HideConsoleKey);

        if (option?.Value is BooleanValueSyntax { Token: { } token } current)
        {
            if (current.Value == hidden)
                return text;

            token.TokenKind = hidden ? TokenKind.True : TokenKind.False;
            token.Text = hidden ? "true" : "false";
        }
        else if (option != null)
        {
            throw new InvalidDataException("Loader.cfg's hide_console setting must be true or false.");
        }
        else
        {
            var setting = new KeyValueSyntax(HideConsoleKey, new BooleanValueSyntax(hidden));
            if (table != null)
            {
                if (table.Items.LastOrDefault() is { } last)
                    last.EndOfLineToken ??= new(TokenKind.NewLine, newline);
                else
                    table.EndOfLineToken ??= new(TokenKind.NewLine, newline);
                setting.EndOfLineToken = new(TokenKind.NewLine, newline);
                table.Items.Add(setting);
            }
            else if (inline != null)
            {
                if (inline.Items.LastOrDefault() is { } last)
                    last.Comma ??= new(TokenKind.Comma, ", ");
                setting.EndOfLineToken = null;
                inline.Items.Add(new InlineTableItemSyntax(setting));
            }
            else if (rootOptions.Any(item => item.Key is { DotKeys.ChildrenCount: > 0 }))
            {
                var key = new KeySyntax(ConsoleTable);
                key.DotKeys.Add(new DottedKeyItemSyntax(HideConsoleKey));
                setting.Key = key;
                setting.EndOfLineToken = new(TokenKind.NewLine, newline);
                if (document.KeyValues.LastOrDefault() is { } last)
                    last.EndOfLineToken ??= new(TokenKind.NewLine, newline);
                document.KeyValues.Add(setting);
            }
            else
            {
                var separator = text.Length == 0 || text.EndsWith('\n') ? "" : newline;
                return $"{text}{separator}[{ConsoleTable}]{newline}{HideConsoleKey} = {(hidden ? "true" : "false")}{newline}";
            }
        }

        return document.ToString();
    }

    private static bool IsKey(KeySyntax? key, string name) => key is { DotKeys.ChildrenCount: 0 } && KeyText(key.Key) == name;

    private static string? KeyText(BareKeyOrStringValueSyntax? key) => key switch
    {
        BareKeySyntax bare => bare.Key?.Text,
        StringValueSyntax quoted => quoted.Value,
        _ => null
    };
}
