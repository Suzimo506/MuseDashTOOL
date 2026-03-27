using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MdModManager.ViewModels;

namespace MdModManager;

/// <summary>
/// 给定视图模型，如果可能则返回对应的视图。
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    // 用于缓存特定界面的视图实例，解决滚动位置丢失和闪烁的根源问题
    private static readonly Dictionary<Type, Control> _viewCache = new();

    /// <summary>
    /// 清除视图缓存，释放内存（在离开相应业务模块时调用）
    /// </summary>
    public static void ClearCache()
    {
        _viewCache.Clear();
    }

    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var type = param.GetType();

        // 仅针对整合包界面使用缓存，以保留其滚动条等内置状态
        if (type == typeof(AlbumCollectionViewModel))
        {
            if (_viewCache.TryGetValue(type, out var cachedView))
            {
                return cachedView;
            }

            var view = CreateView(type);
            if (view != null)
            {
                _viewCache[type] = view;
                return view;
            }
        }

        return CreateView(type) ?? new TextBlock { Text = "Not Found: " + type.FullName };
    }

    private static Control? CreateView(Type viewModelType)
    {
        var name = viewModelType.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        return null;
    }

    public bool Match(object? data)
    {
        return data is CommunityToolkit.Mvvm.ComponentModel.ObservableObject;
    }
}
