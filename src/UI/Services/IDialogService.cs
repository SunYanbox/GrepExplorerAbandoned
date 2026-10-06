namespace GrepExplorer.UI.Services;

public enum UnsavedResult
{
    SavedToFile,
    Discard,
    Cancel,
}

/// <summary>对话框服务（MVVM 分离：View/Dialog 通过服务调用）。</summary>
public interface IDialogService
{
    string? PickFolder(string start);
    /// <summary>单行文本输入；取消返回 null。</summary>
    string? PromptText(string title, string message, string initial);
    string? PickSaveFile(string defaultName, string filter);
    string? PickOpenFile(string filter);
    bool Confirm(string title, string message);
    void Info(string title, string message);
    UnsavedResult ShowUnsavedChanges(string? baseline, string current, Func<string?, string?> saveToFile);
}
