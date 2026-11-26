using System.IO;
using Microsoft.Win32;

namespace 上位机.应用.服务;

/// <summary>
/// 文件选择对话框服务。
/// <para>把 WPF 对话框调用从 ViewModel 里隔离出来——ViewModel 不该直接引用 <c>Microsoft.Win32</c>，
/// 否则没法单测。</para>
/// </summary>
public interface 文件对话框服务接口
{
    /// <summary>选择要打开的文件。用户取消时返回 <c>null</c>。</summary>
    string? 选择打开文件(string 标题文本, string 筛选, string? 初始目录 = null);

    /// <summary>选择保存路径。用户取消时返回 <c>null</c>。</summary>
    string? 选择保存文件(string 标题文本, string 筛选, string 默认文件名称, string? 初始目录 = null);

    /// <summary>选择目录。用户取消时返回 <c>null</c>。</summary>
    string? 选择文件夹(string 标题文本, string? 初始目录 = null);
}

/// <inheritdoc />
public sealed class 文件对话框服务 : 文件对话框服务接口
{
    /// <summary>CSV 文件的通用过滤器。</summary>
    public const string CSV筛选 = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*";

    /// <inheritdoc />
    public string? 选择打开文件(string 标题文本, string 筛选, string? 初始目录 = null)
    {
        var 对话框 = new OpenFileDialog
        {
            Title = 标题文本,
            Filter = 筛选,
            CheckFileExists = true,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(初始目录) && Directory.Exists(初始目录))
        {
            对话框.InitialDirectory = 初始目录;
        }

        return 对话框.ShowDialog() == true ? 对话框.FileName : null;
    }

    /// <inheritdoc />
    public string? 选择保存文件(
        string 标题文本, string 筛选, string 默认文件名称, string? 初始目录 = null)
    {
        var 对话框 = new SaveFileDialog
        {
            Title = 标题文本,
            Filter = 筛选,
            FileName = 默认文件名称,
            OverwritePrompt = true,
        };

        if (!string.IsNullOrWhiteSpace(初始目录) && Directory.Exists(初始目录))
        {
            对话框.InitialDirectory = 初始目录;
        }

        return 对话框.ShowDialog() == true ? 对话框.FileName : null;
    }

    /// <inheritdoc />
    public string? 选择文件夹(string 标题文本, string? 初始目录 = null)
    {
        var 对话框 = new OpenFolderDialog
        {
            Title = 标题文本,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(初始目录) && Directory.Exists(初始目录))
        {
            对话框.InitialDirectory = 初始目录;
        }

        return 对话框.ShowDialog() == true ? 对话框.FolderName : null;
    }
}
