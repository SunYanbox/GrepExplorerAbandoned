using System.Windows;
using GrepExplorer.UI.Dialogs;
using Microsoft.Win32;

namespace GrepExplorer.UI.Services;

/// <summary>WPF 标准对话框实现。</summary>
public sealed class DialogService : IDialogService
{
    public string? PickFolder(string start)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = start };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public string? PromptText(string title, string message, string initial)
    {
        var dlg = new InputWindow(title, message, initial) { Owner = Application.Current?.MainWindow };
        return dlg.ShowDialog() == true ? dlg.InputText : null;
    }

    public string? PickSaveFile(string defaultName, string filter)
    {
        var dlg = new SaveFileDialog { FileName = defaultName, Filter = filter, AddExtension = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PickOpenFile(string filter)
    {
        var dlg = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public bool Confirm(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public void Info(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public UnsavedResult ShowUnsavedChanges(string? baseline, string current, Func<string?, string?> saveToFile)
    {
        var dlg = new UnsavedChangesWindow(baseline, current, saveToFile) { Owner = Application.Current?.MainWindow };
        dlg.ShowDialog();
        return dlg.Result;
    }
}
