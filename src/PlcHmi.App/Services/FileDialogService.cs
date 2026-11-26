using System.IO;
using Microsoft.Win32;

namespace PlcHmi.App.Services;

/// <summary>
/// 文件选择对话框服务。
/// <para>把 WPF 对话框调用从 ViewModel 里隔离出来——ViewModel 不该直接引用 <c>Microsoft.Win32</c>，
/// 否则没法单测。</para>
/// </summary>
public interface IFileDialogService
{
    /// <summary>选择要打开的文件。用户取消时返回 <c>null</c>。</summary>
    string? SelectOpenFile(string titleText, string filter, string? initialDirectory = null);

    /// <summary>选择保存路径。用户取消时返回 <c>null</c>。</summary>
    string? SelectSaveFile(string titleText, string filter, string defaultFileName, string? initialDirectory = null);

    /// <summary>选择目录。用户取消时返回 <c>null</c>。</summary>
    string? SelectFileFixture(string titleText, string? initialDirectory = null);
}

/// <inheritdoc />
public sealed class FileDialogService : IFileDialogService
{
    /// <summary>CSV 文件的通用过滤器。</summary>
    public const string csvFilter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*";

    /// <inheritdoc />
    public string? SelectOpenFile(string titleText, string filter, string? initialDirectory = null)
    {
        var Dialog = new OpenFileDialog
        {
            Title = titleText,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            Dialog.InitialDirectory = initialDirectory;
        }

        return Dialog.ShowDialog() == true ? Dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? SelectSaveFile(
        string titleText, string filter, string defaultFileName, string? initialDirectory = null)
    {
        var Dialog = new SaveFileDialog
        {
            Title = titleText,
            Filter = filter,
            FileName = defaultFileName,
            OverwritePrompt = true,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            Dialog.InitialDirectory = initialDirectory;
        }

        return Dialog.ShowDialog() == true ? Dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? SelectFileFixture(string titleText, string? initialDirectory = null)
    {
        var Dialog = new OpenFolderDialog
        {
            Title = titleText,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            Dialog.InitialDirectory = initialDirectory;
        }

        return Dialog.ShowDialog() == true ? Dialog.FolderName : null;
    }
}
