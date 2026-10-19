using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Gives every icon-only button the accessible name its tooltip already shows. With no
/// AutomationProperties.Name, Avalonia's button peer falls back to its content's
/// ToString(), so screen readers and UI Automation read the island's transport, the top
/// bar's corner icons and every "…" as "Avalonia.Controls.PathIcon" and the like (UIA
/// measured live 10-09). One class handler covers every Button (and ToggleButton,
/// RepeatButton) in the app, including ones templated in later.
/// </summary>
/// <remarks>
/// The name is written at Style priority, so an explicit AutomationProperties.Name in XAML
/// always wins, and it follows the tooltip: a state tooltip (Play/Pause) and a language
/// switch re-name the button. Only string tooltips count (a TextBlock tooltip's runs bind
/// later; those buttons name themselves), and a button whose content is already plain text
/// keeps that text as its name, which is what its peer reads. A button whose content is a
/// lone TextBlock reads that text the same way (its peer read "Avalonia.Controls.TextBlock",
/// UIA 10-13), following it as it changes; while the text is empty the tooltip names it.
/// </remarks>
public static class AccessibleNames
{
    private static readonly ConditionalWeakTable<Button, IDisposable> Mirrored = new();
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        ToolTip.TipProperty.Changed.AddClassHandler<Button>(static (button, _) => Sync(button));
        ContentControl.ContentProperty.Changed.AddClassHandler<Button>(static (button, _) => Sync(button));
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>(static (text, _) =>
        {
            if (text.Parent is Button button && button.Content == text) Sync(button);
        });
    }

    /// <summary>The name a button takes from its tooltip, or null when it has its own
    /// (plain-text content) or the tooltip carries no plain text.</summary>
    public static string? FromTip(Button button)
        => button.Content is string ? null
            : ToolTip.GetTip(button) is string tip && !string.IsNullOrWhiteSpace(tip) ? tip
            : null;

    /// <summary>The text of a button whose content is a lone TextBlock, else its tooltip's.</summary>
    private static string? NameFor(Button button)
        => button.Content is TextBlock { Text: { } text } && !string.IsNullOrWhiteSpace(text) ? text
            : FromTip(button);

    /// <summary>
    /// Names a templated row after the item it shows, for a row button or container that
    /// would otherwise read its content's type name ("Avalonia.Controls.Border", UIA 10-09):
    /// <c>AutomationProperties.Name="{Binding Converter={x:Static helpers:AccessibleNames.ItemName}}"</c>.
    /// </summary>
    public static readonly IValueConverter ItemName = new FuncValueConverter<object?, string?>(NameOfItem);

    /// <summary>A song reads "title, artist" (<see cref="Track.ToString"/>), a chart row its
    /// song, a folder "name, N songs", a lyric line its words ("..." when it has none, like
    /// the intro), a Search Lyrics source "lrc.red, Word-synced" (", Best" on Auto's pick);
    /// anything else its ToString().</summary>
    public static string? NameOfItem(object? item) => item switch
    {
        TopSongRow row => row.Track.ToString(),
        ViewModels.LyricsSearchResultRow source => source.IsBest
            ? $"{source.Source}, {source.FormatLabel}, {Loc.T("LyricsSearch.Best")}"
            : $"{source.Source}, {source.FormatLabel}",
        LyricLine line => string.IsNullOrWhiteSpace(line.Text) ? "..." : line.Text,
        FolderNode folder => $"{folder.DisplayName}, "
                             + (folder.TotalTrackCount == 1
                                 ? Loc.T("DescriptionDialog.Song")
                                 : Loc.T("DescriptionDialog.Songs", folder.TotalTrackCount)),
        _ => item?.ToString(),
    };

    private static void Sync(Button button)
    {
        if (Mirrored.TryGetValue(button, out var previous))
        {
            Mirrored.Remove(button);
            previous.Dispose();
        }
        if (NameFor(button) is not { } name) return;
        if (button.SetValue(AutomationProperties.NameProperty, name, BindingPriority.Style) is { } handle)
            Mirrored.Add(button, handle);
    }
}
