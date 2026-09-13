using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.DependencyInjection;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.Base.Extensions;

internal static class UserControlExtensions
{
    internal static async Task<IStorageFile?> GetUserSelectedFileAsync(this UserControl view, string startingFolderPath, string title, params FilePickerFileType[] filePickerTypes)
    {
        IStorageFile? userSelectedFile = null;
        TopLevel? topLevel = TopLevel.GetTopLevel(view);

        if (topLevel != null)
        {
            FilePickerOpenOptions filePickerOpenOptions = new()
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(startingFolderPath)),
                FileTypeFilter = filePickerTypes
            };

            IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(filePickerOpenOptions);

            if (files.Count > 0)
            {
                userSelectedFile = files[0];
            }
        }

        return userSelectedFile;
    }

    internal static async Task<IStorageFolder?> GetUserSelectedFolderAsync(this UserControl view, string startingFolderPath)
    {
        IStorageFolder? userSelectedFolder = null;
        TopLevel? topLevel = TopLevel.GetTopLevel(view);

        if (topLevel != null)
        {
            FolderPickerOpenOptions folderPickerOpenOptions = new()
            {
                AllowMultiple = false,
                SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(startingFolderPath))
            };

            IReadOnlyList<IStorageFolder> folders = await topLevel.StorageProvider.OpenFolderPickerAsync(folderPickerOpenOptions);

            if (folders.Count > 0)
            {
                userSelectedFolder = folders[0];
            }
        }

        return userSelectedFolder;
    }

    internal static void SetDataContext(this UserControl view, IServiceProvider? services)
    {
        if (view != null)
        {
            Assembly currentAssembly = Assembly.GetExecutingAssembly();
            string viewType = view.GetType().ToString();

            if (currentAssembly != null && !string.IsNullOrEmpty(viewType) && viewType.EndsWith("View") && viewType.Contains(".Views."))
            {
                string qualifiedViewModelPath = $"{viewType.Replace(".Views.", ".ViewModels.")}Model";
                Type? viewModelType = currentAssembly.GetType(qualifiedViewModelPath);

                if (viewModelType != null)
                {
                    view.DataContext = Design.IsDesignMode
                        ? Activator.CreateInstance(viewModelType)
                        : Ioc.Default.GetService(viewModelType);
                }
            }
        }
    }

    internal static void LoadModelEvents(this UserControl view)
    {
        if (view.DataContext is BaseViewModel viewModel)
        {
            viewModel.AddModelEvents();
        }
    }

    /// <summary>
    /// 页面切入时的过渡动画：淡入（叠加在 SukiUI 自带的页面切换过渡之上）。
    /// </summary>
    internal static void PlayPageEnterAnimation(this UserControl view)
    {
        Animation enterAnimation = new()
        {
            Duration = TimeSpan.FromMilliseconds(200),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(Avalonia.Visual.OpacityProperty, 0d) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(Avalonia.Visual.OpacityProperty, 1d) }
                }
            }
        };

        view.Loaded += (_, _) => _ = enterAnimation.RunAsync(view);
    }

    internal static void UnloadModelEvents(this UserControl view)
    {
        if (view.DataContext is BaseViewModel viewModel)
        {
            viewModel.RemoveModelEvents();
        }
    }
}
